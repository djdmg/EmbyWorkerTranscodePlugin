using System.Diagnostics;
using System.Globalization;

namespace RemoteTranscode.Coordinator;

/// <summary>Runs one of the server's tools (ffprobe, ffmpeg) with the given arguments; tests replace it with canned output.</summary>
public delegate Task<(int Code, string Output, string Error)> ToolRunner(IReadOnlyList<string> args, CancellationToken token);

/// <summary>
/// Spools being analysed for a report: a pinned spool is not deleted, the deletion waits for the analysis to end. So the
/// next rejected assembly (which replaces the kept spool) or a conversion given back cannot pull the chunks away.
/// </summary>
public sealed class SpoolPins
{
    private readonly object gate = new();
    private readonly Dictionary<string, bool> pins = [];

    public void Pin(string id)
    {
        lock (gate)
            pins.TryAdd(id, false);
    }

    /// <summary>True when the spool is pinned: the caller must not delete it now; <see cref="Unpin"/> will say so later.</summary>
    public bool DeferDelete(string id)
    {
        lock (gate)
        {
            if (!pins.ContainsKey(id))
                return false;
            pins[id] = true;
            return true;
        }
    }

    /// <summary>Ends the analysis of a spool: true when a deletion was asked for meanwhile, which is now the caller's to do.</summary>
    public bool Unpin(string id)
    {
        lock (gate)
            return pins.Remove(id, out bool deleteAsked) && deleteAsked;
    }
}

/// <summary>What the coordinator noted about a conversion for its report; numbers and machine types only. Changed under the queue's lock.</summary>
public sealed class ConversionDiagnosticsLog
{
    public Dictionary<int, int> Runs { get; } = [];
    public Dictionary<int, int> Resumes { get; } = [];
    /// <summary>Machine type (never a name) → the first line of its ffmpeg's version output.</summary>
    public Dictionary<string, string?> MachineFfmpeg { get; } = [];
    public List<FailingRun> FailingRuns { get; } = [];
    public string[]? AssemblyTemplate { get; set; }
    /// <summary>Machine type → the file's start as that machine's own ffmpeg read it (first run of that type).</summary>
    public Dictionary<string, double> FileStarts { get; } = [];

    public void RunStarted(int sliceIndex, bool resume)
    {
        Runs[sliceIndex] = Runs.GetValueOrDefault(sliceIndex) + 1;
        if (resume)
            Resumes[sliceIndex] = Resumes.GetValueOrDefault(sliceIndex) + 1;
    }
}

/// <summary>A chunk as the coordinator listed it (the content of chunks.csv).</summary>
public sealed record DiagnosticChunk(int Index, double ListedStart, double ListedEnd, double? ConcatDuration, long Size);

/// <summary>A slice as the coordinator knows it, taken under the queue's lock.</summary>
public sealed record DiagnosticSlice(int Number, double Start, double End, bool Last, bool Done, string? Machine, int Runs, int Resumes, IReadOnlyList<DiagnosticChunk> Chunks);

/// <summary>A failing run: which type of machine, which slice, ffmpeg's last lines.</summary>
public sealed record FailingRun(string? Machine, int? Slice, IReadOnlyList<string> Lines);

/// <summary>A command of the conversion, as arguments with placeholders; sanitized when rendered (<see cref="CommandSanitizer"/>).</summary>
public sealed record DiagnosticCommand(string Label, IReadOnlyList<string> Args);

/// <summary>Everything the analysis needs, copied under the queue's lock. The paths stay on the server: none is ever in a result.</summary>
public sealed record DiagnosticsRequest
{
    public required FailureKind Kind { get; init; }
    public required string Error { get; init; }
    public string? Check { get; init; }
    public required string SourcePath { get; init; }
    public required string SpoolId { get; init; }
    /// <summary>Chunks to measure; null: none (the conversion goes on, its spool is left alone).</summary>
    public string? SpoolDir { get; init; }
    public IReadOnlyList<DiagnosticSlice> Slices { get; init; } = [];
    public IReadOnlyList<DiagnosticCommand> Commands { get; init; } = [];
    public IReadOnlyDictionary<string, string?> MachineFfmpeg { get; init; } = new Dictionary<string, string?>();
    public string? ToneMapper { get; init; }
    public string? AudioState { get; init; }
    public string? AudioOffset { get; init; }
    public double? SeekMargin { get; init; }
    /// <summary>Frame time set by Emby's -r, null without.</summary>
    public double? OutputFrame { get; init; }
}

