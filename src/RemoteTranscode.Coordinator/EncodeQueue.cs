using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteTranscode.Common;

namespace RemoteTranscode.Coordinator;

/// <summary>
/// Pending (no slice running) ⇄ Running (at least one slice on a worker or on the server) → Assembling (every slice
/// done, the shim builds Emby's output) → Completed/Failed. Cancelled when Emby stops the conversion.
/// </summary>
public enum EncodeStatus { Pending, Running, Assembling, Completed, Failed, Cancelled }

/// <summary>An encoded chunk on disk; Start/End are in source time (run start offset already added).</summary>
public sealed record EncodeChunk(int Index, double Start, double End, long Size);

/// <summary>
/// A part of the source that one run encodes: [Start, End), the last slice to the end of the source. Slices are
/// independent, so several workers encode the same conversion at once; an interrupted slice resumes at its first
/// missing chunk. Mutated under the queue's lock only.
/// </summary>
public sealed class EncodeSlice
{
    public int Index { get; init; }
    public double Start { get; init; }
    public double End { get; init; }
    public bool Last { get; init; }
    public bool Done { get; set; }
    /// <summary>Dispatcher job of the current worker run; null while waiting or running on the server.</summary>
    public string? JobId { get; set; }
    /// <summary>Who runs this slice now (worker name, <see cref="EncodeQueue.LocalWorkerName"/>, or the offer marker); null while waiting.</summary>
    public string? Worker { get; set; }
    /// <summary>Source time where the current run started.</summary>
    public double RunStartSeconds { get; set; }
    /// <summary>First chunk number of the current run.</summary>
    public int RunStartIndex { get; set; }
    /// <summary>
    /// Whether the current run lists its chunk times from its own start rather than in source time; decided on its
    /// first chunk (see <see cref="EncodeTask.AddChunk"/>), null until then.
    /// </summary>
    public bool? RunTimesRelative { get; set; }
    /// <summary>Source time reached by the current run (progress).</summary>
    public double Position { get; set; }
    public double Speed { get; set; }

    /// <summary>Where the slice lies in the source file (bytes), for the worker to fetch it ahead; null if unknown.</summary>
    public long? ByteStart { get; set; }
    public long? ByteEnd { get; set; }

    /// <summary>
    /// Worker (id) told to fetch this slice ahead while it encodes its current one: it gets this slice next, unless it
    /// is gone or the conversion is down to its last slices (then the fastest finisher wins).
    /// </summary>
    [JsonIgnore]
    public string? ReservedFor { get; set; }

    /// <summary>How far below a frame's time this slice's runs set -ss and -t (EmbyConversionPlanner.SeekMargin).</summary>
    [JsonIgnore]
    public double SeekMargin { get; init; } = EmbyConversionPlanner.FrameMargin;
    /// <summary>The file's start as the server's ffmpeg reads it, for the workers (see <see cref="JobOffer"/>); null: not sent.</summary>
    public double? ServerFileStart { get; init; }

    /// <summary>The scheduler was woken once for the current run nearing its end, to start the worker's next slice ahead.</summary>
    [JsonIgnore]
    public bool EndingSignalled { get; set; }

    /// <summary>
    /// Runs in a row that ended without error and without encoding half a second of new video (see
    /// EncodeTask.DropIdleRun): two on the last slice mean the video ends there; three anywhere else count as a failure,
    /// so a slice never loops forever.
    /// </summary>
    [JsonIgnore]
    public int IdleRuns { get; set; }

    /// <summary>
    /// The resume point last looked up in the source (see EncodeQueue.ResumePointAsync): the time listed by the
    /// previous run, and the source frame found for it (null: none close enough, the listed time is used).
    /// </summary>
    [JsonIgnore]
    public double? ResumeListed { get; set; }

    [JsonIgnore]
    public double? ResumeFrame { get; set; }

    [JsonIgnore]
    public bool IsRunning => Worker != null;

    /// <summary>The type of machine whose run completed this slice (never its name), for the failure reports.</summary>
    [JsonIgnore]
    public string? DoneBy { get; set; }

    [JsonIgnore]
    public int FirstChunk => Index * EmbyConversionPlanner.ChunksPerSlice;
}

/// <summary>One Emby conversion handed over by the shim: its video is encoded in chunks, the shim assembles the result.</summary>
public sealed class EncodeTask
{
    public required string Id { get; init; }
    public required string Input { get; init; }
    /// <summary>Path Emby asked ffmpeg to write (the shim writes it at assembly).</summary>
    public required string Output { get; init; }
    /// <summary>Video part of Emby's command, with placeholders (see <see cref="EmbyConversionPlanner"/>).</summary>
    public required string[] ChunkTemplate { get; init; }
    /// <summary>
    /// Changed only by the scheduler loop, and only its tone mapper while <see cref="ToneMappingFinal"/> is false (see
    /// <see cref="ToneMappingDecision"/>).
    /// </summary>
    public required JobRequirements Requirements { get; set; }

    /// <summary>
    /// The HDR → SDR tone mapper of <see cref="Requirements"/> is final: a run of this conversion started (on a worker or
    /// on the server). Before that it is chosen again at every scheduling pass. Always true without HDR.
    /// </summary>
    [JsonIgnore]
    public bool ToneMappingFinal { get; set; }

    public EncodeStatus Status { get; set; } = EncodeStatus.Pending;
    /// <summary>Failed runs on the server; workers that fail are set aside instead (<see cref="FailedWorkers"/>).</summary>
    public int Attempts { get; set; }
    /// <summary>
    /// Workers (id) whose run of a slice failed: no more slices of this conversion for them. A worker can fail for its
    /// own reasons (GPU gone during a shutdown, driver): the conversion goes on elsewhere, or on the server.
    /// </summary>
    public List<string> FailedWorkers { get; set; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Error { get; set; }
    public string? Note { get; set; }
    public SourceInfo? Source { get; set; }
    public List<EncodeSlice> Slices { get; set; } = [];
    /// <summary>Mutated under the queue's lock only; enumerate it under that lock too.</summary>
    public List<EncodeChunk> Chunks { get; set; } = [];
    /// <summary>Every worker (or the server) that encoded part of this conversion, for the status page.</summary>
    public List<string> Participants { get; set; } = [];
    public double Percent { get; set; }
    public double Speed { get; set; }

    /// <summary>Audio converted ahead on the server (see <see cref="AudioAheadPlan"/>); null when not possible.</summary>
    [JsonIgnore]
    public AudioAheadPlan? AudioAhead { get; init; }

    /// <summary>State of the audio converted ahead, for the status page and the history ("prêt", "échec : ...").</summary>
    public string? Audio { get; set; }

    /// <summary>The prepared audio file and the -itsoffset that puts it in place, once checked; null otherwise.</summary>
    [JsonIgnore]
    public string? AudioFile { get; set; }

    [JsonIgnore]
    public string? AudioOffset { get; set; }

    /// <summary>The audio conversion ended (ready or not): nothing more to wait for.</summary>
    [JsonIgnore]
    public bool AudioSettled { get; set; }

    /// <summary>Until when the assembly waits for the audio converted ahead to finish (see WaitForAudio).</summary>
    [JsonIgnore]
    public DateTimeOffset? AudioWaitUntil { get; set; }

    /// <summary>Settings come from Emby's own conversion profiles.</summary>
    public string Profile => "Emby";

    /// <summary>The ffmpeg shim Emby started for this conversion; the task lives as long as it does.</summary>
    [JsonIgnore]
    public ShimConnection? Shim { get; set; }

    [JsonIgnore]
    public DateTimeOffset? WaitingSince { get; set; }

    /// <summary>Since when a waiting slice found no machine while only set-aside workers could take it (EncodeSetAsideRetryMinutes).</summary>
    [JsonIgnore]
    public DateTimeOffset? SetAsideBlockedSince { get; set; }

    /// <summary>Types of the machines that ran slices of this conversion (never their names), for the failure reports.</summary>
    [JsonIgnore]
    public List<string> MachineKinds { get; } = [];

    /// <summary>Runs, resumes, ffmpeg versions and failing runs, for the failure reports.</summary>
    [JsonIgnore]
    public ConversionDiagnosticsLog DiagnosticsLog { get; } = new();

    /// <summary>The "assembled from chunks" summary of the last hand-over to the shim, for the failure reports.</summary>
    [JsonIgnore]
    public string? AssemblySummary { get; set; }

    [JsonIgnore]
    public bool IsActive => Status is EncodeStatus.Pending or EncodeStatus.Running or EncodeStatus.Assembling;

    /// <summary>Who works on it now, or who did once it is over.</summary>
    [JsonIgnore]
    public string? Worker
    {
        get
        {
            var running = Slices.Where(s => s.IsRunning && s.Worker != EncodeQueue.OfferMarker).Select(s => s.Worker!).Distinct().ToList();
            var names = running.Count > 0 ? running : IsActive ? [] : Participants;
            return names.Count == 0 ? null : string.Join(", ", names);
        }
    }

    /// <summary>
    /// Registers a chunk of the slice's current run, in source time. Runs do not all list times the same way: with
    /// Emby's -copyts, upstream ffmpeg (workers) keeps source timestamps, while Emby's own ffmpeg (the server's local
    /// runs) counts from the run's start. A source-time end is always after the run's start, so a first chunk ending
    /// before it tells a run counting from its start. The first chunk of a run starts where the run started: the
    /// segment muxer lists it from 0 after a seek. Call under the queue's lock.
    /// </summary>
    public void AddChunk(EncodeSlice slice, int index, double start, double end, long size)
    {
        slice.RunTimesRelative ??= slice.RunStartSeconds > 0 && end < slice.RunStartSeconds;
        double offset = slice.RunTimesRelative == true ? slice.RunStartSeconds : 0;
        Chunks.RemoveAll(c => c.Index == index);
        Chunks.Add(new EncodeChunk(index, index == slice.RunStartIndex ? slice.RunStartSeconds : offset + start, offset + end, size));
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// A finished run of a slice that is not the last one ended exactly on the slice's end frame (its -t is set so), so
    /// its listed chunk times are moved by the difference. Emby's ffmpeg with -start_at_zero lists them shifted by the
    /// source's video start time (e.g. 5 ms when the video starts at 0.005 s); left as is, every slice of a conversion
    /// encoded on the server lost that much in the concat list's durations, and 26 slices made the assembled video 3
    /// frames short (seen on Ted Lasso S03E09 and Mr. Robot S03E04). The run's first chunk keeps its start, which is
    /// the run's own start. Only differences well under a frame are a shift: a run missing frames must not be hidden
    /// by stretching its chunks (the assembled length would then look right). Call under the lock.
    /// </summary>
    /// <summary>Smallest tolerance for a listing shift (see <see cref="ListingTolerance"/>).</summary>
    public const double MaxStartShift = 0.02;

    /// <summary>
    /// Largest listing shift corrected by <see cref="AlignRunEnd"/> and <see cref="ConcatDurations"/>. Emby's -r puts listed
    /// times on its own grid, so a shift can reach half a frame: 20.3 ms on a source starting at 0.062 s at 23.976 fps, just
    /// over a fixed 20 ms, and 24 slices of 20.3 ms made the video 12 frames short (issue #4). A missing frame is a whole
    /// frame, well above this. The slice's seek margin is half a frame when the frame gap is known.
    /// </summary>
    public static double ListingTolerance(EncodeSlice slice) => Math.Max(MaxStartShift, slice.SeekMargin * 1.1);

    /// <summary>
    /// The worker moved its -ss by <paramref name="correction"/> because its ffmpeg reads another file start
    /// (EmbyConversionPlanner.SeekCorrection): it listed its chunks from its own start, so they go back to the server's by
    /// the same amount. Like <see cref="AlignRunEnd"/>, the run's first chunk keeps its start. Call under the lock.
    /// </summary>
    public void ShiftRun(EncodeSlice slice, double correction)
    {
        int end = slice.FirstChunk + EmbyConversionPlanner.ChunksPerSlice;
        for (int i = 0; i < Chunks.Count; i++)
        {
            var c = Chunks[i];
            if (c.Index < slice.RunStartIndex || c.Index >= end)
                continue;
            Chunks[i] = c with { Start = c.Index == slice.RunStartIndex ? c.Start : c.Start + correction, End = c.End + correction };
        }
    }

    /// <summary>
    /// Where the source's first frame lies from the file's start (the first slice starts on it), for the assembly
    /// (EmbyConversionPlanner.WithVideoOffset); null at the file's start. Microseconds, as ffprobe prints them.
    /// </summary>
    public string? AssemblyVideoOffset() =>
        Slices.OrderBy(s => s.Index).FirstOrDefault()?.Start is >= 0.0005 and var start
            ? start.ToString("0.000000", CultureInfo.InvariantCulture)
            : null;

    /// <summary>Time of one output frame when Emby's command sets the frame rate (-r): chunks then hold whole frames. Null otherwise.</summary>
    public double? OutputFrameTime()
    {
        for (int i = 0; i + 1 < ChunkTemplate.Length; i++)
        {
            if (ChunkTemplate[i] != "-r" && !ChunkTemplate[i].StartsWith("-r:", StringComparison.Ordinal))
                continue;
            string[] parts = ChunkTemplate[i + 1].Split('/');
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double rate))
                return null;
            if (parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double den) && den > 0)
                rate /= den;
            return rate > 0 ? 1 / rate : null;
        }
        return null;
    }

    public void AlignRunEnd(EncodeSlice slice)
    {
        if (slice.Last)
            return;
        double shift = slice.End - EncodedEnd(slice);
        if (shift == 0 || Math.Abs(shift) > ListingTolerance(slice))
            return;
        int last = slice.FirstChunk + ContiguousChunks(slice);
        for (int i = 0; i < Chunks.Count; i++)
        {
            var c = Chunks[i];
            if (c.Index < slice.RunStartIndex || c.Index >= last)
                continue;
            Chunks[i] = c with { Start = c.Index == slice.RunStartIndex ? c.Start : c.Start + shift, End = c.End + shift };
        }
    }

    /// <summary>Less new video than this (seconds) from a run that did not complete its slice makes it an idle run.</summary>
    public const double MinRunProgressSeconds = 0.5;

    /// <summary>
    /// The slice's current run, which did not complete it, moved its encoded end by less than MinRunProgressSeconds:
    /// nothing at all, or a chunk of a frame or two. Its chunks are dropped (a run resumed on the very last frame of a
    /// video that stops before the file's end gives a one-frame chunk of a frame already encoded, and each such run
    /// would add another copy: seen with worker 0.7.15 on 2026-11-01) and true is returned. Call once per run end,
    /// before the next run is claimed.
    /// </summary>
    public bool DropIdleRun(EncodeSlice slice)
    {
        if (EncodedEnd(slice) >= slice.RunStartSeconds + MinRunProgressSeconds)
            return false;
        Chunks.RemoveAll(c => c.Index >= slice.RunStartIndex && c.Index < slice.FirstChunk + EmbyConversionPlanner.ChunksPerSlice);
        return true;
    }

    /// <summary>
    /// The last slice resumed after its chunks and, twice in a row, ffmpeg ended normally without new video to encode:
    /// the video stream ends there, before the file's duration (the audio runs on, e.g. a 4K Dolby Vision episode whose
    /// video stops 34 s before its audio). Its chunks are complete; the assembled file keeps the source's audio, and the
    /// shim's check compares the video spans. Seen on 2026-11-01: 0.7.25 took each such run for an interruption and
    /// restarted the slice every 2 s for an hour.
    /// </summary>
    public bool EndsAtVideoEnd(EncodeSlice slice) =>
        slice.Last && slice.IdleRuns >= 2 && slice.RunStartIndex > slice.FirstChunk
        && ContiguousChunks(slice) == slice.RunStartIndex - slice.FirstChunk && Consistent(slice);

    /// <summary>
    /// The last slice's run was stopped once it had encoded everything (a worker became available just as the server's run
    /// ended): its chunks reach past the last frame, so the source's nearest frame to their end is that last frame, one
    /// frame before them. Resuming there would encode it again and the assembled video would hold it twice (seen on the
    /// bench, 16784 frames for 16783 and two packets at the same time). A listing shift is at most half a frame, so three
    /// quarters of a frame tells them apart; only within a second of the file's end, as a hole in the video looks the same.
    /// </summary>
    public bool ResumePastLastFrame(EncodeSlice slice, double listed, double frame)
    {
        if (!slice.Last || Source is not { } source || listed < source.DurationSeconds - 1)
            return false;
        double threshold = slice.SeekMargin > EmbyConversionPlanner.FrameMargin ? slice.SeekMargin * 1.5 : MaxStartShift;
        return listed - frame > threshold;
    }

    /// <summary>Chunks of the slice from its first one without gap: where its next run resumes.</summary>
    public int ContiguousChunks(EncodeSlice slice)
    {
        int n = 0;
        foreach (var c in Chunks.Where(c => c.Index >= slice.FirstChunk && c.Index < slice.FirstChunk + EmbyConversionPlanner.ChunksPerSlice).OrderBy(c => c.Index))
        {
            if (c.Index != slice.FirstChunk + n)
                break;
            n++;
        }
        return n;
    }

    /// <summary>Source time up to which the slice is encoded without gap.</summary>
    public double EncodedEnd(EncodeSlice slice)
    {
        int n = ContiguousChunks(slice);
        return n == 0 ? slice.Start : Chunks.Where(c => c.Index >= slice.FirstChunk && c.Index < slice.FirstChunk + n).Max(c => c.End);
    }

    /// <summary>
    /// The slice's chunks reach its end, and not beyond it: a run that overran its end has frames the next slice also
    /// has, and the assembled video would repeat them (out of sync with the audio from there on). Chunk times come from
    /// the segment list, so a small tolerance is allowed. The last slice must reach the end of the source within a few
    /// seconds (the file's duration may be the audio's, a little longer): a run that ended without error but whose
    /// chunks were not all stored must not pass. A video ending well before the audio fails here and the conversion
    /// goes back to Emby's own ffmpeg, which is safe.
    /// </summary>
    public bool Covers(EncodeSlice slice) =>
        ContiguousChunks(slice) > 0 && Consistent(slice)
        && (slice.Last
            ? EncodedEnd(slice) >= Source!.DurationSeconds - 3.0
            : EncodedEnd(slice) >= slice.End - 1.0 && EncodedEnd(slice) <= slice.End + 0.5);

    /// <summary>
    /// The slice's chunks follow each other inside the slice: none starts before the slice or before the previous one
    /// ended. Anything else (times misread, a run that started elsewhere) would repeat or skip frames once assembled.
    /// </summary>
    public bool Consistent(EncodeSlice slice)
    {
        int n = ContiguousChunks(slice);
        double previousEnd = slice.Start;
        foreach (var c in Chunks.Where(c => c.Index >= slice.FirstChunk && c.Index < slice.FirstChunk + n).OrderBy(c => c.Index))
        {
            if (c.Start < previousEnd - 0.5 || c.End <= c.Start)
                return false;
            previousEnd = c.End;
        }
        return true;
    }

    /// <summary>
    /// The length of each chunk of a done slice, for the concat list's duration directives. One run's chunks follow each
    /// other frame after frame, so a chunk lasts up to the next one's start (the time of its first frame). Its listed
    /// end is not used: ffmpeg's segment muxer takes it from the packet durations, and an end listed past the next
    /// chunk's start adds to the assembled length at every chunk (suspected on an AV1 episode, Mr. Robot S03E09, encoded
    /// by a worker and the server, 12 to 14 frames long; its server slices had many small chunks). A new run starts where
    /// the previous one ended (its first chunk starts at the run's start), so this holds across runs too. The slice's
    /// last chunk lasts up to the slice's end when its listed end is within a listing shift of it
    /// (<see cref="ListingTolerance"/>): each slice then lasts exactly its length, whatever time base its run listed in.
    /// Further off is left as listed: missing frames must show in the check. The last slice has no end frame to align on,
    /// so a listing shift stayed in the length of a run's first chunk (54 ms between two frames at 25 fps and the rest of
    /// the slice 14 ms late, seen on 2026-11-02): with Emby's -r its chunks hold whole frames, so their lengths are rounded
    /// to whole frames, within the same tolerance. Call under the lock.
    /// </summary>
    public List<(int Index, double Duration)> ConcatDurations(EncodeSlice slice)
    {
        int n = ContiguousChunks(slice);
        var chunks = Chunks.Where(c => c.Index >= slice.FirstChunk && c.Index < slice.FirstChunk + n).OrderBy(c => c.Index).ToList();
        var durations = new List<(int, double)>(chunks.Count);
        double? frameTime = OutputFrameTime();
        for (int i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            double end = c.End;
            if (i + 1 < chunks.Count)
            {
                if (chunks[i + 1].Start > c.Start)
                    end = chunks[i + 1].Start;
            }
            else if (!slice.Last && Math.Abs(slice.End - c.End) <= ListingTolerance(slice))
                end = slice.End;
            double duration = end - c.Start;
            if (slice.Last && i + 1 < chunks.Count && frameTime is > 0 and var ft)
            {
                double whole = Math.Round(duration / ft) * ft;
                if (whole > 0 && Math.Abs(whole - duration) <= ListingTolerance(slice))
                    duration = whole;
            }
            durations.Add((c.Index, duration));
        }
        return durations;
    }

    /// <summary>
    /// Source seconds encoded so far: done slices, the running ones up to where they are, the waiting ones up to their
    /// last chunk. Call under the queue's lock.
    /// </summary>
    public double CoveredSeconds()
    {
        if (Source is not { DurationSeconds: > 0 } source)
            return 0;
        double covered = 0;
        foreach (var s in Slices)
        {
            double end = s.Last ? source.DurationSeconds : s.End;
            covered += s.Done ? end - s.Start
                : s.IsRunning ? Math.Clamp(s.Position, s.Start, end) - s.Start
                : EncodedEnd(s) - s.Start;
        }
        return covered;
    }

    /// <summary>
    /// Estimated seconds to the end of the encode at the current combined speed (source seconds per second); null
    /// when nothing runs. Call under the queue's lock.
    /// </summary>
    public double? RemainingSeconds()
    {
        double speed = Slices.Where(s => s.IsRunning).Sum(s => s.Speed);
        if (Status != EncodeStatus.Running || speed <= 0.01 || Source is not { DurationSeconds: > 0 } source)
            return null;
        return Math.Max(0, source.DurationSeconds - CoveredSeconds()) / speed;
    }

    /// <summary>Running while any slice runs, pending otherwise; other states are left alone.</summary>
    public void RefreshStatus()
    {
        if (Status is EncodeStatus.Pending or EncodeStatus.Running)
            Status = Slices.Any(s => s.IsRunning) ? EncodeStatus.Running : EncodeStatus.Pending;
    }
}

