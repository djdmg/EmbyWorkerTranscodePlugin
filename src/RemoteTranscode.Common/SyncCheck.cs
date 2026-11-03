using System.Globalization;
using System.Text.Json;

namespace RemoteTranscode.Common;

/// <summary>A packet's times as ffprobe lists them, in the file's own time. Pts is null when it gives none (AVI with packed B-frames).</summary>
public readonly record struct ProbedPacket(double? Pts, double? Dts, double? Duration)
{
    public double? Time => Pts ?? Dts;
}

/// <summary>A stream of a file as ffprobe reads it, with the packets read of it.</summary>
public sealed record ProbedStream(int Index, string? Type, string? Codec, int SampleRate, double? Start, double FrameSeconds, bool Cover, IReadOnlyList<ProbedPacket> Packets)
{
    /// <summary>From the average frame rate, which holes in a video count in: the length check's unit. Frame time otherwise.</summary>
    public double AverageFrameSeconds { get; init; } = FrameSeconds;

    /// <summary>Real video: cover art is stored as a video stream too.</summary>
    public bool IsVideo => Type == "video" && !Cover;
    public bool IsAudio => Type == "audio";
}

/// <summary>
/// What ffprobe says of a file for <see cref="SyncCheck"/>. <see cref="MetadataArgs"/> reads the streams without any packet,
/// <see cref="PacketArgs"/> the packets of some windows, without decoding anything.
/// </summary>
public sealed record MediaProbe(double? FileStart, double? Duration, string? Format, int? Height, IReadOnlyList<ProbedStream> Streams)
{
    private const string Entries = "format=format_name,start_time,duration:stream=index,codec_type,codec_name,sample_rate,start_time,r_frame_rate,avg_frame_rate,height:stream_disposition=attached_pic";

    public static string[] MetadataArgs(string path) => ["-v", "error", "-show_entries", Entries, "-print_format", "json", path];

    /// <summary>
    /// The packets of every stream in these intervals, one ffprobe pass that seeks to each of them. A bounded interval each:
    /// ffprobe 9 sometimes prints no packet at all, without error, for an open-ended one.
    /// </summary>
    public static string[] PacketArgs(string path, string intervals) =>
        ["-v", "error", "-read_intervals", intervals, "-show_entries", Entries + ":packet=stream_index,pts_time,dts_time,duration_time", "-print_format", "json", path];

    public ProbedStream? Video => Streams.FirstOrDefault(s => s.IsVideo);

    public IReadOnlyList<ProbedStream> Audio => Streams.Where(s => s.IsAudio).ToList();

    /// <summary>The packets of another reading of the same file added to these: windows may overlap, and a packet read twice counts once, by its time (Matroska gives no decoding time right after a seek).</summary>
    public MediaProbe Merge(MediaProbe other) => this with
    {
        Streams = Streams.Select(s => s with
        {
            Packets = s.Packets.Concat(other.Streams.FirstOrDefault(o => o.Index == s.Index)?.Packets ?? [])
                .DistinctBy(p => p.Time).ToList(),
        }).ToList(),
    };

    /// <summary>MPEG-TS seeks on its PCR, ahead of the packets' times by the muxer's delay, and may wrap or jump: its own rules apply.</summary>
    public bool IsMpegTs => Format?.Contains("mpegts", StringComparison.Ordinal) == true;