public enum DiagnosticsStatus
{
    Completed,
    /// <summary>The time limit struck: what was collected before is reported.</summary>
    TimedOut,
    /// <summary>The host is stopping.</summary>
    Cancelled,
    /// <summary>The previous analysis held the slot for the whole time limit.</summary>
    Skipped,
}

/// <summary>Result of the automatic analysis of a failure: numbers and allow-listed codec-level words, never a path, name or tag.</summary>
public sealed record DiagnosticsResult
{
    public DiagnosticsStatus Status { get; init; }
    public double Seconds { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    public required Verdict Verdict { get; init; }
    public string? ServerFfmpeg { get; init; }
    public IReadOnlyDictionary<string, string?> MachineFfmpeg { get; init; } = new Dictionary<string, string?>();
    public SourceFingerprint? Fingerprint { get; init; }
    public PacketStats? Stats { get; init; }
    public IReadOnlyList<PacketInfo> FirstFrames { get; init; } = [];
    public StreamEnds? Ends { get; init; }
    public IReadOnlyList<BoundarySample> Boundaries { get; init; } = [];
    public IReadOnlyList<SliceMeasure> Slices { get; init; } = [];
    public IReadOnlyList<ChunkRow> Chunks { get; init; } = [];
    public int ChunksMeasured { get; init; }
    public int ChunksListed { get; init; }
    public Recipe? Recipe { get; init; }
    public IReadOnlyList<DiagnosticCommand> Commands { get; init; } = [];
    public string? ToneMapper { get; init; }
    public string? AudioState { get; init; }
    public string? AudioOffset { get; init; }
    public IReadOnlyList<FailingRun> FailingRuns { get; init; } = [];
    public double? SeekMargin { get; init; }
    /// <summary>Machine type → the file's start as its own ffmpeg read it, when reported. The fingerprint is only the server's ffprobe.</summary>
    public IReadOnlyDictionary<string, double> FileStarts { get; init; } = new Dictionary<string, double>();
}

/// <summary>
/// Runs the diagnostics of a failure with Emby's own tools, at idle priority, one analysis at a time and within a time
/// limit. Never throws, and always returns what it has: a step that fails or the time limit only leaves its part empty
/// and adds a note. Exception messages are not kept (they may hold a path): only their type.
/// </summary>
public sealed class FailureDiagnostics(ToolRunner ffprobe, ToolRunner ffmpeg, TimeSpan? timeLimit = null)
{
    public static readonly TimeSpan DefaultTimeLimit = TimeSpan.FromSeconds(150);
    /// <summary>Packets read from the start of the source for the CFR check and the GOP length.</summary>
    public const int SamplePackets = 600;
    /// <summary>Seconds read before the end of the source to find where each stream ends.</summary>
    public const double EndWindowSeconds = 120;
    public const int MaxBoundaries = 6;

    private readonly TimeSpan limit = timeLimit ?? DefaultTimeLimit;
    private readonly SemaphoreSlim slot = new(1, 1);

    public async Task<DiagnosticsResult> CollectAsync(DiagnosticsRequest request, CancellationToken stop)
    {
        var clock = Stopwatch.StartNew();
        var state = new Collected();
        DiagnosticsStatus status = DiagnosticsStatus.Completed;
        bool holding = false;
        try
        {
            holding = await slot.WaitAsync(limit, stop);
            if (holding)
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(stop);
                budget.CancelAfter(limit);
                try
                {
                    await RunStepsAsync(request, state, budget.Token);
                }
                catch (OperationCanceledException)
                {
                    status = stop.IsCancellationRequested ? DiagnosticsStatus.Cancelled : DiagnosticsStatus.TimedOut;
                }
            }
            else
                status = DiagnosticsStatus.Skipped;
        }
        catch (OperationCanceledException)
        {
            status = DiagnosticsStatus.Cancelled;
        }
        catch (Exception ex)
        {
            state.Notes.Add($"analysis stopped ({ex.GetType().Name})");
        }
        finally
        {
            if (holding)
                slot.Release();
        }
        return Finish(request, state, status, clock.Elapsed.TotalSeconds);
    }

    /// <summary>What the steps found so far.</summary>
    private sealed class Collected
    {
        public List<string> Notes { get; } = [];
        public string? ServerFfmpeg;
        public SourceFingerprint? Fingerprint;
        public PacketStats? Stats;
        public List<PacketInfo> FirstFrames = [];
        public StreamEnds? Ends;
        public List<BoundarySample> Boundaries = [];
        public Dictionary<int, ChunkMeasure> Chunks = [];
        public IReadOnlyDictionary<int, int>? SourceFrames;
    }