/// <summary>
/// Emby's conversions (Convert, downloads), captured by the ffmpeg shim. Emby keeps deciding everything (quality
/// profile, container, destination); only the video encode moves. The source is cut into slices that the available
/// workers encode in parallel (as many as each has free slots), in chunks kept on the server. An interrupted
/// slice (owner reclaimed the PC, worker lost, preempted by a playback) resumes at its first missing chunk, on another
/// worker or, when no worker is left, on the server itself. When every slice is done the shim assembles Emby's output
/// with Emby's own command and verifies it; a conversion that cannot be finished remotely goes back to Emby's original
/// command on the server.
/// </summary>
public sealed partial class EncodeQueue : IJobOwner
{
    private readonly CoordinatorOptions options;
    private readonly Dispatcher dispatcher;
    private readonly ServerMedia media;
    private readonly ILogger<EncodeQueue> logger;
    private readonly FailureReporter reporter;
    // Guards the task list, every task's Status, slices and chunks, and the encodes.json file.
    private readonly object gate = new();
    private readonly List<EncodeTask> tasks;
    // Counts wake-up requests for the scheduler loop; extra releases are drained before each pass.
    private readonly SemaphoreSlim wake = new(0);
    private readonly string spool;
    // At most one local ffmpeg at a time. Set by the scheduler loop, cleared by SuperviseLocalAsync (under gate).
    private LocalRun? localRun;
    // Measured conversion speed of each worker (id → source seconds per second, smoothed), for the end of conversions.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> workerSpeeds = new();
    // When the coordinator started (this singleton is built at start-up): workers reconnect from then on, see
    // EncodeToneMapperGraceSeconds.
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;

    /// <summary>A slice encode running on the server itself because no worker was available any more.</summary>
    private sealed class LocalRun
    {
        public required EncodeTask Task { get; init; }
        public required EncodeSlice Slice { get; init; }
        public required Process Process { get; init; }
        public required string ListFile { get; init; }
        public required JobRequirements Requirements { get; init; }
        /// <summary>Lines of the CSV segment list already turned into chunks.</summary>
        public int RegisteredLines { get; set; }
        /// <summary>Set when the local run was asked to stop so a worker can continue: its end is not a failure.</summary>
        public bool HandOver { get; set; }
        public Queue<string> ErrorTail { get; } = new();
    }

    public const string LocalWorkerName = "NAS (local)";

    /// <summary>Slice claimed by the scheduler while its offer is in flight (no job id yet).</summary>
    public const string OfferMarker = "…";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public EncodeQueue(CoordinatorOptions options, Dispatcher dispatcher, ServerMedia media, ILogger<EncodeQueue> logger, FailureReporter reporter)
    {
        this.options = options;
        this.reporter = reporter;
        this.dispatcher = dispatcher;
        this.media = media;
        this.logger = logger;
        spool = Path.IsPathRooted(options.EncodeSpoolDir) ? options.EncodeSpoolDir : Path.Combine(Path.GetDirectoryName(options.DataDir)!, options.EncodeSpoolDir);
        try
        {
            tasks = File.Exists(options.EncodeQueuePath)
                ? JsonSerializer.Deserialize<List<EncodeTask>>(File.ReadAllText(options.EncodeQueuePath), Json) ?? []
                : [];
        }
        catch (JsonException)
        {
            tasks = [];   // history of an older format: not needed to run
        }
        // A rejected assembly's spool kept for analysis (KeepFailedSpool): only the latest one survives a restart.
        keptSpool = tasks.Where(t => t.Status == EncodeStatus.Failed && Directory.Exists(Path.Combine(spool, t.Id)))
            .OrderByDescending(t => t.UpdatedAt).FirstOrDefault()?.Id;
        foreach (var t in tasks.Where(t => !t.IsActive && t.Id != keptSpool && Directory.Exists(Path.Combine(spool, t.Id))))
        {
            try { Directory.Delete(Path.Combine(spool, t.Id), true); } catch (IOException) { }
        }
        // A conversion belongs to the ffmpeg process Emby started; after a coordinator restart that process has already
        // fallen back to Emby's own ffmpeg, so unfinished tasks are closed and their chunks removed.
        foreach (var t in tasks.Where(t => t.IsActive))
        {
            t.Status = EncodeStatus.Cancelled;
            t.Note = "coordinateur redémarré : Emby a terminé la conversion sur le serveur";
            foreach (var s in t.Slices)
            {
                s.Worker = null;
                s.JobId = null;
            }
            DeleteSpool(t.Id);
        }
        // Not saved here: the file is rewritten with these states at the next change.
        dispatcher.CapacityChanged += () => Wake();
    }

    /// <summary>A copy of the list, but the tasks themselves are live objects that other threads keep changing.</summary>
    public IReadOnlyList<EncodeTask> Snapshot()
    {
        lock (gate)
            return tasks.OrderByDescending(t => t.CreatedAt).ToList();
    }

    /// <summary>
    /// JSON summaries (<see cref="EncodeCli.Summary"/>) built under the lock: they enumerate each task's chunk list,
    /// which uploads and the local run change concurrently.
    /// </summary>
    public IReadOnlyList<object> Summaries(int max = int.MaxValue)
    {
        lock (gate)
            return tasks.OrderByDescending(t => t.CreatedAt).Take(max).Select(EncodeCli.Summary).ToList();
    }