    /// <summary>ffprobe's JSON; what it does not give (start_time "N/A" or absent) stays null. Throws when it is not JSON.</summary>
    public static MediaProbe Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        double? fileStart = null, duration = null;
        string? format = null;
        if (root.TryGetProperty("format", out var f))
        {
            fileStart = Number(f, "start_time");
            duration = Number(f, "duration");
            format = Text(f, "format_name");
        }
        var packets = new Dictionary<int, List<ProbedPacket>>();
        if (root.TryGetProperty("packets", out var list))
            foreach (var p in list.EnumerateArray())
            {
                int index = p.TryGetProperty("stream_index", out var si) && si.ValueKind == JsonValueKind.Number ? si.GetInt32() : -1;
                if (!packets.TryGetValue(index, out var own))
                    packets[index] = own = [];
                own.Add(new ProbedPacket(Number(p, "pts_time"), Number(p, "dts_time"), Number(p, "duration_time")));
            }
        var streams = new List<ProbedStream>();
        int? height = null;
        if (root.TryGetProperty("streams", out var ss))
            foreach (var s in ss.EnumerateArray())
            {
                int index = s.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : -1;
                bool cover = s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var ap) && ap.ValueKind == JsonValueKind.Number && ap.GetInt32() == 1;
                string? type = Text(s, "codec_type");
                // The frame rate the stream claims: r_frame_rate is the exact rational (avg_frame_rate of a file with holes is not its grid).
                double? average = Frame(Text(s, "avg_frame_rate"));
                double frame = Frame(Text(s, "r_frame_rate")) ?? average ?? 0;
                if (type == "video" && !cover && height is null && s.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number)
                    height = h.GetInt32();
                streams.Add(new ProbedStream(index, type, Text(s, "codec_name"), (int)(Number(s, "sample_rate") ?? 0), Number(s, "start_time"), frame, cover,
                    packets.TryGetValue(index, out var own) ? own : []) { AverageFrameSeconds = average ?? frame });
            }
        return new MediaProbe(fileStart, duration, format, height, streams);

        static double? Frame(string? rate) =>
            rate?.Split('/') is [var n, var m] && double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out double num)
            && double.TryParse(m, NumberStyles.Float, CultureInfo.InvariantCulture, out double den) && num > 0 && den > 0 ? den / num : null;
    }

    /// <summary>
    /// The ffprobe arguments for the first frame the decoder gives (see <see cref="ParseFirstFrame"/>), reading a few dozen packets or, when
    /// those gave nothing (an MPEG-TS cut far from a keyframe), the first seconds. They really are decoded: with -skip_frame nokey an AVI's
    /// first frame reads seconds late (1.96 s for 0.12 s, ffmpeg 9.0.2), because its time comes from the packets the decoder went through.
    /// </summary>
    public static string[] FirstFrameArgs(string path, bool further = false) =>
        ["-v", "error", "-select_streams", "V:0", "-read_intervals", further ? "%+15" : "%+#32",
         "-show_entries", "frame=best_effort_timestamp_time:format=start_time", "-print_format", "json", path];

    /// <summary>The first frame's time (see <see cref="ParseFirstFrame"/>), with ffprobe behind <paramref name="probe"/>.</summary>
    public static async Task<double?> FirstFrameAsync(Func<string[], Task<string>> probe, string path) =>
        ParseFirstFrame(await probe(FirstFrameArgs(path))) ?? ParseFirstFrame(await probe(FirstFrameArgs(path, further: true)));

    /// <summary>
    /// Where the first frame comes out of the decoder, from the file's start, null when none did. This, and not the first packet, is
    /// where ffmpeg (and any player) starts the video: an AVI's first packet reads 0 while its first frame comes out at 0.04 to
    /// 0.12 s with B-frames (measured with ffmpeg 9.0.2; a conversion by ffmpeg alone starts there too), and an MPEG-TS cut anywhere
    /// starts with packets that do not decode.
    /// </summary>
    public static double? ParseFirstFrame(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        double start = root.TryGetProperty("format", out var f) && Number(f, "start_time") is { } s ? s : 0;
        double? first = null;
        if (root.TryGetProperty("frames", out var frames))
            foreach (var e in frames.EnumerateArray())
                if (Number(e, "best_effort_timestamp_time") is { } t && (first is null || t < first))
                    first = t;
        return first - start;
    }

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Number(JsonElement e, string name) =>
        double.TryParse(Text(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
}

/// <summary>The check's outcome; the spans are null when the video's end could not be read.</summary>
public sealed record SyncVerdict(string? Problem, string Summary, VideoSpan? Source, VideoSpan? Result);

/// <summary>What the check needs to know of the assembly besides the two files.</summary>
/// <param name="Maps">The source streams Emby's command maps, in order, without the chunks' video.</param>
/// <param name="Encoders">Audio encoder asked for each audio stream of the result, in order ("copy" for a copied stream).</param>
/// <param name="OutputFrame">Frame time set by Emby's -r, null without.</param>
public sealed record SyncExpectation(IReadOnlyList<int> Maps, IReadOnlyList<string?> Encoders, double? OutputFrame)
{
    /// <summary>From Emby's usual assembly, the one whose encoders say what the sound went through (audio converted ahead included).</summary>
    public static SyncExpectation? From(IReadOnlyList<string> assembly, IReadOnlyList<string> chunkTemplate) =>
        EmbyConversionPlanner.AssemblySourceMaps(assembly) is { } maps
            ? new SyncExpectation(maps, EmbyConversionPlanner.AssemblyAudioEncoders(assembly, maps.Count), EmbyConversionPlanner.OutputFrameTime(chunkTemplate))
            : null;
}

/// <summary>
/// Is the result of an assembly in step with its source, from its first frame to its last? Whatever cannot be proved is not delivered:
/// a value that is missing or unreadable rejects the file, it goes to Emby's own command, slower but correct. Commands whose result
/// could not be compared at all never get here (<see cref="EmbyConversionPlanner.WhyResultCannotBeVerified"/>).
/// <para>
/// Only packets are read, in windows through the file (<see cref="WindowTimes"/>), no decoding: a few seconds on a two-hour film. All
/// times count from the source's file start, the one <c>-copyts -start_at_zero</c> counts from, so a result has the same numbers as its
/// source. Rules, in seconds:
/// </para>
/// <list type="bullet">
/// <item>Video start: the result's first frame is where the source's first frame comes out of the decoder (see
/// <see cref="MediaProbe.ParseFirstFrame"/>), within <see cref="Tolerance"/>.</item>
/// <item>Video through the file: each source frame in a window has a result frame within <see cref="Tolerance"/> when both run at the same
/// rate and the source lies on its own grid; within half an output frame (plus the tolerance) otherwise, because ffmpeg's frame
/// grid puts frames there on purpose. Two frames closer than a quarter of a frame are an overlap.</item>
/// <item>Video end: the last frame is where the source's is, under the same tolerance, one output frame when the rates differ.</item>
/// <item>Audio start: the first packet is exactly at the source's start minus the priming of the encoder used
/// (<see cref="AudioPriming"/>): the stream's start_time cannot be used, it reads 0 for anything under the priming.</item>
/// <item>Audio through the file, each stream on its own: a copied stream has the source's packets at the source's times. An encoded one
/// has other packets, so times, not counts: sound in the same places (holes included), and packets on the grid its first packet set,
/// which any shift by a fraction of a packet leaves.</item>
/// <item>Audio end: twice the tolerance for an encoded stream (what shows a shift by whole packets), a packet for a copied one and later
/// than the source for an encoder padding its last packet.</item>
/// </list>
/// </summary>
public static class SyncCheck
{
    /// <summary>
    /// Largest difference allowed where the positions are exact. Measured with ffmpeg 9.0.2 on real assemblies at the same rate as their
    /// source, Matroska included: 0.9 ms at most between a frame and its source frame, decoded and matched one by one; 0 to 1 ms for an audio
    /// stream's first packet (Matroska rounds to the millisecond on each side, MP4's edit list in its default movie time scale too).
    /// 3 ms covers both ends' rounding with room for Emby's 5.1, which test/local/nas-sync-probe.sh measures.
    /// </summary>
    public const double Tolerance = 0.003;

    // A source frame is on its grid within Matroska's rounding of its time, and of the first frame's.
    private const double OnGrid = 0.002;

    // The video may lie 6 ms off: Emby's ffprobe and the workers' ffmpeg can read a file's zero 5 ms apart, a constant shift nobody can perceive
    // (accepted by the owner). Well under the 45 ms from which a shift is noticed (ITU-R BT.1359); the audio keeps Tolerance.
    private const double VideoTolerance = 0.006;

    /// <summary>
    /// Most windows read in a file, on top of its start and end: enough for every chunk boundary of a 10-hour file at 30 s a chunk, since a
    /// slice with no window could be wrong by any amount and be right again at its end.
    /// </summary>
    public const int MaxWindows = 1200;

    /// <summary>What is compared in each window. Long enough for a few dozen frames and audio packets, whatever their lengths.</summary>
    public const double WindowSeconds = 1.5;

    /// <summary>Read before a window: an MPEG-TS seek lands up to 1.4 s late (PCR ahead of the times), MP4 and Matroska on the keyframe before.</summary>
    public const double PreRollTs = 2.5;

    /// <summary>Read after a window: a stream may be ahead of another in the file, and reading stops on the first packet past the interval.</summary>
    public const double PostRoll = 2;

    /// <summary>Read at the end of a file; a stream ending before that is looked for further (see <see cref="MissingEnds"/>).</summary>
    public const double TailSeconds = 30;

    /// <summary>Second try for a stream's end: a long read, for few files.</summary>
    public const double LongTailSeconds = 600;

    /// <summary>
    /// Seconds of priming the encoder puts before the sound, in samples, measured on ffmpeg 9.0.2 on sound starting at 0, 3, 15, 21, 100
    /// and 500 ms: the first packet lies that much before the sound. Null: not measured, the planner keeps it on Emby's ffmpeg.
    /// </summary>
    public static int? AudioPriming(string? encoder) => encoder switch
    {
        "copy" => 0,
        "aac" => 1024,
        "libmp3lame" or "mp3" => 1105,
        "ac3" or "eac3" => 256,
        "libopus" or "opus" => 312,
        "mp2" or "libtwolame" => 481,
        "libvorbis" or "vorbis" => 128,
        "flac" or "alac" or "dca" or "dts" => 0,
        _ when encoder?.StartsWith("pcm_", StringComparison.Ordinal) == true => 0,
        _ => null,
    };

    /// <summary>
    /// Times (from the file's start) of the windows to read: the concat list's boundaries, where a wrong length would show first, evenly
    /// picked up to <see cref="MaxWindows"/>, and at least the quarters of the file. Each window straddles its boundary.
    /// </summary>
    public static IReadOnlyList<double> WindowTimes(double duration, IEnumerable<double> boundaries)
    {
        var inside = boundaries.Where(b => b > WindowSeconds && b < duration - WindowSeconds).OrderBy(b => b).ToList();
        var chosen = new List<double>();
        if (inside.Count <= MaxWindows)
            chosen.AddRange(inside);
        else
            for (int i = 0; i < MaxWindows; i++)
                chosen.Add(inside[(int)((i + 0.5) * inside.Count / MaxWindows)]);
        foreach (double quarter in new[] { 0.25, 0.5, 0.75 })
            if (duration > 4 * WindowSeconds && !chosen.Any(c => Math.Abs(c - duration * quarter) < duration / 8))
                chosen.Add(duration * quarter);
        return chosen.Select(c => Math.Max(0, c - WindowSeconds / 2)).OrderBy(c => c).ToList();
    }

    /// <summary>The ends of the chunks of a concat list on the result's timeline: the video starts at <paramref name="offset"/>, each chunk lasts as listed.</summary>
    public static IReadOnlyList<double> ListBoundaries(string list, double offset)
    {
        var boundaries = new List<double>();
        double t = offset;
        foreach (string line in list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (line.StartsWith("duration ", StringComparison.Ordinal)
                && double.TryParse(line.AsSpan(9), NumberStyles.Float, CultureInfo.InvariantCulture, out double length))
                boundaries.Add(t += length);
        return boundaries;
    }

    /// <summary>
    /// The -read_intervals of a file: its first seconds, each window and each stream's start (a stream starting late may be far from the
    /// first seconds), and its end. In the file's own time, so offset by its start.
    /// </summary>
    public static string Intervals(MediaProbe file, IReadOnlyList<double> windows)
    {
        double start = file.FileStart ?? 0;
        double duration = file.Duration ?? 0;
        double pre = file.IsMpegTs ? PreRollTs : 0.5;
        var parts = new List<string> { "%+#400" };
        foreach (var s in file.Streams.Where(s => (s.IsVideo || s.IsAudio) && s.Start is > 0))
            if (s.Start - start > 3)
                parts.Add(Interval(Math.Max(0, s.Start!.Value - pre), s.Start!.Value + 1 + PostRoll));
        parts.AddRange(windows.Select(w => Interval(Math.Max(0, start + w - pre), start + w + WindowSeconds + PostRoll)));
        parts.Add(Interval(Math.Max(0, start + duration - TailSeconds), start + duration + 3600));
        return string.Join(",", parts);

        static string Interval(double from, double to) => F(from) + "%" + F(to);
    }

    /// <summary>
    /// The streams whose end was not seen in the last seconds read: they end before that (a video 34 s shorter than its audio), or the
    /// reading failed.
    /// </summary>
    public static IReadOnlyList<ProbedStream> MissingEnds(MediaProbe file, double lastSeconds)
    {
        double from = (file.FileStart ?? 0) + (file.Duration ?? 0) - lastSeconds;
        return file.Streams.Where(s => s.IsVideo || s.IsAudio).Where(s => !s.Packets.Any(p => p.Time >= from)).ToList();
    }

    /// <summary>The last seconds of a file, for the streams <see cref="TailSeconds"/> did not reach.</summary>
    public static string[] LongTailArgs(MediaProbe file, string path) =>
        MediaProbe.PacketArgs(path, F(Math.Max(0, (file.FileStart ?? 0) + (file.Duration ?? 0) - LongTailSeconds)) + "%" + F((file.FileStart ?? 0) + (file.Duration ?? 0) + 3600));

    /// <summary>All the packets of one stream, for a stream ending further from the end than that: a whole read, rare and long, of that stream only.</summary>
    public static string[] WholeStreamArgs(string path, int stream) =>
        ["-v", "error", "-select_streams", stream.ToString(CultureInfo.InvariantCulture), "-show_entries", "packet=stream_index,pts_time,dts_time,duration_time", "-print_format", "json", path];

    /// <summary>
    /// The whole check, with ffprobe behind <paramref name="probe"/> (standard output of a run; it throws when the run failed). Null when the
    /// file is proved in step; any failure to read is a reason to reject it.
    /// </summary>
    public static async Task<SyncVerdict> RunAsync(Func<string[], Task<string>> probe, string sourcePath, string resultPath,
        SyncExpectation expected, IReadOnlyList<double> boundaries)
    {
        var sourceInfo = MediaProbe.Parse(await probe(MediaProbe.MetadataArgs(sourcePath)));
        var resultInfo = MediaProbe.Parse(await probe(MediaProbe.MetadataArgs(resultPath)));
        if (sourceInfo.Duration is not > 0 || resultInfo.Duration is not > 0)
            return new SyncVerdict("vérification impossible : durée illisible", "", null, null);
        double? first = await MediaProbe.FirstFrameAsync(probe, sourcePath);
        var windows = WindowTimes(Math.Max(sourceInfo.Duration.Value, resultInfo.Duration.Value), boundaries);
        var source = await ReadAsync(probe, sourcePath, sourceInfo, windows);
        var result = await ReadAsync(probe, resultPath, resultInfo, windows);
        string summary = Reason($"{windows.Count} fenêtres, première image source {first:0.000}s");
        VideoSpan? sourceSpan = Span(source, first), resultSpan = Span(result, null);
        string? problem = sourceSpan is null || resultSpan is null
            ? $"vérification impossible : fin de la vidéo illisible ({(sourceSpan is null ? "source" : "résultat")})"
            : SourceInfo.VerifyVideoSpan(sourceSpan, resultSpan) ?? Verify(source, first, result, expected, windows);
        return new SyncVerdict(problem, summary, sourceSpan, resultSpan);
    }

    // Where the video starts and ends, for the length check. Null when its end could not be read.
    private static VideoSpan? Span(MediaProbe file, double? first)
    {
        if (file.Video is not { } video || video.Packets.Count == 0)
            return null;
        double start = file.FileStart ?? 0;
        double frame = video.AverageFrameSeconds;
        var end = video.Packets.Select(p => p.Time + (p.Duration ?? frame)).Max();
        double? from = first ?? video.Start - start;
        var span = new VideoSpan(from ?? 0, (end ?? double.NaN) - start, frame, video.Packets.Any(p => p.Pts is null));
        return span.IsKnown ? span : null;
    }

    private static async Task<MediaProbe> ReadAsync(Func<string[], Task<string>> probe, string path, MediaProbe info, IReadOnlyList<double> windows)
    {
        var file = info.Merge(MediaProbe.Parse(await probe(MediaProbe.PacketArgs(path, Intervals(info, windows)))));
        if (MissingEnds(file, TailSeconds).Count > 0)
            file = file.Merge(MediaProbe.Parse(await probe(LongTailArgs(file, path))));
        foreach (var stream in MissingEnds(file, LongTailSeconds))
            file = file.Merge(MediaProbe.Parse(await probe(WholeStreamArgs(path, stream.Index))));
        return file;
    }

    /// <summary>
    /// The verdict: null when everything was proved, otherwise the reason. <paramref name="firstFrame"/> is where the source's first frame
    /// comes out of the decoder, from its file's start.
    /// </summary>
    public static string? Verify(MediaProbe source, double? firstFrame, MediaProbe result, SyncExpectation expected, IReadOnlyList<double> windows)
    {
        if (source.FileStart is not { } fileStart)
            return "vérification impossible : début du fichier source illisible";
        var from = new List<ProbedStream>();
        foreach (int map in expected.Maps)
        {
            if (source.Streams.FirstOrDefault(s => s.Index == map) is not { } stream)
                return $"vérification impossible : piste {map} absente de la source";
            if (stream.IsAudio)
                from.Add(stream);
        }
        var to = result.Audio;
        if (from.Count != to.Count)
            return $"{to.Count} piste(s) audio au lieu de {from.Count}";
        if (source.Video is not { } sourceVideo || result.Video is not { } resultVideo)
            return "vérification impossible : piste vidéo introuvable";
        return VerifyVideo(sourceVideo, fileStart, firstFrame, resultVideo, expected.OutputFrame, windows)
            ?? Enumerable.Range(0, to.Count)
                .Select(i => VerifyAudio(i, from[i], fileStart, to[i], i < expected.Encoders.Count ? expected.Encoders[i] : null, windows))
                .FirstOrDefault(problem => problem != null);
    }

    // ---- Video ----

    private static string? VerifyVideo(ProbedStream source, double fileStart, double? firstFrame, ProbedStream result, double? outputFrame, IReadOnlyList<double> windows)
    {
        if (firstFrame is not { } first)
            return "vérification impossible : première image de la source illisible";
        // AVI without presentation times: decoding times, on the frame index. The decoder gives the first frame later than its packet
        // (first frame 0.08 s for a first packet at 0, 2 B-frames), and every other frame by as much.
        bool approximate = source.Packets.Any(p => p.Pts is null);
        double dtsDelay = 0;
        if (approximate)
        {
            if (source.Packets.Where(p => p.Dts != null).Select(p => p.Dts!.Value).ToList() is not { Count: > 0 } decoding)
                return "vérification impossible : temps des images de la source illisibles";
            dtsDelay = first - (decoding.Min() - fileStart);
        }
        var from = source.Packets.Select(p => approximate ? p.Dts + dtsDelay : p.Pts).Where(t => t != null).Select(t => t!.Value - fileStart).OrderBy(t => t).ToList();
        var to = result.Packets.Where(p => p.Pts != null).Select(p => p.Pts!.Value).OrderBy(t => t).ToList();
        if (from.Count == 0 || to.Count == 0)
            return $"vérification impossible : aucune image lue ({(from.Count == 0 ? "source" : "résultat")})";
        if (Math.Abs(to[0] - first) > VideoTolerance)
            return Reason($"vidéo commençant à {to[0]:0.000}s au lieu de {first:0.000}s : décalée de bout en bout");
        double measured = outputFrame ?? MeanGap(to);
        if (measured <= 0)
            return "vérification impossible : durée d'image du résultat inconnue";
        // One frame rate on both sides, or not (Emby's -r, a source whose frames are not regular).
        double sourceFrame = source.FrameSeconds > 0 ? source.FrameSeconds : MeanGap(from);
        bool sameRate = sourceFrame > 0 && Math.Abs(sourceFrame - measured) <= 0.002 * sourceFrame;
        double end = from[^1];
        // With Emby's -r, ffmpeg lays the frames on a grid of its own: a source starting off it (62 ms at 23.976 fps) lands up to half a frame apart
        // in some slices (20 ms measured), not growing. Accepted by the owner, as with any other source off the grid.
        bool onOutputGrid = outputFrame is null || Math.Abs(Remainder(first, measured)) <= OnGrid;
        // Windows the source's read left empty are not compared: past half of them, the read is what failed, not the source.
        var within = windows.Where(w => w + WindowSeconds <= end).ToList();
        if (within.Count(w => !from.Any(t => t >= w && t <= w + WindowSeconds)) * 2 > within.Count)
            return "vérification impossible : images de la source absentes de la plupart des fenêtres lues";
        foreach (double w in windows)
        {
            var sourceFrames = from.Where(t => t >= w && t <= w + WindowSeconds).ToList();
            if (sourceFrames.Count == 0)
                continue;   // a hole in the source, or past its video's end: the end's rule tells.
            // Half an output frame is what ffmpeg's frame grid does on purpose when frames do not lie on it: at -r 25 on 23.976 fps, measured
            // frame by frame, up to 38 ms early and 39 ms late, half a frame of grid plus up to half a frame of a slice's alignment, a
            // frame kept on included. Nothing tighter is true there. Off the source's own grid (a hole of 85 ms) likewise.
            // At 250 frames a second or more, every time would count as on the grid.
            bool onGrid = sameRate && onOutputGrid && sourceFrame > 2 * OnGrid && sourceFrames.All(t => Math.Abs(Remainder(t - first, sourceFrame)) <= OnGrid);
            double tolerance = onGrid ? VideoTolerance : measured / 2 + Tolerance;
            foreach (double t in sourceFrames)
            {
                double distance = Nearest(to, t);
                if (double.IsNaN(distance) || Math.Abs(distance) > tolerance)
                    return Reason($"vidéo décalée à {t:0.000}s : image la plus proche à {(double.IsNaN(distance) ? double.NaN : distance * 1000):+0;-0} ms (tolérance {tolerance * 1000:0.0} ms)");
            }
            var near = to.Where(t => t >= w && t <= w + WindowSeconds).ToList();
            for (int i = 1; i < near.Count; i++)
                if (near[i] - near[i - 1] < measured / 4)
                    return Reason($"vidéo décalée à {near[i]:0.000}s : images superposées");
        }
        // AVI: the last decoded frame has no time, the end is known to 2 frames at best (see VerifyVideoSpan), and the decoder's delay added to the
        // last decoding time overshoots by up to that delay (0.16 s of delay on packed B-frames: last frame exactly 3 frames before the estimate).
        double endTolerance = approximate ? Math.Max(3 * measured, dtsDelay + measured)
            : sameRate && onOutputGrid && Math.Abs(Remainder(end - first, sourceFrame)) <= OnGrid ? VideoTolerance
            : measured + Tolerance;
        if (Math.Abs(to[^1] - end) > endTolerance)
            return Reason($"dernière image de la vidéo à {to[^1]:0.000}s au lieu de {end:0.000}s");
        return null;
    }

    private static double MeanGap(IReadOnlyList<double> sorted)
    {
        // Between frames in the same window only: the jump from a window to the next is not a frame. The mean of those, with Matroska's
        // rounding averaged out, and without the holes.
        var gaps = sorted.Zip(sorted.Skip(1), (a, b) => b - a).Where(g => g > 0).OrderBy(g => g).ToList();
        if (gaps.Count == 0)
            return 0;
        double median = gaps[gaps.Count / 2];
        var regular = gaps.Where(g => Math.Abs(g - median) <= median / 4).ToList();
        return regular.Count == 0 ? median : regular.Average();
    }

    // The value's distance to the nearest multiple of the step, signed.
    private static double Remainder(double value, double step) => value - Math.Round(value / step) * step;

    // Signed distance from a time to the nearest time of a sorted list; NaN when the list is empty.
    private static double Nearest(List<double> sorted, double time)
    {
        if (sorted.Count == 0)
            return double.NaN;
        int k = sorted.BinarySearch(time);
        if (k >= 0)
            return 0;
        k = ~k;
        double? before = k > 0 ? sorted[k - 1] - time : null, after = k < sorted.Count ? sorted[k] - time : null;
        return before is null ? after!.Value : after is null ? before.Value : -before.Value <= after.Value ? before.Value : after.Value;
    }

    // ---- Audio ----

    private static string? VerifyAudio(int i, ProbedStream source, double fileStart, ProbedStream result, string? encoder, IReadOnlyList<double> windows)
    {
        string name = string.Create(CultureInfo.InvariantCulture, $"audio {i + 1}");
        if (AudioPriming(encoder) is not { } priming)
            return $"vérification impossible : amorce de l'encodeur {encoder ?? "inconnu"} inconnue ({name})";
        bool copied = encoder == "copy";
        var from = source.Packets.Where(p => p.Time != null).Select(p => (Time: p.Time!.Value - fileStart, Length: p.Duration ?? 0)).OrderBy(p => p.Time).ToList();
        var to = result.Packets.Where(p => p.Time != null).Select(p => (Time: p.Time!.Value, Length: p.Duration ?? 0)).OrderBy(p => p.Time).ToList();
        if (from.Count == 0 || to.Count == 0)
            return $"vérification impossible : aucun paquet lu ({name}, {(from.Count == 0 ? "source" : "résultat")})";
        // Where the sound starts: the stream's start (a first packet may lie before it, an AC-3 in Matroska at -5 ms for a stream starting at 0).
        if (source.Start is not { } sourceStart)
            return $"vérification impossible : début de la piste illisible ({name}, source)";
        double start = sourceStart - fileStart;
        // Copied: the same packet, wherever it lies. Encoded: the sound starts there, the first packet lies the priming before.
        if (priming > 0 && result.SampleRate <= 0)
            return $"vérification impossible : fréquence inconnue ({name})";
        double expected = copied ? from[0].Time : priming > 0 ? start - priming / (double)result.SampleRate : start;
        if (Math.Abs(to[0].Time - expected) > Tolerance)
            return Reason($"{name} commençant à {to[0].Time:0.000}s au lieu de {expected:0.000}s : décalé par rapport à la vidéo");

        double sourceStep = Step(from, source.SampleRate), resultStep = Step(to, result.SampleRate);
        if (sourceStep <= 0 || resultStep <= 0)
            return $"vérification impossible : durée des paquets inconnue ({name})";
        var starts = from.Select(p => p.Time).ToList();
        var results = to.Select(p => p.Time).ToList();
        double margin = Math.Max(sourceStep, resultStep);
        foreach (double w in windows)
        {
            var inSource = starts.Where(t => t >= w && t <= w + WindowSeconds).ToList();
            var inResult = results.Where(t => t >= w && t <= w + WindowSeconds).ToList();
            if (inSource.Count == 0 && inResult.Count == 0)
                continue;
            if (copied)
            {
                foreach (double t in inSource)
                    if (Nearest(results, t) is var d && (double.IsNaN(d) || Math.Abs(d) > Tolerance))
                        return Reason($"{name} décalé à {t:0.000}s : paquet le plus proche à {d * 1000:+0;-0} ms");
                foreach (double t in inResult)
                    if (Nearest(starts, t) is var d && (double.IsNaN(d) || Math.Abs(d) > Tolerance))
                        return Reason($"{name} décalé à {t:0.000}s : paquet absent de la source");
                continue;
            }
            // Sound in the same places: a place that has sound on one side only is a hole, or a shift. Each list is walked for gaps;
            // the edges of a hole may differ by a packet on each side.
            if (FirstUnmatched(inSource, results, 2 * margin + Tolerance) is { } missing)
                return Reason($"{name} décalé à {missing:0.000}s : son dans la source, pas dans le résultat");
            if (FirstUnmatched(inResult, starts, 2 * margin + Tolerance) is { } extra)
                return Reason($"{name} décalé à {extra:0.000}s : son dans le résultat, pas dans la source");
            // On the grid its first packet set. A hole in the source may move the grid (then the source's own grid moved too, and
            // nothing can be said of a fraction of a packet here); otherwise, a move means a shift.
            if (inSource.All(t => Math.Abs(Remainder(t - from[0].Time, sourceStep)) <= Tolerance))
                foreach (double t in inResult)
                    if (Remainder(t - to[0].Time, resultStep) is var off && Math.Abs(off) > Tolerance)
                        return Reason($"{name} décalé à {t:0.000}s : {off * 1000:+0.0;-0.0} ms par rapport à ses premiers paquets");
        }
        // An encoder gives back every sample and its last packet says so (0 to 0.2 ms measured): this is what shows a shift by whole packets
        // lasting to the end, which leaves the grid alone. MP3 pads its last packet (17 ms measured), so only a stream ending early is seen
        // there. A copied stream's last packet reads its container's idea of its length (11 ms apart measured, Matroska to MP4), but its
        // packets were compared one by one. AAC may read later too: with Emby's ffmpeg 5.1 its last packet said 10 ms more through the file prepared
        // ahead (same packets, same times, two real files), and on sound starting at 0.5 s it ended 16 ms late in both assemblies. A whole packet late
        // to the end is therefore not seen on those encoders; the audio is one encode, with no known cause for it.
        double sourceEnd = from[^1].Time + (from[^1].Length > 0 ? from[^1].Length : sourceStep), resultEnd = to[^1].Time + (to[^1].Length > 0 ? to[^1].Length : resultStep);
        double late = copied || PadsLastPacket(encoder) ? margin + Tolerance : 2 * Tolerance;
        double early = copied ? margin + Tolerance : 2 * Tolerance;
        if (resultEnd - sourceEnd > late || sourceEnd - resultEnd > early)
            return Reason($"{name} finissant à {resultEnd:0.000}s au lieu de {sourceEnd:0.000}s");
        return null;
    }

    // Encoders whose last packet is not cut to the samples it holds: all but those measured exact.
    private static bool PadsLastPacket(string? encoder) => encoder is not ("ac3" or "eac3" or "libopus" or "opus" or "flac");

    // First time of a list with nothing within the distance in another (sorted) one.
    private static double? FirstUnmatched(IEnumerable<double> times, List<double> sorted, double distance)
    {
        foreach (double t in times)
            if (Nearest(sorted, t) is var d && (double.IsNaN(d) || Math.Abs(d) > distance))
                return t;
        return null;
    }

    /// <summary>
    /// A packet's length, in seconds: a whole number of samples when the frequency is known (Matroska rounds its times to the millisecond and
    /// ffprobe its durations to the microsecond, which would add up over 100000 packets). 0 when the packets read do not say.
    /// </summary>
    private static double Step(List<(double Time, double Length)> packets, int sampleRate)
    {
        var gaps = packets.Zip(packets.Skip(1), (a, b) => b.Time - a.Time).Where(g => g > 0).OrderBy(g => g).ToList();
        if (gaps.Count == 0)
            return packets.Count > 0 ? packets[0].Length : 0;
        double median = gaps[gaps.Count / 2];
        var regular = gaps.Where(g => Math.Abs(g - median) <= 1.5 * Tolerance).ToList();
        double mean = regular.Count > 0 ? regular.Average() : median;
        return sampleRate > 0 ? Math.Round(mean * sampleRate) / sampleRate : mean;
    }

    private static string Reason(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