    private async Task RunStepsAsync(DiagnosticsRequest r, Collected s, CancellationToken token)
    {
        await Step(s, "ffmpeg version", token, async () =>
        {
            var (code, output, _) = await ffmpeg(["-hide_banner", "-version"], token);
            if (code == 0)
                s.ServerFfmpeg = DiagnosticsAnalysis.FfmpegVersionToken(output);
        });
        await Step(s, "source fingerprint", token, async () =>
            s.Fingerprint = DiagnosticsAnalysis.ParseFingerprint(await Probe(FingerprintArgs(r.SourcePath), token)));
        await Step(s, "packet sample", token, async () =>
        {
            var packets = DiagnosticsAnalysis.ParsePackets(await Probe(SampleArgs(r.SourcePath), token));
            s.Stats = DiagnosticsAnalysis.Stats(packets);
            s.FirstFrames = packets.Take(24).ToList();
        });
        await Step(s, "end of the streams", token, async () =>
        {
            double from = Math.Max(0, (s.Fingerprint?.Start ?? 0) + (s.Fingerprint?.Duration ?? 0) - EndWindowSeconds);
            // ffprobe (9.0.2 measured) sometimes prints no packet at all and exits 0: one more try, and it says so.
            for (int attempt = 0; attempt < 2 && s.Ends is not { Video: not null } && s.Ends is not { Audio.Count: > 0 }; attempt++)
                s.Ends = DiagnosticsAnalysis.ParseEnds(await Probe(EndArgs(r.SourcePath, from), token), s.Fingerprint);
            if (s.Ends is { Video: null, Audio.Count: 0 })
                s.Notes.Add("end of the streams: ffprobe read no packet");
        });
        if (r.SpoolDir is { } spool)
        {
            await MeasureChunksAsync(r, spool, s, token);
            // A variable frame rate gives no expected count from a length, unless Emby's -r sets a grid: the source's frames are counted.
            if (s.Stats?.ConstantRate == false && r.OutputFrame is null && r.Slices.Count > 0)
                await Step(s, "source frames per slice", token, async () =>
                    s.SourceFrames = DiagnosticsAnalysis.CountSourceFrames(await Probe(AllPtsArgs(r.SourcePath), token), s.Fingerprint?.Start ?? 0, r.Slices));
        }
        var measures = DiagnosticsAnalysis.Slices(r.Slices, s.Chunks, s.Fingerprint, s.Stats, s.Ends, s.SourceFrames, r.OutputFrame);
        var times = DiagnosticsAnalysis.PickBoundaries(measures, MaxBoundaries);
        if (times.Count > 0)
            await Step(s, "frames around slice boundaries", token, async () =>
            {
                double start = s.Fingerprint?.Start ?? 0;
                var packets = DiagnosticsAnalysis.ParsePackets(await Probe(BoundaryArgs(r.SourcePath, times.Select(t => t.Time + start)), token));
                s.Boundaries = DiagnosticsAnalysis.Around(packets, times, start);
            });
    }

    private async Task MeasureChunksAsync(DiagnosticsRequest r, string spool, Collected s, CancellationToken token)
    {
        int failed = 0;
        foreach (var chunk in r.Slices.SelectMany(x => x.Chunks))
        {
            token.ThrowIfCancellationRequested();
            string path = Path.Combine(spool, $"chunk_{chunk.Index:D5}.ts");
            if (!File.Exists(path))
            {
                s.Chunks[chunk.Index] = new ChunkMeasure(chunk.Index, 0, null, null, null, Missing: true);
                continue;
            }
            try
            {
                s.Chunks[chunk.Index] = DiagnosticsAnalysis.MeasureChunk(chunk.Index, await Probe(ChunkArgs(path), token));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
            }
        }
        if (failed > 0)
            s.Notes.Add($"{failed} chunk(s) could not be read by ffprobe");
    }

    private async Task<string> Probe(IReadOnlyList<string> args, CancellationToken token)
    {
        var (code, output, _) = await ffprobe(args, token);
        // ffprobe's error output names the file: it is not kept.
        return code == 0 ? output : throw new InvalidOperationException($"ffprobe code {code}");
    }