    /// <summary>Asks the scheduler loop for a pass now instead of at its 10-second tick.</summary>
    public void Wake() => wake.Release();

    // ---- Shim side ------------------------------------------------------------------------------------

    /// <summary>
    /// Accepts an Emby conversion when a connected worker can run it, even if that worker is busy right now, or when the
    /// server can encode its slices itself (<c>EncodeLocalFallback</c>): the server then starts alone (after
    /// <c>EncodeLocalDelaySeconds</c>) and every scheduling pass offers the waiting slices to the workers again, so a
    /// worker connecting later takes part. Only when neither can run it does Emby convert on the server as it always
    /// did (the shim runs the original command).
    /// </summary>
    public async Task<Message> StartConversionAsync(ConversionPlan plan, ShimConnection shim, CancellationToken token)
    {
        // A paired worker that is not connected right now (PC off, or Emby just restarted and the workers are
        // reconnecting) may come: the conversion waits for one up to EncodeNoWorkerMinutes, then goes back to Emby.
        if (!dispatcher.HasWorkerFor(plan.Requirements!) && !await ServerCanEncodeAsync(plan.Requirements!, token)
            && dispatcher.Store?.Load().Any(w => !w.Revoked) != true)
            return new ShimRejected("ni worker ni serveur capables de cette conversion par tranches");
        SourceInfo source;
        try
        {
            source = await media.ProbeAsync(plan.Input, token);
        }
        catch (Exception ex)
        {
            return new ShimRejected($"analyse de la source impossible : {ex.Message}");
        }
        var task = new EncodeTask
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Input = plan.Input,
            Output = plan.Output,
            ChunkTemplate = plan.ChunkTemplate,
            // HDR → SDR: on the GPU (libplacebo) when a connected worker validated it, and then only such workers take
            // its slices (never the server, whose ffmpeg has no libplacebo); otherwise zscale everywhere. Never both in
            // one file: the renderings differ slightly. Provisional until the first run starts (DecideToneMapping):
            // after an Emby restart the conversion arrives before the workers have reconnected.
            Requirements = WorkerMatching.WithToneMapping(plan.Requirements!, dispatcher.ConnectedCapabilities()),
            ToneMappingFinal = !EmbyFilters.NeedsToneMapping(plan.Requirements!.Filters),
            Source = source,
            Shim = shim,
            Slices = await SlicesAsync(plan.Input, source.DurationSeconds, token),
            AudioAhead = options.EncodeAudioAhead ? plan.AudioAhead : null,
            Audio = !options.EncodeAudioAhead ? null : plan.AudioAhead is null ? $"converti à l'assemblage ({plan.AudioAheadReason})" : "en attente",
        };
        task.DiagnosticsLog.AssemblyTemplate = plan.AssemblyTemplate;
        lock (gate)
        {
            tasks.Add(task);
            // Keep the history short.
            tasks.RemoveAll(t => !t.IsActive && DateTimeOffset.UtcNow - t.UpdatedAt > TimeSpan.FromDays(7));
            Save();
        }
        logger.LogInformation("Conversion {Id} captured ({Slices} slices): {Input} -> {Output}", task.Id, task.Slices.Count, plan.Input, plan.Output);
        if (options.EncodeAudioAhead && plan.AudioAhead is null)
            logger.LogInformation("Conversion {Id}: audio converted at assembly ({Reason})", task.Id, plan.AudioAheadReason);
        if (EmbyFilters.NeedsToneMapping(task.Requirements.Filters))
            logger.LogInformation("Conversion {Id}: HDR to SDR with {ToneMapper} for now, decided at first slice", task.Id,
                ToneMapperName(task.Requirements.ToneMapping));
        Wake();
        return new ShimAssigned(task.Id, "workers");
    }

    /// <summary>
    /// The slices of a source, each boundary moved onto the first video frame at or after its nominal time: runs are
    /// cut on frames exactly (<see cref="EmbyConversionPlanner.ChunkArgs"/>). A boundary that cannot be read is left
    /// out: its two slices become one (seen on a 4K HDR episode whose frame 30 s before the end was unreadable, which
    /// used to keep the whole episode in one slice on one machine). Only when most boundaries are unreadable (the
    /// analysis timed out) does the conversion stay in one slice, which needs no boundary.
    /// </summary>
    private async Task<List<EncodeSlice>> SlicesAsync(string input, double duration, CancellationToken token)
    {
        var nominal = EmbyConversionPlanner.Slices(duration);
        ServerMedia.Boundary?[] frames;
        // Bounded: the shim waits for the answer (and falls back to Emby's ffmpeg after its submit timeout).
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                frames = await media.BoundariesAsync(input, nominal.Select(n => n.Start).ToList(), limit.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                frames = new ServerMedia.Boundary?[nominal.Count];
            }
        }
        var single = new List<EncodeSlice> { new() { Index = 0, Start = frames[0]?.Time ?? 0, End = duration, Last = true } };
        var bounds = new List<ServerMedia.Boundary> { frames[0] ?? new ServerMedia.Boundary(0, 0, 0) };
        int skipped = 0;
        for (int i = 1; i < nominal.Count; i++)
        {
            if (frames[i] is not { } f || f.Time <= bounds[^1].Time + 1 || f.Time >= duration - 1)
            {
                skipped++;
                logger.LogWarning("Cannot read the frame at {Time:0.###}s of {Input}: boundary left out, its two slices merged", nominal[i].Start, input);
                continue;
            }
            bounds.Add(f);
        }
        if (nominal.Count > 1 && skipped * 2 > nominal.Count - 1)
        {
            logger.LogWarning("{Skipped} of {Count} boundaries of {Input} unreadable: converted as a single slice", skipped, nominal.Count - 1, input);
            return single;
        }
        // Byte ranges for fetching ahead, with margins: a run reads from the keyframe before its start, and audio
        // packets interleaved around the boundaries. The first slice also needs the file's header.
        const long Margin = 4 << 20;
        double seekMargin = options.EncodeSeekMarginSeconds ?? EmbyConversionPlanner.SeekMargin(frames.FirstOrDefault(f => f != null)?.SmallestFrameGap);
        return bounds.Select((b, i) => new EncodeSlice
        {
            Index = i,
            SeekMargin = seekMargin,
            // The bench sets its own margin to provoke a skipped frame: nothing must correct it then.
            ServerFileStart = options.EncodeSeekMarginSeconds is null ? b.FileStart : null,
            Start = b.Time,
            End = i + 1 < bounds.Count ? bounds[i + 1].Time : duration,
            Last = i + 1 == bounds.Count,
            ByteStart = i == 0 ? 0 : b.KeyframePosition is { } k ? Math.Max(0, k - Margin) : null,
            ByteEnd = i + 1 < bounds.Count ? bounds[i + 1].Position is { } e ? e + Margin : null : long.MaxValue,
        }).ToList();
    }

    /// <summary>Emby stopped the conversion (its ffmpeg got "q" or was killed).</summary>
    public async Task CancelAsync(string id, string reason)
    {
        EncodeTask? task;
        List<Job> jobs;
        lock (gate)
        {
            task = tasks.FirstOrDefault(t => t.Id == id);
            if (task is null || !task.IsActive)
                return;
            jobs = DetachRuns(task);
            task.Status = EncodeStatus.Cancelled;
            Touch(task, reason);
        }
        foreach (var job in jobs)
            await dispatcher.StopJobAsync(job, "conversion cancelled");
        DeleteSpool(task.Id);
    }

    /// <summary>
    /// Under the lock: forgets every run of the task (its slices go back to waiting) and kills its local run. Returns
    /// the worker jobs, which the caller stops outside the lock.
    /// </summary>
    private List<Job> DetachRuns(EncodeTask task)
    {
        var jobs = new List<Job>();
        foreach (var s in task.Slices)
        {
            if (s.JobId != null && dispatcher.FindJob(s.JobId) is { } job)
                jobs.Add(job);
            s.JobId = null;
            s.Worker = null;
        }
        if (localRun?.Task == task)
            KillLocal(localRun);
        if (audioRun?.Task == task)
            KillAudio(audioRun, "conversion arrêtée");
        return jobs;
    }

    public async Task OnShimClosedAsync(ShimConnection shim)
    {
        List<EncodeTask> owned;
        lock (gate)
            owned = tasks.Where(t => t.Shim == shim && t.IsActive).ToList();
        foreach (var t in owned)
        {
            // While assembling, the shim does not read its connection for minutes and it may drop: the shim then sends
            // its verdict over a new connection. Cancelling at once ended good conversions as cancelled; the verdict
            // gets ShimVerdictGraceSeconds, and only a task still assembling after that was really stopped.
            if (t.Status == EncodeStatus.Assembling)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(ShimVerdictGraceSeconds));
                    bool stillAssembling;
                    lock (gate)
                        stillAssembling = t.Status == EncodeStatus.Assembling && t.Shim == shim;
                    if (stillAssembling)
                        await CancelAsync(t.Id, "conversion arrêtée par Emby");
                });
                continue;
            }
            await CancelAsync(t.Id, "conversion arrêtée par Emby");
        }
    }

    /// <summary>How long an assembly whose shim connection closed waits for the shim's verdict on a new connection.</summary>
    private const int ShimVerdictGraceSeconds = 120;

    /// <summary>The shim finished assembling (and verifying) Emby's output; the chunks are no longer needed.</summary>
    public void OnConversionDone(ConversionDone done)
    {
        EncodeTask? task;
        lock (gate)
        {
            task = tasks.FirstOrDefault(t => t.Id == done.TaskId);
            if (task is null)
                return;
            task.Status = done.Success ? EncodeStatus.Completed : EncodeStatus.Failed;
            task.Error = done.Error;
            if (audioRun?.Task == task)
                KillAudio(audioRun, "conversion terminée");
            task.Percent = done.Success ? 100 : task.Percent;
            task.Speed = 0;
            Touch(task, done.Success ? "assemblé et vérifié par le serveur" : "échec de l'assemblage");
            if (done.Success)
                DeleteSpool(task.Id);
            else
                KeepFailedSpool(task);
        }
        // "q reçu": Emby cancelled during the assembly, nothing failed.
        if (!done.Success && done.Error != "q reçu")
            ReportFailure(task, FailureKind.AssemblyRejected, done.Error ?? "assemblage rejeté", check: done.Check);
    }

    // ---- Scheduling ---------------------------------------------------------------------------------

    /// <summary>
    /// The scheduler loop: the only caller of ScheduleAsync, so scheduling passes never overlap. Runs every 10 s or
    /// when woken; ends with the host.
    /// </summary>
    public async Task RunAsync(CancellationToken token)
    {
        using var stopDiagnostics = token.Register(diagnosticsStop.Cancel);
        while (!token.IsCancellationRequested)
        {
            await wake.WaitAsync(TimeSpan.FromSeconds(10), token).ContinueWith(_ => { });
            while (wake.CurrentCount > 0)
                await wake.WaitAsync(token);
            try
            {
                await ScheduleAsync(token);
                await BroadcastQueueAsync();
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                logger.LogError(ex, "Encode scheduling failed");
            }
        }
    }

    private async Task ScheduleAsync(CancellationToken token)
    {
        // A worker is back: stop the slow local encode at a chunk boundary and let the worker continue from there.
        if (localRun is { HandOver: false } run && ShouldHandOver(run))
        {
            run.HandOver = true;
            logger.LogInformation("Encode {Id}: a worker is available, stopping the local encode to hand over", run.Task.Id);
            // "q" makes ffmpeg close the current segment cleanly; it is listed like any other chunk and the worker
            // resumes from its end. SuperviseLocalAsync puts the slice back to waiting once the process has exited.
            try
            {
                run.Process.StandardInput.Write('q');
                run.Process.StandardInput.Flush();
            }
            catch (Exception) { }
        }

        List<EncodeTask> active;
        lock (gate)
            active = tasks.Where(t => t.Status is EncodeStatus.Pending or EncodeStatus.Running).OrderBy(t => t.CreatedAt).ToList();

        // The audio of the oldest conversion whose audio is still to convert, one at a time.
        if (active.FirstOrDefault(t => t.AudioAhead != null && !t.AudioSettled && t.Shim is { IsOpen: true }) is { } next)
            await TryStartAudioAsync(next, token);

        foreach (var task in active)
        {
            if (task.Shim is not { IsOpen: true })
            {
                await CancelAsync(task.Id, "conversion arrêtée par Emby");
                continue;
            }
            bool complete;
            lock (gate)
                complete = task.Status == EncodeStatus.Pending && task.Slices.All(s => s.Done);
            if (complete)
            {
                // Its audio is about to be ready: worth a few seconds, the assembly then only copies streams.
                if (WaitForAudio(task))
                    continue;
                await HandToShimAsync(task);
                continue;
            }
            long inputSize;
            try
            {
                inputSize = new FileInfo(task.Input).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Source moved or deleted: fail this task only, the rest of the pass goes on.
                await GiveUpAsync(task, $"source inaccessible : {ex.Message}", report: false);
                continue;
            }
            // HDR → SDR not final yet: chosen again from the workers connected now, or nothing starts yet.
            if (!DecideToneMapping(task))
                continue;
            // Waiting slices in order, each to a free worker, until no worker takes one.
            List<EncodeSlice> waiting;
            lock (gate)
                waiting = task.Slices.Where(s => !s.Done && !s.IsRunning).OrderBy(s => s.Index).ToList();
            foreach (var slice in waiting)
            {
                if (!await TryStartSliceAsync(task, slice, inputSize, token))
                    break;
            }
        }
    }

    /// <summary>
    /// While no run of an HDR conversion has started, its tone mapper is chosen again (<see cref="ToneMappingDecision"/>)
    /// from the workers connected now; false when nothing of it may start yet (grace period after the coordinator's
    /// start, see EncodeToneMapperGraceSeconds). Only the scheduler loop changes <see cref="EncodeTask.Requirements"/>,
    /// and it plans (PlanFor) and claims slices in the same pass, so every run of a conversion gets the same mode.
    /// </summary>
    private bool DecideToneMapping(EncodeTask task)
    {
        lock (gate)
        {
            if (task.ToneMappingFinal)
                return true;
            // Safety net: something of it exists already (a run's chunks, even of a run that ended before its offer
            // was answered): its mode is the conversion's.
            if (task.Slices.Any(s => s.IsRunning || s.Done) || task.Chunks.Count > 0)
            {
                FreezeToneMapping(task, "chunks already there");
                return true;
            }
        }
        var outcome = ToneMappingDecision.Decide(task.Requirements, dispatcher.ConnectedCapabilities(), DateTimeOffset.UtcNow - startedAt,
            TimeSpan.FromSeconds(Math.Max(0, options.EncodeToneMapperGraceSeconds)), PairedWorkersMissing);
        lock (gate)
        {
            if (task.ToneMappingFinal || !task.IsActive)
                return true;
            if (outcome.Requirements.ToneMapping != task.Requirements.ToneMapping)
            {
                logger.LogInformation("Conversion {Id}: HDR to SDR with {ToneMapper} for now (workers changed), decided at first slice",
                    task.Id, ToneMapperName(outcome.Requirements.ToneMapping));
                task.Requirements = outcome.Requirements;
            }
            if (!outcome.Wait)
                return true;
            const string Waiting = "en attente des workers (redémarrage) : tone mapping HDR choisi au démarrage de la première tranche";
            if (task.Note != Waiting)
            {
                logger.LogInformation("Conversion {Id}: no GPU tone mapping worker connected yet, waiting up to {Grace}s after start-up before zscale",
                    task.Id, options.EncodeToneMapperGraceSeconds);
                Touch(task, Waiting);
            }
            return false;
        }
    }

    /// <summary>Some paired, not revoked worker is not connected now (it may be reconnecting).</summary>
    private bool PairedWorkersMissing()
    {
        try
        {
            var connected = dispatcher.Workers.Where(w => w.IsOpen && w.Hello != null).Select(w => w.Identity.Id).ToHashSet();
            return dispatcher.Store?.Load().Any(w => !w.Revoked && !connected.Contains(w.Id)) == true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return true;   // unknown: the grace period is short anyway
        }
    }

    /// <summary>The first run of the conversion starts: its tone mapper is kept for every slice. Call under the lock.</summary>
    private void FreezeToneMapping(EncodeTask task, string machine)
    {
        if (task.ToneMappingFinal)
            return;
        task.ToneMappingFinal = true;
        if (EmbyFilters.NeedsToneMapping(task.Requirements.Filters))
            logger.LogInformation("Conversion {Id}: HDR to SDR with {ToneMapper}, decided at first slice ({Machine}, {Seconds:0}s after capture)",
                task.Id, ToneMapperName(task.Requirements.ToneMapping), machine, (DateTimeOffset.UtcNow - task.CreatedAt).TotalSeconds);
    }

    private static string ToneMapperName(ToneMapper mode) =>
        mode == ToneMapper.Libplacebo ? "libplacebo (GPU, capable workers only)" : "zscale (CPU)";

    /// <summary>
    /// Whether the local run should stop so a free worker continues its slice. Alongside workers, only once nothing of
    /// its conversion is left waiting: until then a free worker takes a waiting slice and the server keeps its own.
    /// </summary>
    private bool ShouldHandOver(LocalRun run)
    {
        if (!options.EncodeLocalAlongsideWorkers)
            return dispatcher.HasCapacityFor(run.Requirements);
        List<string> busy;
        lock (gate)
        {
            if (run.Task.Slices.Any(s => !s.Done && !s.IsRunning))
                return false;
            busy = [.. BusyWorkers(run.Task).Keys];
        }
        return dispatcher.HasCapacityFor(run.Requirements, excluded: busy);
    }

    /// <summary>
    /// Offers one waiting slice to a worker; false when no worker took it (no point offering the next one). True also
    /// when the slice is skipped for now: reserved for a worker still busy with its current slice.
    /// </summary>
    private async Task<bool> TryStartSliceAsync(EncodeTask task, EncodeSlice slice, long inputSize, CancellationToken token)
    {
        JobPlan plan;
        int startIndex;
        double startSeconds;
        List<string> avoid;
        EncodeSlice? next;
        var resume = await ResumePointAsync(task, slice, token);
        lock (gate)
        {
            if (!task.IsActive || task.Status == EncodeStatus.Assembling || slice.Done || slice.IsRunning)
                return true;
            var busy = BusyWorkers(task);
            var ending = EndingWorkers(task);
            // Without the workers refused conversions: a slice reserved for one (it came back with an older version) is released.
            var connected = dispatcher.Workers.Where(w => w.IsOpen && w.Hello != null && w.ConversionsRefused is null).Select(w => w.Identity.Id).ToHashSet();
            if (slice.ReservedFor is { } reservee)
            {
                bool othersLeft = task.Slices.Any(s => s != slice && !s.Done && !s.IsRunning && s.ReservedFor is null);
                if (!connected.Contains(reservee) || !othersLeft || task.FailedWorkers.Contains(reservee))
                    slice.ReservedFor = null;   // gone, or the end of the conversion: the fastest finisher takes it
                else if (busy.ContainsKey(reservee) && !ending.Contains(reservee))
                    return true;                // its worker is still on its current slice and already fetching this one
            }
            // One slice per worker and conversion, plus the next one started ahead when the current one is ending (a
            // worker that has a slice reserved starts that one); a reserved slice goes to its worker only.
            var reservees = task.Slices.Where(s => !s.Done && !s.IsRunning && s.ReservedFor != null).Select(s => s.ReservedFor!).ToHashSet();
            avoid = slice.ReservedFor is { } only
                ? connected.Where(id => id != only).ToList()
                : [.. busy.Keys.Where(id => !ending.Contains(id) || reservees.Contains(id)), .. SlowerThanTheBusy(task, slice, busy, connected), .. task.FailedWorkers];
            // Chunks that do not follow each other cannot be resumed from: the slice starts over.
            if (!task.Consistent(slice))
                task.Chunks.RemoveAll(c => c.Index >= slice.FirstChunk && c.Index < slice.FirstChunk + EmbyConversionPlanner.ChunksPerSlice);
            startIndex = slice.FirstChunk + task.ContiguousChunks(slice);
            startSeconds = task.EncodedEnd(slice);
            // Resumed from the source frame where the previous run ended, not from its listed time (still the same).
            if (startIndex > slice.FirstChunk && resume is { } r && r.Listed == startSeconds)
                startSeconds = r.Frame;
            // The previous run reached the slice's end frame before it was stopped (a hand-over asked just as it
            // finished) or lost: nothing is left to encode. A new run would still encode one frame (its -t cannot be
            // 0), the next slice's first one, and the assembled video would show it twice, 1 frame late from there on.
            if (!slice.Last && startIndex > slice.FirstChunk && startSeconds >= slice.End - EmbyConversionPlanner.FrameMargin)
            {
                slice.Done = true;
                slice.ReservedFor = null;
                task.RefreshStatus();
                Touch(task, $"tranche {slice.Index + 1}/{task.Slices.Count} terminée (fin atteinte avant l'arrêt du run précédent)");
                Wake();
                return true;
            }
            plan = PlanFor(task, slice, startIndex, startSeconds, inputSize);
            // The slice this worker should take next: fetched ahead while this one is encoded.
            next = task.Slices.Where(s => s.Index > slice.Index && !s.Done && !s.IsRunning && s.ReservedFor is null && s.ByteStart != null && s.ByteEnd != null)
                .OrderBy(s => s.Index).FirstOrDefault();
            if (next != null)
                plan = plan with { Prefetch = [new ByteRange(next.ByteStart!.Value, Math.Min(next.ByteEnd!.Value, inputSize) - next.ByteStart.Value)] };
            // Claimed: no second run can start for this slice. Chunks past the resume point belong to an older run.
            task.Chunks.RemoveAll(c => c.Index >= startIndex && c.Index < slice.FirstChunk + EmbyConversionPlanner.ChunksPerSlice);
            slice.Worker = OfferMarker;
            slice.JobId = null;
            slice.RunStartSeconds = startSeconds;
            slice.RunStartIndex = startIndex;
            slice.RunTimesRelative = null;
            slice.Position = startSeconds;
            slice.EndingSignalled = false;
            task.RefreshStatus();
        }
        // Each offered job is linked before its offer leaves (onCreated), so owner callbacks the worker's receive
        // loop processes before StartJobAsync returns (JobEnded, worker lost) find the slice through SliceOf.
        var (job, why) = await dispatcher.StartJobAsync(plan, JobKind.Background, this, Describe(task, slice), token,
            onCreated: created =>
            {
                lock (gate)
                {
                    if (slice.IsRunning && !slice.Done && slice.Worker != LocalWorkerName)
                    {
                        slice.JobId = created.Id;
                        slice.Worker = created.Worker.Identity.Name;
                        if (next != null && next.ReservedFor is null && !next.IsRunning)
                            next.ReservedFor = created.Worker.Identity.Id;
                    }
                }
            },
            avoid: avoid);
        if (job is null)
        {
            bool stillClaimed, nothingRuns, alongside, reservedRefused = false;
            lock (gate)
            {
                // Still claimed means no owner callback ended this run: it is ours to put back. Otherwise the run
                // already ended (OnEndedAsync/OnLostAsync/CancelAsync decided what follows).
                stillClaimed = slice.IsRunning && slice.Worker != LocalWorkerName && task.IsActive;
                if (next != null && !next.IsRunning)
                    next.ReservedFor = null;
                if (stillClaimed)
                {
                    slice.Worker = null;
                    slice.JobId = null;
                    task.WaitingSince ??= DateTimeOffset.UtcNow;
                    task.RefreshStatus();
                    if (task.Status == EncodeStatus.Pending)
                        task.Note = $"en attente d'un worker : {why}";
                    ReadmitSetAsideWorkers(task);
                }
                nothingRuns = !task.Slices.Any(s => s.IsRunning);
                // Workers are on this conversion: the server takes a slice of its own too, unless it is reserved for
                // a worker (already fetched ahead) or, at the end, a busy worker would finish it clearly sooner.
                alongside = stillClaimed && !nothingRuns && options.EncodeLocalAlongsideWorkers && slice.ReservedFor is null
                    && !task.Slices.Any(s => s.Worker == LocalWorkerName)
                    && !SlowerThanTheBusy(task, slice, BusyWorkers(task), [LocalWorkerName]).Any();
                // A reserved slice is offered to its worker only: refused (the worker is connected but unavailable,
                // e.g. its PC got busy), the reservation is dropped so any machine takes it from the next pass, and the
                // slices after it are still offered in this pass. Before, the pass stopped at this slice and the
                // conversion waited for that one worker while another sat idle (seen on 2026-11-01: 30 min, then the
                // conversion was given back to Emby with 4 slices left).
                if (stillClaimed && slice.ReservedFor is { } reservee)
                {
                    reservedRefused = true;
                    slice.ReservedFor = null;
                    logger.LogInformation("Conversion {Id}: slice {Slice} reserved for {Worker}, refused ({Why}): open to any machine",
                        task.Id, slice.Index + 1, reservee, why);
                }
            }
            // Otherwise the server steps in when no worker works on this conversion at all.
            if (stillClaimed && (nothingRuns || alongside))
                await TryStartLocalAsync(task, slice, plan, startSeconds, token, alongside);
            return reservedRefused;
        }
        lock (gate)
        {
            // A worker took the run: the tone mapper it was planned with is the conversion's from now on. (A refused
            // offer leaves it open: nothing was encoded, the next pass may still choose with the workers then connected.)
            FreezeToneMapping(task, job.Worker.Identity.Name);
            // Cancelled by Emby while the offer was in flight, or this run already ended (its JobEnded/loss was
            // processed right after the answer): the job is not wanted, or is already over.
            if (!task.IsActive || slice.JobId != job.Id)
            {
                _ = dispatcher.StopJobAsync(job, "conversion cancelled");
                return true;
            }
            task.WaitingSince = null;
            task.SetAsideBlockedSince = null;
            if (!task.Participants.Contains(job.Worker.Identity.Name))
                task.Participants.Add(job.Worker.Identity.Name);
            string kind = MachineKind(task, job.Worker);
            if (!task.MachineKinds.Contains(kind))
                task.MachineKinds.Add(kind);
            task.DiagnosticsLog.MachineFfmpeg[kind] = job.Worker.Hello?.Capabilities.FfmpegVersion;
            task.DiagnosticsLog.RunStarted(slice.Index, slice.RunStartIndex > slice.FirstChunk);
            Touch(task, startSeconds <= slice.Start
                ? $"tranche {slice.Index + 1}/{task.Slices.Count} sur {job.Worker.Identity.Name}"
                : $"tranche {slice.Index + 1}/{task.Slices.Count} reprise à {startSeconds:0}s sur {job.Worker.Identity.Name}");
        }
        return true;
    }

    /// <summary>Largest gap between a listed resume point and the source frame taken for it: half a frame at 10 fps.</summary>
    public const double MaxResumeShift = 0.05;

    /// <summary>
    /// Where a slice with chunks resumes: the source frame nearest to the end its previous run listed, with that
    /// listed end; null when the slice has nothing to resume from or no frame was found close to it. ffmpeg lists chunk
    /// times on its output frame grid (multiples of the frame duration from 0), and a video that does not start on
    /// that grid (start time 0.007 s, Mr. Robot S03E09) is listed up to half a frame off its own times; boundaries, on
    /// the contrary, are source frame times. A run resumed from a listed time 7 ms early kept its seek right but its -t,
    /// which counts from the first frame kept, reached past the slice's end: it encoded the next slice's first frame,
    /// shown twice once assembled (one frame late from there on; reproduced on the bench, T20a). Completed runs are
    /// aligned on their end instead (<see cref="EncodeTask.AlignRunEnd"/>). The answer is kept for the slice while its
    /// listed end does not change (one ffprobe pass per resume).
    /// </summary>
    private async Task<(double Listed, double Frame)?> ResumePointAsync(EncodeTask task, EncodeSlice slice, CancellationToken token)
    {
        double listed;
        lock (gate)
        {
            if (!task.IsActive || slice.Done || slice.IsRunning || task.ContiguousChunks(slice) == 0 || !task.Consistent(slice))
                return null;
            listed = task.EncodedEnd(slice);
            if (slice.ResumeListed == listed)
                return slice.ResumeFrame is { } known ? (listed, known) : null;
        }
        double? frame = null;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            frame = await media.NearestFrameAsync(task.Input, listed, limit.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Conversion {Id}: frame at {Time:0.###}s unreadable ({Error}), resuming from the listed time", task.Id, listed, ex.Message);
        }
        if (frame is { } f && Math.Abs(f - listed) > MaxResumeShift)
            frame = null;
        lock (gate)
        {
            slice.ResumeListed = listed;
            slice.ResumeFrame = frame;
            if (frame is { } past && task.ResumePastLastFrame(slice, listed, past) && task.IsActive && !slice.Done && !slice.IsRunning)
            {
                MarkVideoEnd(task, slice, ServerKind);
                Wake();
                return null;
            }
        }
        if (frame is { } found && found != listed)
            logger.LogInformation("Conversion {Id} slice {Slice}: resuming at the source frame {Frame:0.######}s (listed {Listed:0.######}s)",
                task.Id, slice.Index + 1, found, listed);
        return frame is { } fr ? (listed, fr) : null;
    }

    /// <summary>
    /// The run of a slice from its resume point (the frame where its last chunk ended) up to the slice's end frame
    /// (except the last slice, which runs to the end of the source).
    /// </summary>
    private JobPlan PlanFor(EncodeTask task, EncodeSlice slice, int startIndex, double startSeconds, long inputSize)
    {
        // Only the very first run of the first slice starts with the source and needs no seek.
        bool seek = !(slice.Index == 0 && startIndex == slice.FirstChunk);
        double? end = slice.Last ? task.Source?.DurationSeconds : slice.End;
        // Filters translated here (EmbyFilters.TranslateArgs) with the conversion's tone mapper: the same graph for every
        // slice, whoever encodes it.
        string[] template = EmbyFilters.TranslateArgs(EmbyConversionPlanner.ChunkArgs(task.ChunkTemplate, startIndex, startSeconds, seek,
            slice.Last ? null : slice.End, end - startSeconds, slice.SeekMargin), task.Requirements.ToneMapping);
        var file = new PlannedFile(0, JobFileKind.Input, task.Input, Path.GetFileName(task.Input), inputSize);
        return new JobPlan(true, "conversion", template, [file], EmbyConversionPlanner.ChunkSeconds, startIndex,
            $"encode/{task.Id}/seg_%d.ts", $"encode/{task.Id}/list.m3u8", task.Requirements)
        {
            Slice = new SliceInfo(Path.GetFileNameWithoutExtension(task.Input), slice.Index + 1, task.Slices.Count, slice.Start,
                end ?? slice.End),
            ServerFileStart = slice.ServerFileStart,
        };
    }

    /// <summary>Workers (id) running a slice of this conversion, with that slice. Call under the lock.</summary>
    private Dictionary<string, EncodeSlice> BusyWorkers(EncodeTask task)
    {
        var busy = new Dictionary<string, EncodeSlice>();
        foreach (var s in task.Slices)
            if (s.JobId != null && dispatcher.FindJob(s.JobId) is { } job)
                busy[job.Worker.Identity.Id] = s;
        return busy;
    }

    /// <summary>
    /// Workers (id) whose only running slice of this conversion ends within <c>EncodeOverlapSeconds</c>: they may start
    /// their next slice now. Call under the lock.
    /// </summary>
    private HashSet<string> EndingWorkers(EncodeTask task)
    {
        var ending = new HashSet<string>();
        if (options.EncodeOverlapSeconds <= 0)
            return ending;
        var running = new Dictionary<string, int>();
        foreach (var s in task.Slices)
        {
            if (s.JobId == null || dispatcher.FindJob(s.JobId) is not { } job)
                continue;
            string id = job.Worker.Identity.Id;
            running[id] = running.GetValueOrDefault(id) + 1;
            if (IsEnding(task, s, id))
                ending.Add(id);
        }
        ending.RemoveWhere(id => running[id] > 1);
        return ending;
    }

    /// <summary>
    /// Whether a running slice ends within <c>EncodeOverlapSeconds</c> of wall time, at its run's speed (or its
    /// worker's measured one before the run reports any). Call under the lock.
    /// </summary>
    private bool IsEnding(EncodeTask task, EncodeSlice s, string workerId)
    {
        double speed = s.Speed > 0.05 ? s.Speed : workerSpeeds.GetValueOrDefault(workerId);
        if (options.EncodeOverlapSeconds <= 0 || speed <= 0.05)
            return false;
        double end = s.Last ? task.Source?.DurationSeconds ?? s.End : s.End;
        return (end - Math.Max(s.Position, s.Start)) / speed <= options.EncodeOverlapSeconds;
    }

    /// <summary>
    /// End of a conversion (no more waiting slices than busy workers): an idle worker is passed over for this slice
    /// when a busy one would still finish it clearly sooner, counting what that one has left of its current slice.
    /// Without measured speeds nobody is passed over. Call under the lock.
    /// </summary>
    private IEnumerable<string> SlowerThanTheBusy(EncodeTask task, EncodeSlice slice, Dictionary<string, EncodeSlice> busy, HashSet<string> connected)
    {
        int waiting = task.Slices.Count(s => !s.Done && !s.IsRunning);
        if (waiting > busy.Count || busy.Count == 0)
            yield break;
        double length = (slice.Last ? task.Source?.DurationSeconds ?? slice.End : slice.End) - task.EncodedEnd(slice);
        double? bestBusy = null;
        foreach (var (id, current) in busy)
        {
            if (!workerSpeeds.TryGetValue(id, out double speed) || speed <= 0.05)
                continue;
            double end = current.Last ? task.Source?.DurationSeconds ?? current.End : current.End;
            double finish = Math.Max(0, end - Math.Max(current.Position, current.Start)) / speed + length / speed;
            bestBusy = bestBusy is null ? finish : Math.Min(bestBusy.Value, finish);
        }
        if (bestBusy is null)
            yield break;
        foreach (string id in connected)
        {
            if (busy.ContainsKey(id) || !workerSpeeds.TryGetValue(id, out double speed) || speed <= 0.05)
                continue;
            if (bestBusy.Value < 0.8 * length / speed)
                yield return id;
        }
    }

    private static string Describe(EncodeTask task, EncodeSlice slice) =>
        $"conversion {task.Id} tranche {slice.Index + 1}/{task.Slices.Count} {Path.GetFileName(task.Input)}";

    /// <summary>All slices are there: the shim (running as Emby's user) assembles Emby's output from this list.</summary>
    private async Task HandToShimAsync(EncodeTask task)
    {
        var list = new StringBuilder();
        var summary = new StringBuilder();
        string? audioFile, audioOffset;
        lock (gate)
        {
            if (task.Status != EncodeStatus.Pending || !task.Slices.All(s => s.Done))
                return;
            task.Status = EncodeStatus.Assembling;
            task.Speed = 0;
            // Not ready by now (WaitForAudio gave up on it): the assembly converts the audio, as without it.
            if (audioRun?.Task == task)
                KillAudio(audioRun, "pas prêt à l'assemblage");
            task.AudioSettled = true;
            audioFile = task.AudioFile;
            audioOffset = task.AudioOffset;
            Touch(task, audioFile != null
                ? "assemblage par le serveur (commande d'Emby, audio converti à l'avance)"
                : "assemblage par le serveur (commande d'Emby)");
            // ffmpeg concat demuxer list, slice after slice; single quotes in paths are escaped the way the demuxer expects.
            foreach (var slice in task.Slices.OrderBy(s => s.Index))
            {
                var durations = task.ConcatDurations(slice);
                foreach (var (index, duration) in durations)
                {
                    list.Append(CultureInfo.InvariantCulture, $"file '{ChunkPath(task.Id, index).Replace("'", "'\\''")}'\n");
                    // Each chunk's exact length (from the segment lists, see ConcatDurations): without it the concat
                    // demuxer guesses it from the MPEG-TS file, and older ffmpeg builds (Emby's 5.1) guess one frame
                    // short now and then; the next chunk then overlaps and the video ends frames early, drifting from the audio.
                    if (duration > 0)
                        list.Append(CultureInfo.InvariantCulture, $"duration {duration:0.######}\n");
                }
                // Kept in the log: which chunks went into the file, up to where, and the slice's length in the list
                // (its chunk durations added up), for any later doubt about its end.
                summary.Append(CultureInfo.InvariantCulture, $" {slice.Index + 1}:{durations.Count}@{task.EncodedEnd(slice):0.000}/{durations.Sum(d => d.Duration):0.000}");
            }
        }
        lock (gate)
            task.AssemblySummary = summary.ToString();
        logger.LogInformation("Conversion {Id} assembled from chunks (slice:count@end/listed length):{Summary}", task.Id, summary);
        logger.LogInformation("Conversion {Id}: {Audio}", task.Id, audioFile != null
            ? $"audio converted ahead used ({audioFile}, itsoffset {audioOffset})"
            : $"audio converted at assembly ({task.Audio ?? "désactivé"})");
        string listFile = Path.Combine(SpoolDir(task.Id), "concat.txt");
        await File.WriteAllTextAsync(listFile, list.ToString());
        try
        {
            await task.Shim!.SendAsync(new ConversionReady(task.Id, listFile, audioFile, audioOffset, task.AssemblyVideoOffset()));
        }
        catch (Exception)
        {
            await CancelAsync(task.Id, "relais d'Emby injoignable");
        }
    }

    /// <summary>
    /// Gives the conversion back to Emby: the shim then runs Emby's original command on the server. Reported to GitHub
    /// (<see cref="ReportFailure"/>) unless <paramref name="report"/> is false: the normal cases without any error
    /// (no capable worker connected for EncodeNoWorkerMinutes, source removed meanwhile).
    /// </summary>
    private async Task GiveUpAsync(EncodeTask task, string error, bool report = true)
    {
        List<Job> jobs;
        lock (gate)
        {
            if (!task.IsActive)
                return;
            jobs = DetachRuns(task);
            task.Status = EncodeStatus.Failed;
            task.Error = error;
            task.Speed = 0;
            Touch(task, "échec : Emby convertit sur le serveur");
        }
        foreach (var job in jobs)
            await dispatcher.StopJobAsync(job, "conversion given back to Emby");
        logger.LogWarning("Conversion {Id} given back to Emby: {Error}", task.Id, error);
        if (report)
            ReportFailure(task, FailureKind.GivenBack, error);
        try
        {
            if (task.Shim is { IsOpen: true } shim)
                await shim.SendAsync(new ConversionFailed(task.Id, error));
        }
        catch (Exception) { }
        DeleteSpool(task.Id);
    }

    // ---- Local fallback on the server ---------------------------------------------------------------

    /// <summary>
    /// No worker works on this conversion any more and none is free, or (<paramref name="alongside"/>) workers do and
    /// the server takes a slice of its own: encode the slice on the server with Emby's ffmpeg, producing the same
    /// chunks, so a worker can take over again later without losing work. Alongside workers there is no waiting delay:
    /// they are busy with slices of this conversion, not between two of them.
    /// </summary>
    private async Task TryStartLocalAsync(EncodeTask task, EncodeSlice slice, JobPlan plan, double startSeconds, CancellationToken token,
        bool alongside = false)
    {
        if (!options.EncodeLocalFallback || localRun != null)
            return;
        // The delay leaves a worker between two slices time to take the next one; with no capable worker connected
        // at all there is nobody to wait for, and the server goes on with the next slice at once.
        if (!alongside && (task.WaitingSince is null
            || (DateTimeOffset.UtcNow - task.WaitingSince.Value < TimeSpan.FromSeconds(options.EncodeLocalDelaySeconds)
                && dispatcher.HasWorkerFor(plan.Requirements!))))
            return;
        if (!await ServerCanEncodeAsync(plan.Requirements!, token))
        {
            // The server cannot encode these slices the way the workers do (e.g. no zscale for a translated
            // supertonemap): it leaves them to the workers, retried every pass (set-aside ones too, after
            // EncodeSetAsideRetryMinutes). Only when nothing could be placed for EncodeNoWorkerMinutes, whether workers
            // are gone or connected but unable to take it, is the conversion given back to Emby, which redoes it with
            // its own command on the server.
            if (!alongside && task.WaitingSince is { } since
                && DateTimeOffset.UtcNow - since >= TimeSpan.FromMinutes(options.EncodeNoWorkerMinutes))
                await GiveUpAsync(task, dispatcher.HasWorkerFor(plan.Requirements!)
                    ? $"aucun worker n'a pu reprendre la conversion pendant {options.EncodeNoWorkerMinutes} min, et le serveur ne peut pas encoder ces morceaux"
                    : $"plus aucun worker capable connecté{(dispatcher.ConversionRefusals() is { } refused ? $" ({refused})" : "")}, et le serveur ne peut pas encoder ces morceaux", report: false);
            return;
        }

        string dir = SpoolDir(task.Id);
        Directory.CreateDirectory(dir);
        string list = Path.Combine(dir, "local-list.csv");
        File.Delete(list);
        // Same template as a worker run; only the placeholders differ, and the segment list is CSV so chunk times can
        // be read while ffmpeg is still writing. Filter graphs get the same translation as on the workers (Emby-only
        // filters such as supertonemap replaced by standard ones), so every slice of a film looks the same whoever
        // encodes it.
        var args = new List<string> { "-hide_banner", "-nostats", "-progress", "pipe:1" };
        for (int i = 0; i < plan.Template.Length; i++)
        {
            string a = plan.Template[i];
            bool filter = i > 0 && plan.Template[i - 1] is "-filter_complex" or "-filter" or "-vf" or "-af";
            args.Add(a switch
            {
                "{input:0}" => task.Input,
                "{list}" => list,
                "{out}" => Path.Combine(dir, "chunk_%05d.ts"),
                // Already translated by PlanFor (zscale: a conversion tone mapped on the GPU never reaches the server).
                _ when filter => EmbyFilters.Translate(a, ToneMapper.Zscale),
                _ => a,
            });
            if (a == "-segment_list_type")
            {
                args.Add("csv");
                i++;
            }
        }

        lock (gate)
        {
            if (!task.IsActive || task.Status == EncodeStatus.Assembling || slice.Done || slice.IsRunning || slice.ReservedFor != null
                || (alongside ? task.Slices.Any(s => s.Worker == LocalWorkerName) : task.Slices.Any(s => s.IsRunning)))
                return;
            // The server's runs are always zscale (a libplacebo conversion never passes ServerCanEncodeAsync).
            FreezeToneMapping(task, LocalWorkerName);
            slice.Worker = LocalWorkerName;
            slice.JobId = null;
            slice.RunStartSeconds = startSeconds;
            slice.RunStartIndex = plan.StartIndex;
            slice.RunTimesRelative = null;
            slice.Position = startSeconds;
            task.WaitingSince = null;
            task.RefreshStatus();
            if (!task.Participants.Contains(LocalWorkerName))
                task.Participants.Add(LocalWorkerName);
            if (!task.MachineKinds.Contains(ServerKind))
                task.MachineKinds.Add(ServerKind);
            task.DiagnosticsLog.RunStarted(slice.Index, plan.StartIndex > slice.FirstChunk);
            Touch(task, alongside
                ? $"tranche {slice.Index + 1}/{task.Slices.Count} encodée sur le serveur en parallèle des workers, à partir de {startSeconds:0}s"
                : $"aucun worker{(dispatcher.ConversionRefusals() is { } refused ? $" ({refused})" : "")} : tranche {slice.Index + 1}/{task.Slices.Count} encodée sur le serveur à partir de {startSeconds:0}s");
        }
        Process process;
        try
        {
            process = media.Start(args);
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                if (slice.Worker == LocalWorkerName)
                {
                    slice.Worker = null;
                    task.WaitingSince = DateTimeOffset.UtcNow;
                    task.RefreshStatus();
                    Touch(task, $"encodage sur le serveur impossible : {ex.Message}");
                }
            }
            logger.LogWarning("Conversion {Id}: cannot start the local encode: {Error}", task.Id, ex.Message);
            return;
        }
        var run = new LocalRun { Task = task, Slice = slice, Process = process, ListFile = list, Requirements = plan.Requirements! };
        lock (gate)
            localRun = run;
        logger.LogInformation("Conversion {Id}: {Why}, encoding slice {Slice} locally from chunk {Start}", task.Id,
            alongside ? "alongside the workers" : "no worker", slice.Index + 1, plan.StartIndex);
        // Tied to the host's lifetime (token = ApplicationStopping): the conversion belongs to Emby's ffmpeg, which
        // falls back to its own command once the coordinator is gone, so a local ffmpeg must not outlive it.
        _ = Task.Run(() => SuperviseLocalAsync(run, token));
    }

    /// <summary>Drains the local ffmpeg's output, registers its chunks as they complete and decides what follows its exit.</summary>
    private async Task SuperviseLocalAsync(LocalRun run, CancellationToken stopping)
    {
        var task = run.Task;
        var registration = stopping.Register(() => KillLocal(run));
        try
        {
            await SuperviseLocalCoreAsync(run);
        }
        catch (Exception ex)
        {
            // Fire-and-forget: nothing else observes this task, so a failure must not leave the slice stuck as running
            // with localRun set (which would also block every later local run).
            logger.LogError(ex, "Conversion {Id}: local encode supervision failed", task.Id);
            KillLocal(run);
            lock (gate)
            {
                if (localRun == run)
                    localRun = null;
                if (run.Slice.Worker == LocalWorkerName)
                {
                    run.Slice.Worker = null;
                    task.RefreshStatus();
                    Touch(task, $"encodage sur le serveur interrompu ({ex.Message}), nouvel essai");
                }
            }
            try { run.Process.Dispose(); } catch (Exception) { }
            Wake();
        }
        finally
        {
            registration.Dispose();
        }
    }

    private static void KillLocal(LocalRun run)
    {
        try
        {
            if (!run.Process.HasExited)
                run.Process.Kill(true);
        }
        catch (Exception)
        {
            // Already exited or disposed.
        }
    }

    private async Task SuperviseLocalCoreAsync(LocalRun run)
    {
        var task = run.Task;
        var slice = run.Slice;
        var stderr = Task.Run(async () =>
        {
            while (await run.Process.StandardError.ReadLineAsync() is { } line)
            {
                if (FfmpegLog.InputStart(line) is { } inputStart)
                    lock (gate)
                        task.DiagnosticsLog.FileStarts.TryAdd(ServerKind, inputStart);
                lock (run.ErrorTail)
                {
                    run.ErrorTail.Enqueue(line);
                    if (run.ErrorTail.Count > 5)
                        run.ErrorTail.Dequeue();
                }
            }
        });
        var progress = Task.Run(async () =>
        {
            while (await run.Process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line[12..], out long us))
                {
                    // ffmpeg's progress counts from the run's first frame, -copyts or not.
                    slice.Position = slice.RunStartSeconds + us / 1e6;
                    await ReportProgressAsync(task);
                }
                else if (line.StartsWith("speed=", StringComparison.Ordinal) && double.TryParse(line[6..].TrimEnd('x'), CultureInfo.InvariantCulture, out double sp))
                {
                    slice.Speed = sp;
                    // The server's own speed, so the end of a conversion is not left to it when a worker is faster.
                    if (sp > 0.05)
                        workerSpeeds.AddOrUpdate(LocalWorkerName, sp, (_, old) => old * 0.7 + sp * 0.3);
                }
            }
        });
        while (!run.Process.HasExited)
        {
            RegisterLocalChunks(run);
            await Task.Delay(1000);
        }
        await Task.WhenAll(stderr, progress);
        RegisterLocalChunks(run);

        string? giveUp = null;
        lock (gate)
        {
            localRun = null;
            // Still ours unless the task was cancelled or given up meanwhile (DetachRuns cleared it).
            if (slice.Worker == LocalWorkerName && task.IsActive)
            {
                slice.Worker = null;
                slice.Speed = 0;
                bool covers = !run.HandOver && run.Process.ExitCode == 0 && task.Covers(slice);
                slice.IdleRuns = !covers && run.Process.ExitCode == 0 && !run.HandOver && task.DropIdleRun(slice) ? slice.IdleRuns + 1 : 0;
                if (run.HandOver)
                    Touch(task, $"tranche {slice.Index + 1}/{task.Slices.Count} confiée à un worker à partir de {task.EncodedEnd(slice):0}s");
                else if (covers)
                {
                    task.AlignRunEnd(slice);
                    slice.Done = true;
                    slice.DoneBy = ServerKind;
                    Touch(task, $"tranche {slice.Index + 1}/{task.Slices.Count} terminée sur le serveur");
                }
                else if (task.EndsAtVideoEnd(slice))
                    MarkVideoEnd(task, slice, ServerKind);
                else if (++task.Attempts >= options.EncodeMaxAttempts)
                {
                    lock (run.ErrorTail)
                        giveUp = $"échec de l'encodage sur le serveur : {string.Join(" | ", run.ErrorTail)}";
                }
                else
                    Touch(task, $"encodage sur le serveur interrompu (code {run.Process.ExitCode}), nouvel essai");
                task.RefreshStatus();
            }
        }
        if (giveUp != null)
            await GiveUpAsync(task, giveUp);
        run.Process.Dispose();
        Wake();
    }

    /// <summary>The CSV list gets "chunk_NNNNN.ts,start,end" once a chunk file is complete.</summary>
    private void RegisterLocalChunks(LocalRun run)
    {
        if (!File.Exists(run.ListFile))
            return;
        string[] lines;
        try
        {
            using var fs = new FileStream(run.ListFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (IOException)
        {
            return;
        }
        for (; run.RegisteredLines < lines.Length; run.RegisteredLines++)
        {
            string[] parts = lines[run.RegisteredLines].Split(',');
            if (parts.Length < 3 || !int.TryParse(Path.GetFileNameWithoutExtension(parts[0])["chunk_".Length..], out int index))
                return;
            string path = ChunkPath(run.Task.Id, index);
            double start = double.Parse(parts[1], CultureInfo.InvariantCulture), end = double.Parse(parts[2], CultureInfo.InvariantCulture);
            lock (gate)
            {
                if (run.Slice.Worker != LocalWorkerName)
                    return;
                run.Task.AddChunk(run.Slice, index, start, end, File.Exists(path) ? new FileInfo(path).Length : 0);
                Save();
            }
        }
    }

    // ---- Audio converted ahead on the server --------------------------------------------------------

    /// <summary>The audio of a conversion, converted on the server while the video is encoded (see <see cref="AudioAheadPlan"/>).</summary>
    private sealed class AudioRun
    {
        public required EncodeTask Task { get; init; }
        public required Process Process { get; init; }
        public required string File { get; init; }
        /// <summary>Source seconds converted so far, and at what speed (from ffmpeg's progress).</summary>
        public double Position { get; set; }
        public double Speed { get; set; }
        /// <summary>Why it was stopped before its end; null while it runs to its end.</summary>
        public string? Stopped { get; set; }
        public Queue<string> ErrorTail { get; } = new();
    }

    // At most one audio conversion at a time, next to the local slice encode. Set by the scheduler loop, cleared by
    // SuperviseAudioAsync (under gate).
    private AudioRun? audioRun;

    /// <summary>
    /// Starts converting the audio of a conversion with Emby's own options. Light (audio decoding and encoding, about
    /// one core for a minute or two per episode) and one at a time, it runs at normal priority: below it, it would get
    /// next to nothing while the server encodes a slice on all its cores, and would not be ready in time. Only once
    /// the source's mapped streams are checked to be audio streams; anything unexpected settles it: the assembly then
    /// converts the audio.
    /// </summary>
    private async Task TryStartAudioAsync(EncodeTask task, CancellationToken token)
    {
        var plan = task.AudioAhead!;
        lock (gate)
        {
            if (audioRun != null || task.AudioSettled || !task.IsActive || task.Status == EncodeStatus.Assembling)
                return;
        }
        string? problem = null;
        try
        {
            var streams = await media.StreamsAsync(task.Input, token);
            foreach (int index in plan.SourceStreams)
            {
                var s = streams.Streams.FirstOrDefault(s => s.Index == index);
                if (s is null || s.Type != "audio")
                    problem = $"flux {index} de la source : {s?.Type ?? "absent"}, pas de l'audio";
                if (problem != null)
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            problem = $"analyse de la source impossible : {ex.Message}";
        }
        if (problem != null)
        {
            SettleAudio(task, null, null, $"converti à l'assemblage ({problem})");
            logger.LogInformation("Conversion {Id}: audio converted at assembly ({Reason})", task.Id, problem);
            return;
        }

        string dir = SpoolDir(task.Id);
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "audio.mp4");
        File.Delete(file);
        var args = new List<string> { "-hide_banner", "-nostats", "-progress", "pipe:1" };
        args.AddRange(plan.PrepareTemplate.Select(a => a == AudioAheadPlan.AudioPlaceholder ? file : a));
        // Started outside the lock (creating a process can take seconds, an antivirus scanning it for instance).
        Process process;
        try
        {
            process = media.Start(args);
        }
        catch (Exception ex)
        {
            SettleAudio(task, null, null, $"converti à l'assemblage (démarrage impossible : {ex.Message})");
            logger.LogWarning("Conversion {Id}: cannot start the audio conversion: {Error}", task.Id, ex.Message);
            return;
        }
        AudioRun run;
        lock (gate)
        {
            // Cancelled or assembled meanwhile (DetachRuns found no run to stop): not wanted any more.
            if (audioRun != null || task.AudioSettled || !task.IsActive || task.Status == EncodeStatus.Assembling)
            {
                try { process.Kill(true); } catch (Exception) { }
                process.Dispose();
                return;
            }
            audioRun = run = new AudioRun { Task = task, Process = process, File = file };
            task.Audio = "conversion en cours sur le serveur";
            Save();
        }
        logger.LogInformation("Conversion {Id}: converting the audio ahead ({Streams} stream(s))", task.Id, plan.SourceStreams.Length);
        // Tied to the host's lifetime, as the local slice encode.
        _ = Task.Run(() => SuperviseAudioAsync(run, token));
    }

    private async Task SuperviseAudioAsync(AudioRun run, CancellationToken stopping)
    {
        var task = run.Task;
        var registration = stopping.Register(() => KillAudio(run, "coordinateur arrêté"));
        string? problem;
        string? offset = null;
        try
        {
            var stderr = Task.Run(async () =>
            {
                while (await run.Process.StandardError.ReadLineAsync() is { } line)
                {
                    lock (run.ErrorTail)
                    {
                        run.ErrorTail.Enqueue(line);
                        if (run.ErrorTail.Count > 5)
                            run.ErrorTail.Dequeue();
                    }
                }
            });
            var progress = Task.Run(async () =>
            {
                while (await run.Process.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line[12..], out long us))
                        run.Position = us / 1e6;
                    else if (line.StartsWith("speed=", StringComparison.Ordinal) && double.TryParse(line[6..].TrimEnd('x'), CultureInfo.InvariantCulture, out double sp))
                        run.Speed = sp;
                }
            });
            await run.Process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stderr, progress);
            lock (run.ErrorTail)
                problem = run.Stopped ?? (run.Process.ExitCode != 0 ? $"échec (code {run.Process.ExitCode} : {string.Join(" | ", run.ErrorTail)})" : null);
            if (problem is null)
                (problem, offset) = await CheckAudioAsync(task, run.File);
        }
        catch (Exception ex)
        {
            KillAudio(run, "supervision interrompue");
            problem = $"supervision interrompue : {ex.Message}";
        }
        finally
        {
            registration.Dispose();
        }
        SettleAudio(task, run, problem is null ? offset : null,
            problem is null ? $"converti à l'avance sur le serveur, itsoffset {offset}" : $"converti à l'assemblage ({problem})");
        if (problem is null)
            logger.LogInformation("Conversion {Id}: audio converted ahead and checked ({File}, itsoffset {Offset})", task.Id, run.File, offset);
        else
            logger.LogWarning("Conversion {Id}: audio conversion ahead not used: {Problem}", task.Id, problem);
        try { run.Process.Dispose(); } catch (Exception) { }
        Wake();
    }

    /// <summary>
    /// The prepared file holds one audio stream per audio map, starts where it should (shifted by
    /// <see cref="AudioAheadPlan.TimeShift"/>, so never at or below 0) and lasts as long as the source (same tolerance
    /// as the check of the assembled file). Returns the problem, or the assembly's -itsoffset.
    /// </summary>
    private async Task<(string? Problem, string? Offset)> CheckAudioAsync(EncodeTask task, string file)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var prepared = await media.StreamsAsync(file, limit.Token);
            var plan = task.AudioAhead!;
            if (prepared.Streams.Count != plan.SourceStreams.Length || prepared.Streams.Any(s => s.Type != "audio"))
                return ($"{prepared.Streams.Count} flux au lieu de {plan.SourceStreams.Length} pistes audio", null);
            if (prepared.StartMicros <= 0)
                return ($"début à {prepared.StartMicros / 1e6:0.000000}s", null);
            // Length of the audio from its first packet to the end of its last one: what the file's duration field
            // means depends on the container, the packets do not.
            double source = task.Source?.DurationSeconds ?? 0;
            double start = prepared.StartMicros / 1e6;
            if (await media.PacketsEndAsync(file, limit.Token) is not { } end)
                return ("fin de l'audio illisible", null);
            double tolerance = Math.Max(1.5, source * 0.0005);
            if (Math.Abs(end - start - source) > tolerance)
                return ($"audio de {end - start:0.000}s pour une source de {source:0.000}s", null);
            return (null, plan.AssemblyOffset(prepared.StartMicros));
        }
        catch (Exception ex)
        {
            return ($"vérification impossible : {ex.Message}", null);
        }
    }

    /// <summary>The audio conversion is over for this task: ready (with its file and offset) or not to be used.</summary>
    private void SettleAudio(EncodeTask task, AudioRun? run, string? offset, string note)
    {
        lock (gate)
        {
            if (run != null && audioRun == run)
                audioRun = null;
            // Already settled by the assembly (which did not wait), or the conversion is over: kept as it is.
            if (task.AudioSettled || !task.IsActive || task.Status == EncodeStatus.Assembling)
                return;
            task.AudioSettled = true;
            task.Audio = note;
            if (run != null && offset != null)
            {
                task.AudioFile = run.File;
                task.AudioOffset = offset;
            }
            Save();
        }
    }

    /// <summary>Stops an audio conversion; its end is then not used. Callable under the lock.</summary>
    private static void KillAudio(AudioRun run, string why)
    {
        run.Stopped ??= why;
        try
        {
            if (!run.Process.HasExited)
                run.Process.Kill(true);
        }
        catch (Exception)
        {
            // Already exited or disposed.
        }
    }

    /// <summary>
    /// Every slice is encoded: whether to hold the assembly back for the audio converted ahead, still running. The
    /// usual assembly would convert the whole audio again, at about the same speed (the server's processor is freed
    /// for both alike), so finishing the run already under way is the shorter way: the assembly waits for it, but
    /// never longer than converting the whole audio again would take at the speed measured when the wait began (nor
    /// less than <c>EncodeAudioWaitSeconds</c>). A run with no measured speed yet, or past that limit, is given up:
    /// the assembly goes ahead at once and converts the audio itself (HandToShimAsync stops the audio run).
    /// </summary>
    private bool WaitForAudio(EncodeTask task)
    {
        lock (gate)
        {
            if (task.AudioAhead is null || task.AudioSettled || audioRun?.Task != task || audioRun.Speed <= 0.05
                || task.Source is not { DurationSeconds: > 0 } source)
                return false;
            var now = DateTimeOffset.UtcNow;
            double remaining = Math.Max(0, source.DurationSeconds - audioRun.Position) / audioRun.Speed;
            double again = source.DurationSeconds / audioRun.Speed;
            if (task.AudioWaitUntil is null)
            {
                task.AudioWaitUntil = now + TimeSpan.FromSeconds(Math.Max(options.EncodeAudioWaitSeconds, again));
                Touch(task, $"vidéo prête, fin de la conversion de l'audio à l'avance attendue (environ {remaining:0} s)");
                logger.LogInformation("Conversion {Id}: waiting about {Seconds:0}s for the audio converted ahead (at most {Limit:0}s)",
                    task.Id, remaining, Math.Max(options.EncodeAudioWaitSeconds, again));
            }
            return now <= task.AudioWaitUntil.Value;
        }
    }

    // ---- IJobOwner (worker runs) -------------------------------------------------------------------

    /// <summary>
    /// Stores a chunk uploaded by the worker (write to .part then rename, so a listed chunk is always complete).
    /// The index is absolute (the run starts numbering at its start chunk); times are relative to the run start.
    /// </summary>
    public async Task<bool> OnSegmentAsync(Job job, int index, double start, double end, ReadOnlyMemory<byte> data)
    {
        EncodeTask? task;
        EncodeSlice? slice;
        lock (gate)
        {
            (task, slice) = SliceOf(job);
            if (task is null || slice is null
                || index < slice.FirstChunk || index >= slice.FirstChunk + EmbyConversionPlanner.ChunksPerSlice)
                return false;
        }
        Directory.CreateDirectory(SpoolDir(task.Id));
        string path = ChunkPath(task.Id, index);
        // Per-job temporary name: a stale upload of a previous run cannot clobber this run's file in progress.
        string part = $"{path}.{job.Id}.part";
        try
        {
            await File.WriteAllBytesAsync(part, data.ToArray());
            lock (gate)
            {
                // The run may have ended (and another started) during the write: only the current run's chunks are
                // moved into place and registered.
                if (slice.JobId != job.Id || !task.IsActive || task.Status == EncodeStatus.Assembling)
                    return false;
                // Still this run (checked above), so the slice's run offsets are the ones this chunk was made with.
                MoveWithRetry(part, path);
                task.AddChunk(slice, index, start, end, data.Length);
                Save();
            }
            return true;
        }
        finally
        {
            // Left behind only when the upload was refused or failed.
            try { File.Delete(part); } catch (Exception) { }
        }
    }

    public Task OnProgressAsync(Job job, JobProgress progress)
    {
        EncodeTask? task;
        lock (gate)
        {
            (task, var slice) = SliceOf(job);
            if (task is null || slice is null)
                return Task.CompletedTask;
            // ffmpeg's progress counts from the run's first frame, -copyts or not.
            slice.Position = slice.RunStartSeconds + progress.TimeSeconds;
            slice.Speed = progress.Speed;
            if (progress.Speed > 0.05)
                workerSpeeds.AddOrUpdate(job.Worker.Identity.Id, progress.Speed, (_, old) => old * 0.7 + progress.Speed * 0.3);
            // Nearly done: wake the scheduler once so the worker's next slice starts ahead (EncodeOverlapSeconds).
            if (!slice.EndingSignalled && IsEnding(task, slice, job.Worker.Identity.Id))
            {
                slice.EndingSignalled = true;
                Wake();
            }
        }
        return ReportProgressAsync(task);
    }

    /// <summary>
    /// Progress in source time over the whole conversion (done slices, plus the running ones where they are), so
    /// Emby's conversion page shows a meaningful percentage; the speed is the sum of the running slices'.
    /// </summary>
    private async Task ReportProgressAsync(EncodeTask task)
    {
        double covered, speed;
        lock (gate)
        {
            if (task.Source is not { DurationSeconds: > 0 } source)
                return;
            covered = task.CoveredSeconds();
            speed = task.Slices.Where(s => s.IsRunning).Sum(s => s.Speed);
            task.Percent = Math.Round(Math.Min(99.9, 100 * covered / source.DurationSeconds), 1);
            task.Speed = speed;
        }
        try
        {
            if (task.Shim is { IsOpen: true } shim)
                await shim.SendAsync(new JobProgress(task.Id, 0, 0, covered, speed));
        }
        catch (Exception) { }
        await ReportToWorkersAsync(task);
    }

    /// <summary>Progress of the whole conversion to the workers running its slices, for their window (0.7.8 and later).</summary>
    private async Task ReportToWorkersAsync(EncodeTask task)
    {
        List<string> jobIds;
        double percent;
        double? remaining;
        lock (gate)
        {
            jobIds = task.Slices.Where(s => s.JobId != null).Select(s => s.JobId!).ToList();
            percent = task.Percent;
            remaining = task.RemainingSeconds();
        }
        int? queue = QueueRemaining();
        foreach (string id in jobIds)
        {
            if (dispatcher.FindJob(id) is not { IsActive: true } job || !KnowsConversionProgress(job.Worker.Hello?.Version))
                continue;
            try
            {
                await job.Worker.SendAsync(new ConversionProgress(id, percent, remaining, queue), CancellationToken.None);
            }
            catch (Exception) { }
        }
    }

    private int? queueRemaining;
    private DateTimeOffset queueReadAt;

    /// <summary>Emby's conversion queue length (see <c>ConversionQueueRemaining</c>), read at most every 30 s.</summary>
    public int? QueueRemaining()
    {
        if (options.ConversionQueueRemaining is not { } read)
            return null;
        var now = DateTimeOffset.UtcNow;
        if (now - queueReadAt >= TimeSpan.FromSeconds(30))
        {
            queueReadAt = now;
            try
            {
                queueRemaining = read();
            }
            catch (Exception ex)
            {
                queueRemaining = null;
                logger.LogDebug("Emby conversion queue unreadable: {Error}", ex.Message);
            }
        }
        return queueRemaining;
    }

    private DateTimeOffset queueBroadcastAt;

    /// <summary>
    /// Emby's conversion queue length to every connected worker every 30 s, busy or not, so it shows even while a
    /// worker runs nothing (empty JobId: no job's progress).
    /// </summary>
    private async Task BroadcastQueueAsync()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - queueBroadcastAt < TimeSpan.FromSeconds(30) || QueueRemaining() is not { } queue)
            return;
        queueBroadcastAt = now;
        foreach (var worker in dispatcher.Workers.Where(w => w.IsOpen && KnowsConversionProgress(w.Hello?.Version)).ToList())
        {
            try
            {
                await worker.SendAsync(new ConversionProgress("", 0, null, queue), CancellationToken.None);
            }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// Whether the server's own ffmpeg can encode slices with these requirements, Emby-only filters counted as their
    /// standard translation (the server runs the same translated graph as the workers).
    /// </summary>
    private async Task<bool> ServerCanEncodeAsync(JobRequirements requirements, CancellationToken token)
    {
        if (!options.EncodeLocalFallback)
            return false;
        try
        {
            return WorkerMatching.Incompatibility(requirements, await media.CapabilitiesAsync(token)) is null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Server ffmpeg capabilities unreadable: {Error}", ex.Message);
            return false;
        }
    }

    /// <summary>Workers before 0.7.8 drop their connection on a message type they do not know.</summary>
    private static bool KnowsConversionProgress(string? version) =>
        Version.TryParse(version, out var v) && v >= new Version(0, 7, 8);

    public async Task OnEndedAsync(Job job, JobEnded ended)
    {
        string? giveUp = null;
        string? failure = null;
        int? failedSlice = null;
        EncodeTask? task;
        lock (gate)
        {
            (task, var slice) = SliceOf(job);
            if (task is null || slice is null)
                return;
            slice.JobId = null;
            slice.Worker = null;
            slice.Speed = 0;
            if (ended.InputStart is { } inputStart)
                task.DiagnosticsLog.FileStarts.TryAdd(MachineKind(task, job.Worker), inputStart);
            if (ended.SeekCorrection is { } correction)
                task.ShiftRun(slice, correction);
            bool covers = ended.ExitCode == 0 && task.Covers(slice);
            slice.IdleRuns = !covers && ended.Error is null && task.DropIdleRun(slice) ? slice.IdleRuns + 1 : 0;
            if (covers)
            {
                task.AlignRunEnd(slice);
                slice.Done = true;
                slice.DoneBy = MachineKind(task, job.Worker);
                Touch(task, $"tranche {slice.Index + 1}/{task.Slices.Count} encodée par {job.Worker.Identity.Name}");
            }
            else if (ended.ExitCode == 0 && task.EndsAtVideoEnd(slice))
                MarkVideoEnd(task, slice, MachineKind(task, job.Worker));
            else if (ended.Error is null && slice.IdleRuns >= 3)
            {
                // Ended "normally" three times in a row without a single chunk: not an interruption any more. Treated
                // as a failure (worker set aside, retried later, conversion given back to Emby in the end) rather than
                // restarted forever.
                failure = $"ffmpeg s'est arrêté {slice.IdleRuns} fois de suite sans rien encoder à partir de {task.EncodedEnd(slice):0.###}s (code {ended.ExitCode})";
                if (!task.FailedWorkers.Contains(job.Worker.Identity.Id))
                    task.FailedWorkers.Add(job.Worker.Identity.Id);
                foreach (var s in task.Slices.Where(s => s.ReservedFor == job.Worker.Identity.Id))
                    s.ReservedFor = null;
                logger.LogWarning("Conversion {Id}: slice {Slice} ended {Runs} times in a row without any chunk on {Worker}, set aside for this conversion",
                    task.Id, slice.Index + 1, slice.IdleRuns, job.Worker.Identity.Name);
                Touch(task, $"tranche {slice.Index + 1}/{task.Slices.Count} : {failure} sur {job.Worker.Identity.Name}, ce worker est écarté de cette conversion");
            }
            else if (ended.Error is null)
            {
                // Ended early without an error from ffmpeg: the worker was closed or updated, or the job stopped
                // (real failures always carry ffmpeg's error lines). An interruption, as in OnLostAsync: the slice
                // resumes from its last chunk, and the worker stays welcome.
                logger.LogInformation("Conversion {Id}: slice {Slice} stopped early on {Worker} without error (code {Code}), resumed elsewhere",
                    task.Id, slice.Index + 1, job.Worker.Identity.Name, ended.ExitCode);
                Touch(task, $"tranche {slice.Index + 1}/{task.Slices.Count} interrompue sur {job.Worker.Identity.Name}, reprise à {task.EncodedEnd(slice):0}s");
            }
            else
            {
                failure = ended.Error;
                failedSlice = slice.Index + 1;
                // A failing worker is set aside for this conversion; only the server's own failures give it back to
                // Emby (OnLocalEndedAsync). The slice waits for another worker, or the server after the usual delay.
                if (!task.FailedWorkers.Contains(job.Worker.Identity.Id))
                    task.FailedWorkers.Add(job.Worker.Identity.Id);
                foreach (var s in task.Slices.Where(s => s.ReservedFor == job.Worker.Identity.Id))
                    s.ReservedFor = null;
                logger.LogWarning("Conversion {Id}: {Worker} failed slice {Slice} ({Error}), set aside for this conversion",
                    task.Id, job.Worker.Identity.Name, slice.Index + 1, ended.Error);
                Touch(task, $"tranche {slice.Index + 1}/{task.Slices.Count} : échec sur {job.Worker.Identity.Name} ({ended.Error}), ce worker est écarté de cette conversion");
            }
            task.RefreshStatus();
        }
        if (failure != null)
            ReportFailure(task, FailureKind.WorkerFailed, failure, job.Worker, failedSlice: failedSlice);
        if (giveUp != null)
            await GiveUpAsync(task, giveUp);
        Wake();
    }

    /// <summary>
    /// No machine took a waiting slice while the only capable workers connected are ones this conversion set aside:
    /// after EncodeSetAsideRetryMinutes they are retried, so the conversion is rebalanced on the workers before the
    /// EncodeNoWorkerMinutes give-up hands it back to Emby. Call under the lock.
    /// </summary>
    private void ReadmitSetAsideWorkers(EncodeTask task)
    {
        if (task.FailedWorkers.Count == 0)
        {
            task.SetAsideBlockedSince = null;
            return;
        }
        bool setAsideConnected = dispatcher.Workers.Any(w => w.IsOpen && w.Hello != null && w.ConversionsRefused is null && task.FailedWorkers.Contains(w.Identity.Id)
            && WorkerMatching.Incompatibility(task.Requirements, w.Hello.Capabilities) is null);
        if (!setAsideConnected)
        {
            task.SetAsideBlockedSince = null;
            return;
        }
        var now = DateTimeOffset.UtcNow;
        task.SetAsideBlockedSince ??= now;
        if (now - task.SetAsideBlockedSince.Value < TimeSpan.FromMinutes(options.EncodeSetAsideRetryMinutes))
            return;
        logger.LogWarning("Conversion {Id}: no other machine for {Minutes} min, set-aside workers retried", task.Id, options.EncodeSetAsideRetryMinutes);
        Touch(task, $"aucune autre machine depuis {options.EncodeSetAsideRetryMinutes} min : nouvel essai avec les workers écartés");
        task.FailedWorkers.Clear();
        task.SetAsideBlockedSince = null;
        Wake();
    }

    /// <summary>The last slice is done where its video stream ends (EncodeTask.EndsAtVideoEnd). Call under the lock.</summary>
    private void MarkVideoEnd(EncodeTask task, EncodeSlice slice, string doneBy)
    {
        slice.Done = true;
        slice.DoneBy = doneBy;
        double end = task.EncodedEnd(slice);
        logger.LogInformation("Conversion {Id}: the video ends at {End:0.###} s, {Gap:0.#} s before the source's duration: last slice done",
            task.Id, end, (task.Source?.DurationSeconds ?? end) - end);
        Touch(task, $"tranche {slice.Index + 1}/{task.Slices.Count} terminée : la vidéo s'arrête à {end:0.###}s, avant la fin de l'audio");
    }

    /// <summary>Interruptions are not failures: the slice waits for a worker (or the server) and resumes from its last chunk.</summary>
    public Task OnLostAsync(Job job, string reason)
    {
        lock (gate)
        {
            var (task, slice) = SliceOf(job);
            if (task is null || slice is null)
                return Task.CompletedTask;
            slice.JobId = null;
            slice.Worker = null;
            slice.Speed = 0;
            task.RefreshStatus();
            Touch(task, $"tranche {slice.Index + 1}/{task.Slices.Count} interrompue ({reason}), reprise à {task.EncodedEnd(slice):0}s");
        }
        Wake();
        return Task.CompletedTask;
    }

    // ---- Helpers -------------------------------------------------------------------------------------

    /// <summary>
    /// The active task and slice this job runs; nulls once the job is superseded, so stale callbacks are ignored.
    /// Call under the lock.
    /// </summary>
    private (EncodeTask?, EncodeSlice?) SliceOf(Job job)
    {
        foreach (var t in tasks)
        {
            if (!t.IsActive)
                continue;
            var s = t.Slices.FirstOrDefault(s => s.JobId == job.Id);
            if (s != null)
                return (t, s);
        }
        return (null, null);
    }

    // ---- Automatic failure reports --------------------------------------------------------------------

    /// <summary>The server as the failure reports name it (type only).</summary>
    private string ServerKind => $"server ({options.ServerDescription})";

    /// <summary>A worker as the failure reports name it: its version, GPUs and suitable encoders, never its name.</summary>
    private static string MachineKind(EncodeTask task, WorkerConnection worker) =>
        FailureReportFormatter.DescribeWorker(worker.Hello?.Version, worker.Hello?.Capabilities.Gpus, worker.Hello?.Capabilities.VideoEncoders,
            task.Requirements.VideoEncoder);

    /// <summary>
    /// Reports a failed conversion to GitHub (<see cref="FailureReporter"/>), in the background: the source is analysed,
    /// the report built and sent without ever delaying or affecting the conversion. Writes to the local log the line
    /// that links the report's anonymous source code to the file, so the administrator can find the episode. Call
    /// outside the lock.
    /// </summary>
    private void ReportFailure(EncodeTask task, FailureKind kind, string error, WorkerConnection? failedWorker = null, string? check = null, int? failedSlice = null)
    {
        try
        {
            string? failed = failedWorker is null ? null : MachineKind(task, failedWorker);
            if (kind != FailureKind.AssemblyRejected)
                NoteFailingRun(task, error, failed ?? (error.Contains("sur le serveur", StringComparison.Ordinal) ? ServerKind : null), failedSlice);
            if (!reporter.TryClaim(task.Id))
                return;
            List<string?> slices;
            List<string> machines;
            string? summary;
            lock (gate)
            {
                slices = task.Slices.OrderBy(s => s.Index).Select(s => s.Done ? s.DoneBy ?? "?" : null).ToList();
                machines = [.. task.MachineKinds];
                summary = task.AssemblySummary;
            }
            if (failed != null && !machines.Contains(failed))
                machines.Add(failed);
            // ffmpeg's last lines, as the worker sent them ("ffmpeg failed: line | line | ...").
            const string FfmpegFailed = "ffmpeg failed: ";
            List<string> tail = kind == FailureKind.WorkerFailed && error.StartsWith(FfmpegFailed, StringComparison.Ordinal)
                ? error[FfmpegFailed.Length..].Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                : [];
            var sensitive = SensitiveStrings(task);
            // Pins the spool until the analysis ends, so nothing deletes the chunks under it.
            var diagnosticsRequest = PrepareDiagnostics(task, kind, error, check);
            _ = Task.Run(async () =>
            {
                try
                {
                    SourceDescription? source = null;
                    long size = 0;
                    try
                    {
                        size = new FileInfo(task.Input).Length;
                        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        source = await media.DescribeAsync(task.Input, limit.Token);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug("Conversion {Id}: source not analysed for the failure report ({Error})", task.Id, ex.Message);
                    }
                    var diagnostics = await CollectDiagnosticsAsync(task, diagnosticsRequest);
                    double duration = source?.DurationSeconds ?? task.Source?.DurationSeconds ?? 0;
                    string code = FailureReportFormatter.SourceCode(size, duration, reporter.InstallId);
                    // Local log only: the published report carries the code, never the file.
                    logger.LogInformation("Conversion {Id}: failure reported to GitHub (anonymous source code {Code} = {Input})", task.Id, code, task.Input);
                    await reporter.SendAsync(new FailureReport
                    {
                        Kind = kind,
                        Error = error,
                        WorkerErrorTail = tail,
                        FailedWorker = failed,
                        PluginVersion = FailureReporter.Version,
                        Server = options.ServerDescription,
                        Installation = reporter.InstallId[..8],
                        SourceCode = code,
                        Source = source,
                        RequestedEncoder = task.Requirements.VideoEncoder,
                        Filters = task.Requirements.Filters,
                        SliceMachines = slices,
                        Machines = machines,
                        AssemblySummary = summary,
                        Check = check,
                        Sensitive = sensitive,
                        Diagnostics = diagnostics,
                    });
                }
                catch (Exception ex)
                {
                    logger.LogDebug("Conversion {Id}: failure report not sent ({Error})", task.Id, ex.Message);
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogDebug("Conversion {Id}: failure report not prepared ({Error})", task.Id, ex.Message);
        }
    }

    /// <summary>
    /// What this installation must never publish, removed from every text of a report before anything else: the
    /// conversion's paths, file names and title, the folders above them (series, season), the names of the workers
    /// (paired and announced), the server's host names and addresses, and the coordinator's own folders.
    /// </summary>
    private List<string> SensitiveStrings(EncodeTask task)
    {
        var list = new List<string> { task.Input, task.Output, options.DataDir, spool };
        foreach (string path in new[] { task.Input, task.Output })
        {
            list.Add(Path.GetFileName(path));
            list.Add(Path.GetFileNameWithoutExtension(path));
            string? dir = Path.GetDirectoryName(path);
            list.Add(dir ?? "");
            // The two folders above the file (series and season, or the film's folder).
            for (int i = 0; i < 2 && !string.IsNullOrEmpty(dir); i++)
            {
                list.Add(Path.GetFileName(dir));
                dir = Path.GetDirectoryName(dir);
            }
        }
        try
        {
            list.AddRange(dispatcher.Store?.Load().Select(w => w.Name) ?? []);
        }
        catch (Exception) { }
        list.AddRange(dispatcher.Workers.Select(w => w.Identity.Name));
        list.AddRange(dispatcher.Workers.Select(w => w.Hello?.Name ?? ""));
        try
        {
            list.Add(System.Net.Dns.GetHostName());
        }
        catch (Exception) { }
        if (options.PublicUrl is { } pub && Uri.TryCreate(pub, UriKind.Absolute, out var uri))
            list.Add(uri.Host);
        // The administrator's advertised host and the server's addresses (put in the certificate).
        list.AddRange(options.CertificateNames.Where(n => n != "localhost"));
        return list.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToList();
    }

    private void Touch(EncodeTask task, string note)
    {
        task.Note = note;
        task.UpdatedAt = DateTimeOffset.UtcNow;
        Save();
    }

    // Whole-file rewrite through a temporary file and an atomic rename: a crash never leaves a truncated encodes.json.
    // The file is only history (active tasks are cancelled at start-up): failing to write it is logged, never thrown,
    // so it cannot interrupt a state change half-way.
    private void Save()
    {
        lock (gate)
        {
            try
            {
                string tmp = options.EncodeQueuePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(tasks, Json));
                MoveWithRetry(tmp, options.EncodeQueuePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Cannot save {File}: {Error}", options.EncodeQueuePath, ex.Message);
            }
        }
    }

    /// <summary>
    /// Rename over an existing file, retried briefly: on Windows a scanner or indexer may hold a file it has just seen
    /// being written (access denied), which passes within a moment.
    /// </summary>
    private static void MoveWithRetry(string from, string to)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(from, to, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 10)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    private string SpoolDir(string id) => Path.Combine(spool, id);

    private string ChunkPath(string id, int index) => Path.Combine(SpoolDir(id), $"chunk_{index:D5}.ts");

    // The spool of the last conversion whose assembly was rejected, kept for analysis (chunks, concat.txt, chunks.csv).
    private string? keptSpool;

    /// <summary>
    /// A rejected assembly keeps its chunks and lists (only the latest one, an episode's worth of disk) and logs its
    /// chunk table: the cause of a wrong length is in the chunk times, which are gone once the spool is deleted.
    /// Call under the lock.
    /// </summary>
    private void KeepFailedSpool(EncodeTask task)
    {
        if (keptSpool != null && keptSpool != task.Id)
            DeleteSpool(keptSpool);
        keptSpool = task.Id;
        var table = new StringBuilder("slice,slice_start,slice_end,index,start,end,concat_duration\n");
        foreach (var slice in task.Slices.OrderBy(s => s.Index))
        {
            // The length each chunk was given in the concat list (empty past a gap: not assembled).
            var durations = task.ConcatDurations(slice).ToDictionary(d => d.Index, d => d.Duration);
            foreach (var c in task.Chunks.Where(c => c.Index >= slice.FirstChunk && c.Index < slice.FirstChunk + EmbyConversionPlanner.ChunksPerSlice).OrderBy(c => c.Index))
                table.Append(CultureInfo.InvariantCulture, $"{slice.Index + 1},{slice.Start:0.######},{slice.End:0.######},{c.Index},{c.Start:0.######},{c.End:0.######},{(durations.TryGetValue(c.Index, out double d) ? d.ToString("0.######", CultureInfo.InvariantCulture) : "")}\n");
        }
        try
        {
            File.WriteAllText(Path.Combine(SpoolDir(task.Id), "chunks.csv"), table.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        logger.LogWarning("Conversion {Id} rejected ({Error}): spool kept in {Dir} for analysis", task.Id, task.Error, SpoolDir(task.Id));
    }

    private void DeleteSpool(string id)
    {
        // Being analysed for a failure report: deleted when that ends (CollectDiagnosticsAsync).
        if (spoolPins.DeferDelete(id))
            return;
        try
        {
            if (Directory.Exists(SpoolDir(id)))
                Directory.Delete(SpoolDir(id), true);
        }
        catch (IOException ex)
        {
            logger.LogWarning("Cannot clean {Dir}: {Error}", SpoolDir(id), ex.Message);
        }
    }
}