    private static async Task Step(Collected s, string name, CancellationToken token, Func<Task> action)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            s.Notes.Add($"{name}: failed ({ex.GetType().Name})");
        }
    }

    // Only named fields are ever asked for: no tags, no metadata, no chapter titles, no file name.
    public static IReadOnlyList<string> FingerprintArgs(string path) =>
        ["-v", "error", "-print_format", "json", "-show_entries",
         "format=format_name,start_time,duration,bit_rate:stream=index,codec_type,codec_name,profile,level,has_b_frames,width,height,pix_fmt,bits_per_raw_sample,field_order,time_base,start_time,duration,r_frame_rate,avg_frame_rate,nb_frames,color_transfer,color_primaries,color_space,color_range,sample_rate,channels,channel_layout:stream_disposition=attached_pic:chapter=id",
         path];

    public static IReadOnlyList<string> SampleArgs(string path) =>
        ["-v", "error", "-select_streams", "V:0", "-read_intervals", $"%+#{SamplePackets}", "-print_format", "json",
         "-show_entries", "packet=pts_time,dts_time,duration_time,size,flags", path];

    public static IReadOnlyList<string> EndArgs(string path, double from) =>
        // With an end: without one and without -select_streams, ffprobe 9.0.2 printed no packet in 1 run out of 7 or so.
        ["-v", "error", "-read_intervals", $"{Time(from)}%{Time(from + EndWindowSeconds * 1000)}", "-print_format", "json",
         "-show_entries", "packet=stream_index,pts_time,duration_time", path];

    public static IReadOnlyList<string> AllPtsArgs(string path) =>
        ["-v", "error", "-select_streams", "V:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0", path];

    public static IReadOnlyList<string> BoundaryArgs(string path, IEnumerable<double> times) =>
        ["-v", "error", "-select_streams", "V:0", "-read_intervals", string.Join(",", times.Select(t => $"{Time(Math.Max(0, t - 1))}%{Time(t + 1)}")),
         "-print_format", "json", "-show_entries", "packet=pts_time,dts_time,duration_time,size,flags", path];

    public static IReadOnlyList<string> ChunkArgs(string path) =>
        ["-v", "error", "-select_streams", "v:0", "-print_format", "json", "-show_entries", "packet=pts_time,duration_time,flags", path];

    private static string Time(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private static DiagnosticsResult Finish(DiagnosticsRequest r, Collected s, DiagnosticsStatus status, double seconds)
    {
        IReadOnlyList<SliceMeasure> slices = [];
        Verdict verdict = new(VerdictClass.Undetermined, "the measures could not be analysed.");
        Recipe? recipe = null;
        var notes = new List<string>(s.Notes);
        try
        {
            slices = DiagnosticsAnalysis.Slices(r.Slices, s.Chunks, s.Fingerprint, s.Stats, s.Ends, s.SourceFrames, r.OutputFrame);
            verdict = DiagnosticsAnalysis.Decide(r.Kind, r.Error, r.Check, slices, s.Fingerprint, s.Stats, s.Ends, r.SpoolDir != null, status == DiagnosticsStatus.Completed);
            recipe = s.Fingerprint is null ? null : RecipeBuilder.Build(s.Fingerprint, s.Stats, s.Ends);
        }
        catch (Exception ex)
        {
            notes.Add($"analysis of the measures failed ({ex.GetType().Name})");
        }
        return new DiagnosticsResult
        {
            Status = status,
            Seconds = seconds,
            Notes = notes,
            Verdict = verdict,
            ServerFfmpeg = s.ServerFfmpeg,
            MachineFfmpeg = r.MachineFfmpeg.ToDictionary(p => p.Key, p => DiagnosticsAnalysis.FfmpegVersionToken(p.Value)),
            Fingerprint = s.Fingerprint,
            Stats = s.Stats,
            FirstFrames = s.FirstFrames,
            Ends = s.Ends,
            Boundaries = s.Boundaries,
            Slices = slices,
            Chunks = DiagnosticsAnalysis.ChunkRows(r.Slices, s.Chunks),
            ChunksMeasured = s.Chunks.Values.Count(c => !c.Missing),
            ChunksListed = r.Slices.Sum(x => x.Chunks.Count),
            Recipe = recipe,
            Commands = r.Commands,
            ToneMapper = r.ToneMapper,
            AudioState = r.AudioState,
            AudioOffset = r.AudioOffset,
            SeekMargin = r.SeekMargin,
        };
    }
}
