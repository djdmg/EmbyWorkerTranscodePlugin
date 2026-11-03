using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RemoteTranscode.Coordinator;

/// <summary>A stream as ffprobe describes it: allow-listed fields only, each checked against <see cref="DiagnosticsAnalysis.Token(string?)"/>.</summary>
public sealed record StreamFingerprint
{
    public int Index { get; init; }
    public string? Type { get; init; }
    public string? Codec { get; init; }
    public string? Profile { get; init; }
    public int? Level { get; init; }
    public int? HasBFrames { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? PixelFormat { get; init; }
    public int? BitDepth { get; init; }
    public string? FieldOrder { get; init; }
    public string? TimeBase { get; init; }
    public double? Start { get; init; }
    public double? Duration { get; init; }
    public string? RFrameRate { get; init; }
    public string? AvgFrameRate { get; init; }
    public long? Frames { get; init; }
    public string? ColorTransfer { get; init; }
    public string? ColorPrimaries { get; init; }
    public string? ColorSpace { get; init; }
    public string? ColorRange { get; init; }
    public int? SampleRate { get; init; }
    public int? Channels { get; init; }
    public string? ChannelLayout { get; init; }
    public bool Cover { get; init; }
}

public sealed record SourceFingerprint(string? Container, double? Start, double? Duration, long? BitRate, int Chapters, IReadOnlyList<StreamFingerprint> Streams)
{
    public StreamFingerprint? Video => Streams.FirstOrDefault(s => s.Type == "video" && !s.Cover);
    public IEnumerable<StreamFingerprint> Audio => Streams.Where(s => s.Type == "audio");
}

/// <summary>A video packet: times, flags, size. No content.</summary>
public sealed record PacketInfo(double? Pts, double? Dts, double? Duration, long? Size, string? Flags)
{
    public bool Key => Flags?.Contains('K') == true;
}

/// <summary>Frame rate and GOP measured on a sample of packets at the start of the source.</summary>
public sealed record PacketStats(int Packets, double? FrameSeconds, double? MinDelta, double? MaxDelta, bool? ConstantRate, int? GopMedian, int? GopMax, int Keyframes, int WithoutPts);

/// <summary>Where the streams end (file timestamps), read in the window before the end of the file; a stream without packet in the window is absent.</summary>
public sealed record StreamEnds(double? Video, IReadOnlyDictionary<int, double> Audio, double From);

/// <summary>The frames around a slice boundary in the source.</summary>
public sealed record BoundarySample(int Slice, double Time, IReadOnlyList<PacketInfo> Frames);

/// <summary>What ffprobe measured in a chunk of the spool.</summary>
public sealed record ChunkMeasure(int Index, int Packets, double? FirstPts, double? LastPts, double? FrameSeconds, bool Missing = false);

/// <summary>A line of the raw table: a chunk as listed and as measured.</summary>
public sealed record ChunkRow(int Slice, int Index, double ListedStart, double ListedEnd, double? ConcatDuration, long Size, ChunkMeasure? Measure);

/// <summary>One slice: what was listed against the slice's boundaries, and the frames measured against those expected.</summary>
public sealed record SliceMeasure
{
    public int Number { get; init; }
    public double Start { get; init; }
    public double End { get; init; }
    public bool Last { get; init; }
    public bool Done { get; init; }
    public string? Machine { get; init; }
    public int Runs { get; init; }
    public int Resumes { get; init; }
    public int ChunkCount { get; init; }
    public int MissingChunks { get; init; }
    public double? ListedEnd { get; init; }
    /// <summary>Listed end minus the slice's end (ms); null on the last slice, open-ended.</summary>
    public double? ListedEndMs { get; init; }
    /// <summary>Sum of the concat durations minus the slice's length (ms); null on the last slice.</summary>
    public double? ConcatMs { get; init; }
    public int? Frames { get; init; }
    public int? Expected { get; init; }
    public double? FirstPts { get; init; }
    public double? LastPts { get; init; }
    /// <summary>First frame of the next slice minus (this slice's last frame + one frame), in ms; null when not comparable.</summary>
    public double? JoinMs { get; init; }
    /// <summary>Joins between this slice's chunks off by more than 1.5 frames (slices of a single run only).</summary>
    public int InnerJumps { get; init; }
    /// <summary>
    /// Whole frames between this slice's first frame and where it should be, against the first slice (which does not
    /// seek); null when its times are not source times. A count can be right with every frame shifted by one.
    /// </summary>
    public int? FirstFrameShift { get; init; }
    public int? FrameDifference => Frames is { } f && Expected is { } e ? f - e : null;
}

public enum VerdictClass
{
    Undetermined,
    ExtraFrames,
    MissingFrames,
    ExtraAndMissingFrames,
    ShiftedFrames,
    FileStartDisagreement,
    GapBetweenSlices,
    ListingLate,
    ListingDrift,
    Audio,
    EncoderStart,
    FilterError,
    SourceRead,
    NetworkInput,
    OutOfMemory,
    DiskFull,
    IdleRuns,
}

/// <summary>The cause as measured. <see cref="Text"/> holds numbers and fixed words only; <see cref="Name"/> enters the signature.</summary>
public sealed record Verdict(VerdictClass Class, string Text)
{
    public string Name => Class switch
    {
        VerdictClass.ExtraFrames => "extra-frames",
        VerdictClass.MissingFrames => "missing-frames",
        VerdictClass.ExtraAndMissingFrames => "extra-and-missing-frames",
        VerdictClass.ShiftedFrames => "shifted-frames",
        VerdictClass.FileStartDisagreement => "file-start-disagreement",
        VerdictClass.GapBetweenSlices => "gap-between-slices",
        VerdictClass.ListingLate => "listing-late",
        VerdictClass.ListingDrift => "listing-drift",
        VerdictClass.Audio => "audio",
        VerdictClass.EncoderStart => "encoder-start",
        VerdictClass.FilterError => "filter-error",
        VerdictClass.SourceRead => "source-read",
        VerdictClass.NetworkInput => "network-input",
        VerdictClass.OutOfMemory => "out-of-memory",
        VerdictClass.DiskFull => "disk-full",
        VerdictClass.IdleRuns => "idle-runs",
        _ => "undetermined",
    };
}

/// <summary>Pure analysis of ffprobe's output and of the coordinator's lists: testable with canned output.</summary>
public static partial class DiagnosticsAnalysis
{
    private static double? Num(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d) ? d : null;

    /// <summary>A field as text, whether ffprobe printed it as a string or a number.</summary>
    private static string? Raw(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null
            : null;

    private static double? Double(JsonElement e, string name) => Num(Raw(e, name));
    private static int? Int(JsonElement e, string name) => int.TryParse(Raw(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;
    private static long? Long(JsonElement e, string name) => long.TryParse(Raw(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;

    /// <summary>
    /// A codec-level word: short, no quote, and a slash only between digits (a time base or a frame rate). Anything else
    /// is dropped: even a field that should never hold a name cannot carry a path or a sentence.
    /// </summary>
    public static string? Token(string? value) =>
        value is null || !SafeWord().IsMatch(value) || BadSlash().IsMatch(value) ? null : value;

    private static string? Token(JsonElement e, string name) => Token(Raw(e, name));

    [GeneratedRegex(@"^[A-Za-z0-9_.,:/+\-() ]{1,48}$")]
    private static partial Regex SafeWord();

    [GeneratedRegex(@"(?<!\d)/|/(?!\d)")]
    private static partial Regex BadSlash();

    /// <summary>
    /// Only the fields read here can reach a report, whatever ffprobe printed: its tags, stream titles, chapter titles and
    /// file name are never looked at.
    /// </summary>
    public static SourceFingerprint ParseFingerprint(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var format = root.TryGetProperty("format", out var f) ? f : default;
        var streams = new List<StreamFingerprint>();
        if (root.TryGetProperty("streams", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var s in list.EnumerateArray())
                streams.Add(new StreamFingerprint
                {
                    Index = Int(s, "index") ?? streams.Count,
                    Type = Token(s, "codec_type"),
                    Codec = Token(s, "codec_name"),
                    Profile = Token(s, "profile"),
                    Level = Int(s, "level"),
                    HasBFrames = Int(s, "has_b_frames"),
                    Width = Int(s, "width") ?? 0,
                    Height = Int(s, "height") ?? 0,
                    PixelFormat = Token(s, "pix_fmt"),
                    BitDepth = Int(s, "bits_per_raw_sample"),
                    FieldOrder = Token(s, "field_order"),
                    TimeBase = Token(s, "time_base"),
                    Start = Double(s, "start_time"),
                    Duration = Double(s, "duration"),
                    RFrameRate = Token(s, "r_frame_rate"),
                    AvgFrameRate = Token(s, "avg_frame_rate"),
                    Frames = Long(s, "nb_frames"),
                    ColorTransfer = Token(s, "color_transfer"),
                    ColorPrimaries = Token(s, "color_primaries"),
                    ColorSpace = Token(s, "color_space"),
                    ColorRange = Token(s, "color_range"),
                    SampleRate = Int(s, "sample_rate"),
                    Channels = Int(s, "channels"),
                    ChannelLayout = Token(s, "channel_layout"),
                    Cover = s.TryGetProperty("disposition", out var d) && Int(d, "attached_pic") == 1,
                });
        int chapters = root.TryGetProperty("chapters", out var c) && c.ValueKind == JsonValueKind.Array ? c.GetArrayLength() : 0;
        return new SourceFingerprint(Token(format, "format_name"), Double(format, "start_time"), Double(format, "duration"), Long(format, "bit_rate"), chapters, streams);
    }

    public static List<PacketInfo> ParsePackets(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var packets = new List<PacketInfo>();
        if (doc.RootElement.TryGetProperty("packets", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var p in list.EnumerateArray())
                packets.Add(new PacketInfo(Double(p, "pts_time"), Double(p, "dts_time"), Double(p, "duration_time"), Long(p, "size"), Flags(Raw(p, "flags"))));
        return packets;
    }

    private static string? Flags(string? flags) => flags != null && FlagWord().IsMatch(flags) ? flags : null;

    [GeneratedRegex(@"^[A-Z_]{1,8}$")]
    private static partial Regex FlagWord();

    private static double Median(List<double> sorted) => sorted[sorted.Count / 2];

    /// <summary>Frame duration, constant rate check and GOP length from packets (taken in presentation order).</summary>
    private const int ReorderEdgePackets = 16;

    public static PacketStats Stats(IReadOnlyList<PacketInfo> packets)
    {
        var timed = packets.Where(p => p.Pts != null).OrderBy(p => p.Pts).ToList();
        var deltas = new List<double>();
        // A sample cut after N packets in decoding order lacks frames at its edges once sorted by time (B-frames): that is no change of rate.
        int edge = timed.Count > 4 * ReorderEdgePackets ? ReorderEdgePackets : 0;
        for (int i = edge + 1; i < timed.Count - edge; i++)
            deltas.Add(timed[i].Pts!.Value - timed[i - 1].Pts!.Value);
        deltas.Sort();
        double? frame = deltas.Count > 0 ? Median(deltas) : null;
        // Containers round to the millisecond (Matroska): 2 ms is not a change of rate.
        bool? constant = frame is { } fr ? deltas.All(d => Math.Abs(d - fr) <= Math.Max(0.002, fr * 0.05)) : null;
        var gops = new List<int>();
        int last = -1, keyframes = 0;
        for (int i = 0; i < timed.Count; i++)
        {
            if (!timed[i].Key)
                continue;
            keyframes++;
            if (last >= 0)
                gops.Add(i - last);
            last = i;
        }
        gops.Sort();
        return new PacketStats(packets.Count, frame, deltas.Count > 0 ? deltas[0] : null, deltas.Count > 0 ? deltas[^1] : null, constant,
            gops.Count > 0 ? gops[gops.Count / 2] : null, gops.Count > 0 ? gops[^1] : null, keyframes, packets.Count - timed.Count);
    }

    public static StreamEnds ParseEnds(string json, SourceFingerprint? fingerprint)
    {
        using var doc = JsonDocument.Parse(json);
        var ends = new Dictionary<int, double>();
        if (doc.RootElement.TryGetProperty("packets", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var p in list.EnumerateArray())
                if (Int(p, "stream_index") is { } index && Double(p, "pts_time") is { } pts)
                    ends[index] = Math.Max(ends.GetValueOrDefault(index, double.MinValue), pts + (Double(p, "duration_time") ?? 0));
        int? video = fingerprint?.Video?.Index;
        var audio = (fingerprint?.Audio ?? []).Where(a => ends.ContainsKey(a.Index)).ToDictionary(a => a.Index, a => ends[a.Index]);
        double from = Math.Max(0, (fingerprint?.Start ?? 0) + (fingerprint?.Duration ?? 0) - FailureDiagnostics.EndWindowSeconds);
        return new StreamEnds(video is { } v && ends.TryGetValue(v, out double e) ? e : null, audio, from);
    }

    public static ChunkMeasure MeasureChunk(int index, string json)
    {
        var packets = ParsePackets(json);
        var stats = Stats(packets);
        var pts = packets.Where(p => p.Pts != null).Select(p => p.Pts!.Value).ToList();
        return new ChunkMeasure(index, packets.Count, pts.Count > 0 ? pts.Min() : null, pts.Count > 0 ? pts.Max() : null, stats.FrameSeconds);
    }

    /// <summary>Frames of the source in each slice, from a list of presentation times ("0.042000\n..." or with a trailing comma).</summary>
    public static IReadOnlyDictionary<int, int> CountSourceFrames(string csv, double fileStart, IReadOnlyList<DiagnosticSlice> slices)
    {
        var counts = slices.ToDictionary(s => s.Number, _ => 0);
        var ordered = slices.OrderBy(s => s.Start).ToList();
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Num(line.Split(',')[0]) is not { } pts)
                continue;
            double t = pts - fileStart;
            var slice = ordered.LastOrDefault(s => t >= s.Start - 1e-4);
            if (slice != null && (slice.Last || t < slice.End - 1e-4))
                counts[slice.Number]++;
        }
        return counts;
    }

    public static IReadOnlyList<ChunkRow> ChunkRows(IReadOnlyList<DiagnosticSlice> slices, IReadOnlyDictionary<int, ChunkMeasure> measures) =>
        slices.SelectMany(s => s.Chunks.Select(c => new ChunkRow(s.Number, c.Index, c.ListedStart, c.ListedEnd, c.ConcatDuration, c.Size, measures.GetValueOrDefault(c.Index)))).ToList();

    /// <summary>The frame duration of the encoded chunks: the median of what was measured, else null.</summary>
    private static double? OutputFrame(IEnumerable<ChunkMeasure> chunks)
    {
        var frames = chunks.Where(c => c.FrameSeconds is > 0).Select(c => c.FrameSeconds!.Value).OrderBy(v => v).ToList();
        return frames.Count > 0 ? Median(frames) : null;
    }

    /// <summary>The source's frame duration: from its declared rate, else measured on the packets.</summary>
    public static double? SourceFrame(SourceFingerprint? fingerprint, PacketStats? stats)
    {
        if (fingerprint?.Video is { } v)
            foreach (string? rate in new[] { v.AvgFrameRate, v.RFrameRate })
                if (rate?.Split('/') is [var n, var d] && Num(n) is > 0 and var num && Num(d) is > 0 and var den)
                    return den / num;
        return stats?.FrameSeconds;
    }

    /// <summary>
    /// Per-slice summary of what was listed and measured. Chunks without a measure give no frame count for their slice. Under Emby's -r
    /// (<paramref name="outputFrame"/>) a run holds one frame per tick of its grid, whatever the source's frames: a variable source's frames are
    /// not what to expect (a real slice of 3019 frames, 3014 in the source, was 2 ticks too long and ended 80 ms late, not 5 frames).
    /// </summary>
    public static IReadOnlyList<SliceMeasure> Slices(IReadOnlyList<DiagnosticSlice> slices, IReadOnlyDictionary<int, ChunkMeasure> chunks,
        SourceFingerprint? fingerprint, PacketStats? stats, StreamEnds? ends, IReadOnlyDictionary<int, int>? sourceFrames, double? outputFrame = null)
    {
        double? output = outputFrame ?? OutputFrame(chunks.Values) ?? SourceFrame(fingerprint, stats);
        double start = fingerprint?.Start ?? 0;
        bool variable = stats?.ConstantRate == false && outputFrame is null;
        var result = new List<SliceMeasure>(slices.Count);
        foreach (var s in slices.OrderBy(x => x.Number))
        {
            var measures = s.Chunks.Select(c => chunks.GetValueOrDefault(c.Index)).ToList();
            bool allMeasured = s.Chunks.Count > 0 && measures.All(m => m is { Missing: false });
            var present = measures.Where(m => m is { Missing: false, FirstPts: not null }).Select(m => m!).ToList();
            double? listedEnd = s.Chunks.Count > 0 ? s.Chunks.Max(c => c.ListedEnd) : null;
            int? expected = null;
            if (s.Done)
            {
                if (variable)
                    expected = sourceFrames?.GetValueOrDefault(s.Number);
                else if (output is > 0 and var f)
                {
                    double? end = !s.Last ? s.End : ends?.Video is { } v ? v - start : null;
                    if (end is { } e && e > s.Start)
                        expected = (int)Math.Round((e - s.Start) / f, MidpointRounding.AwayFromZero);
                }
            }
            result.Add(new SliceMeasure
            {
                Number = s.Number,
                Start = s.Start,
                End = s.End,
                Last = s.Last,
                Done = s.Done,
                Machine = s.Machine,
                Runs = s.Runs,
                Resumes = s.Resumes,
                ChunkCount = s.Chunks.Count,
                MissingChunks = measures.Count(m => m is { Missing: true }),
                ListedEnd = listedEnd,
                ListedEndMs = !s.Last && listedEnd is { } le ? (le - s.End) * 1000 : null,
                ConcatMs = !s.Last && s.Chunks.Count > 0 && s.Chunks.All(c => c.ConcatDuration != null) ? (s.Chunks.Sum(c => c.ConcatDuration!.Value) - (s.End - s.Start)) * 1000 : null,
                Frames = allMeasured ? present.Sum(m => m.Packets) : null,
                Expected = expected,
                FirstPts = present.Count > 0 ? present.Min(m => m.FirstPts) : null,
                LastPts = present.Count > 0 ? present.Max(m => m.LastPts) : null,
                InnerJumps = s.Runs <= 1 && allMeasured && output is > 0 ? InnerJumps(present, output.Value) : 0,
            });
        }
        // Joins: only between slices whose frames carry source times (a run counting from its own start tells nothing).
        for (int i = 0; i + 1 < result.Count; i++)
        {
            var (a, b) = (result[i], result[i + 1]);
            if (output is > 0 && a.LastPts is { } last && b.FirstPts is { } first && OnSourceTimes(a) && OnSourceTimes(b))
                result[i] = a with { JoinMs = (first - (last + output.Value)) * 1000 };
        }
        // The first slice does not seek: its first frame is the source's whatever the machines think of the file's start.
        // Its first chunk can be moved by the muxer for B-frames (2 frames seen), so the clock is read at its end, where no seek decides anything.
        if (output is > 0 && result.Count > 0 && OnSourceTimes(result[0]))
        {
            double baseline = !result[0].Last && result[0].FrameDifference is null or 0 && result[0].LastPts is { } firstEnd
                ? firstEnd + output.Value - result[0].End
                : result[0].FirstPts!.Value - result[0].Start;
            for (int i = 1; i < result.Count; i++)
                if (OnSourceTimes(result[i]))
                    result[i] = result[i] with { FirstFrameShift = (int)Math.Round((result[i].FirstPts!.Value - result[i].Start - baseline) / output.Value, MidpointRounding.AwayFromZero) };
        }
        return result;
    }

    // The muxer's delay (1.4 s for MPEG-TS) and a start time of a few seconds stay well under this.
    private static bool OnSourceTimes(SliceMeasure s) => s.FirstPts is { } f && Math.Abs(f - s.Start) < 5;

    private static int InnerJumps(List<ChunkMeasure> chunks, double frame)
    {
        int jumps = 0;
        for (int i = 0; i + 1 < chunks.Count; i++)
            if (Math.Abs(chunks[i + 1].FirstPts!.Value - (chunks[i].LastPts!.Value + frame)) > 1.5 * frame)
                jumps++;
        return jumps;
    }

    /// <summary>Boundaries worth showing: those of abnormal slices first, then spread over the conversion. Times from the file's start.</summary>
    public static IReadOnlyList<(int Slice, double Time)> PickBoundaries(IReadOnlyList<SliceMeasure> slices, int count)
    {
        var inner = slices.Where(s => s.Number > 1).ToList();
        var picked = inner.Where(s => s.FrameDifference is not (null or 0) || s.FirstFrameShift is not (null or 0) || s.ConcatMs is { } c && Math.Abs(c) >= 1).Take(count / 2).ToList();
        int left = count - picked.Count;
        for (int i = 0; i < left && inner.Count > 0; i++)
        {
            var s = inner[(int)((i + 0.5) * inner.Count / left)];
            if (!picked.Contains(s))
                picked.Add(s);
        }
        return picked.OrderBy(s => s.Number).Select(s => (s.Number, s.Start)).ToList();
    }

    /// <summary>For each boundary, the three frames before it and the three from it on (presentation order, times from the file).</summary>
    public static List<BoundarySample> Around(IReadOnlyList<PacketInfo> packets, IReadOnlyList<(int Slice, double Time)> boundaries, double fileStart)
    {
        var timed = packets.Where(p => p.Pts != null).GroupBy(p => p.Pts).Select(g => g.First()).OrderBy(p => p.Pts).ToList();
        var result = new List<BoundarySample>();
        foreach (var (slice, time) in boundaries)
        {
            double at = time + fileStart;
            int k = timed.FindIndex(p => p.Pts >= at - 1e-4);
            if (k < 0)
                continue;
            int from = Math.Max(0, k - 3);
            result.Add(new BoundarySample(slice, at, timed.GetRange(from, Math.Min(6, timed.Count - from))));
        }
        return result;
    }

    /// <summary>"6.1.1" from ffmpeg's version output; null when it does not read as a version. Nothing else of the output is kept.</summary>
    public static string? FfmpegVersionToken(string? output)
    {
        if (output is null)
            return null;
        var m = VersionLine().Match(output);
        return m.Success ? FailureReportFormatter.Sanitize(m.Groups[1].Value, []) : null;
    }

    [GeneratedRegex(@"ffmpeg version ([A-Za-z0-9._+~\-]{1,64})")]
    private static partial Regex VersionLine();

    /// <summary>
    /// The length difference the check reported, in seconds (positive: the result is longer), from the shim's error
    /// ("vidéo de 3121.992s au lieu de 3121.117s (+21 images)") or its check line.
    /// </summary>
    public static double? ReportedExcess(string? error, string? check)
    {
        if (error != null && LengthError().Match(error) is { Success: true } m && Num(m.Groups[1].Value) is { } a && Num(m.Groups[2].Value) is { } b)
            return a - b;
        if (check != null && CheckLine().Match(check) is { Success: true } c
            && Num(c.Groups[1].Value) is { } s0 && Num(c.Groups[2].Value) is { } s1 && Num(c.Groups[3].Value) is { } r0 && Num(c.Groups[4].Value) is { } r1)
            return r1 - r0 - (s1 - s0);
        return null;
    }

    [GeneratedRegex(@"de ([\d.]+)s au lieu de ([\d.]+)s")]
    private static partial Regex LengthError();

    [GeneratedRegex(@"source ([\d.]+)-([\d.]+)s, \S+ ([\d.]+)-([\d.]+)s")]
    private static partial Regex CheckLine();

    private static string Ms(double v) => v.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);

    /// <summary>"3, 5-7, 9": slice numbers, consecutive ones as ranges, at most 40 ranges.</summary>
    private static string Numbers(IEnumerable<int> numbers)
    {
        var list = numbers.OrderBy(n => n).ToList();
        var parts = new List<string>();
        for (int i = 0; i < list.Count && parts.Count < 40;)
        {
            int j = i;
            while (j + 1 < list.Count && list[j + 1] == list[j] + 1)
                j++;
            parts.Add(i == j ? $"{list[i]}" : $"{list[i]}-{list[j]}");
            i = j + 1;
        }
        return string.Join(", ", parts) + (parts.Count == 40 ? ", ..." : "");
    }

    /// <summary>
    /// The verdict: what was measured, and "undetermined" rather than a guess. A rejected assembly is judged on its
    /// frames, then its joins, then its list; other failures on ffmpeg's error lines (the error text is only matched
    /// against fixed patterns, it never goes into the verdict).
    /// </summary>
    public static Verdict Decide(FailureKind kind, string error, string? check, IReadOnlyList<SliceMeasure> slices, SourceFingerprint? fingerprint,
        PacketStats? stats, StreamEnds? ends, bool chunksMeasured, bool complete, IReadOnlyDictionary<string, double>? fileStarts = null)
    {
        if (kind != FailureKind.AssemblyRejected)
            return ClassifyError(error, slices);
        var measured = DecideAssembly(error, check, slices, fingerprint, stats, ends, chunksMeasured, complete);
        if (StartDisagreement(fingerprint, fileStarts) is not { } views)
            return measured;
        // A disagreement alone does not prove the cause (the runs now seek between two frames and bear it): counted
        // frames and the audio keep their own class, and everything about timing goes under the disagreement.
        return measured.Class is VerdictClass.ShiftedFrames or VerdictClass.GapBetweenSlices or VerdictClass.ListingLate or VerdictClass.ListingDrift or VerdictClass.Undetermined
            ? new(VerdictClass.FileStartDisagreement, $"the server and a worker disagree on the file's start: {views}. Measured: `{measured.Name}`, {measured.Text}")
            : measured with { Text = $"{measured.Text} Also, the machines disagree on the file's start: {views}." };
    }

    /// <summary>"server ffprobe 0 s, others 0.005 s" when the machines read different file starts (half a millisecond or more), else null.</summary>
    public static string? StartDisagreement(SourceFingerprint? fingerprint, IReadOnlyDictionary<string, double>? fileStarts)
    {
        if (fileStarts is not { Count: > 0 })
            return null;
        var views = fileStarts.Values.Concat(fingerprint?.Start is { } s ? [s] : Array.Empty<double>()).ToList();
        if (views.Max() - views.Min() < 0.0005)
            return null;
        static string T(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        string others = string.Join(", ", fileStarts.Values.Distinct().OrderBy(v => v).Select(v => T(v) + " s"));
        return $"server's ffprobe {(fingerprint?.Start is { } start ? T(start) + " s" : "unknown")}, machines' ffmpeg {others}";
    }

    /// <summary>The result with what the machines' own ffmpeg read as the file's start, its verdict and recipe taking it into account.</summary>
    public static DiagnosticsResult WithFileStarts(DiagnosticsResult result, IReadOnlyDictionary<string, double> fileStarts, DiagnosticsRequest request)
    {
        var starts = new Dictionary<string, double>(fileStarts);
        return result with
        {
            FileStarts = starts,
            Verdict = Decide(request.Kind, request.Error, request.Check, result.Slices, result.Fingerprint, result.Stats, result.Ends, request.SpoolDir != null,
                result.Status == DiagnosticsStatus.Completed, starts),
            Recipe = result.Fingerprint is null ? result.Recipe
                : RecipeBuilder.Build(result.Fingerprint, result.Stats, result.Ends, starts.Count > 0 ? starts.Values.Max() : null),
        };
    }

    private static Verdict DecideAssembly(string error, string? check, IReadOnlyList<SliceMeasure> slices, SourceFingerprint? fingerprint,
        PacketStats? stats, StreamEnds? ends, bool chunksMeasured, bool complete)
    {
        if (error.StartsWith(SliceShiftCheck.ErrorPrefix, StringComparison.Ordinal))
            return new(VerdictClass.ShiftedFrames, "the check before assembly read a whole frame between where worker slices start and where the source has those frames (see Error): their frame counts and the total length can be right all the same. Nothing was assembled.");
        if (error.Contains("audio", StringComparison.OrdinalIgnoreCase))
            return new(VerdictClass.Audio, "the check rejected the audio tracks of the assembled file (see Error); the video was not at fault in this check.");
        if (!chunksMeasured)
            return new(VerdictClass.Undetermined, "the chunks could not be measured.");
        var done = slices.Where(s => s.Done).ToList();
        var measured = done.Where(s => s.FrameDifference != null).ToList();
        string coverage = $"{measured.Count} of {done.Count} slices measured";
        string part = complete ? "" : ", analysis cut short";
        if (measured.Count == 0)
            return new(VerdictClass.Undetermined, $"no slice could be compared with its expected frames ({coverage}{part}).");

        var extra = measured.Where(s => s.FrameDifference > 0).ToList();
        var missing = measured.Where(s => s.FrameDifference < 0).ToList();
        static string Signed(int v) => v.ToString("+0;-0;0", CultureInfo.InvariantCulture);
        string Diff(SliceMeasure s) => $"{s.Number} ({Signed(s.FrameDifference!.Value)})";

        // Most slices off the same way, with hardly a frame gained or lost: a slice that skips its first frame and takes the next one's.
        // The count alone would call that "missing frames", and send the search to the wrong place.
        var skewed = done.Where(s => s.FirstFrameShift is not (null or 0)).ToList();
        int seeking = done.Count(s => s.FirstFrameShift != null);
        bool sameWay = skewed.Count > 0 && (skewed.All(s => s.FirstFrameShift > 0) || skewed.All(s => s.FirstFrameShift < 0));
        int net = measured.Sum(s => s.FrameDifference!.Value);
        if (sameWay && skewed.Count * 2 > seeking && Math.Abs(net) <= 1 && measured.All(s => Math.Abs(s.FrameDifference!.Value) <= 1))
        {
            string counts = extra.Count + missing.Count == 0 ? "frame counts match" : $"frame counts differ only by {Signed(net)} in all";
            return new(VerdictClass.ShiftedFrames,
                $"{counts} but the first frame of {skewed.Count} of {seeking} slices compared is off: {string.Join(", ", skewed.Take(40).Select(s => $"{s.Number} ({Signed(s.FirstFrameShift!.Value)})"))} frame(s), against the first slice's end ({coverage}{part}).");
        }
        if (extra.Count > 0 && missing.Count > 0)
            return new(VerdictClass.ExtraAndMissingFrames, $"frames counted in the chunks differ from the source: extra in slices {string.Join(", ", extra.Take(20).Select(Diff))}; missing in slices {string.Join(", ", missing.Take(20).Select(Diff))} ({coverage}{part}).");
        if (extra.Count > 0)
            return new(VerdictClass.ExtraFrames, $"real extra frames, counted in the chunks: slices {string.Join(", ", extra.Take(40).Select(Diff))}, {Signed(extra.Sum(s => s.FrameDifference!.Value))} in all ({coverage}{part}).");
        if (missing.Count > 0)
            return new(VerdictClass.MissingFrames, $"frames missing, counted in the chunks: slices {string.Join(", ", missing.Take(40).Select(Diff))}, {Signed(missing.Sum(s => s.FrameDifference!.Value))} in all ({coverage}{part}).");

        // Counts can be right with every frame off by one: first frame of the slice skipped, first frame of the next kept.
        var shifted = done.Where(s => s.FirstFrameShift is not (null or 0)).ToList();
        int compared = done.Count(s => s.FirstFrameShift != null);
        if (shifted.Count > 0)
            return new(VerdictClass.ShiftedFrames,
                $"frame counts match but the first frame of {shifted.Count} of {compared} slices compared is off: {string.Join(", ", shifted.Take(40).Select(s => $"{s.Number} ({Signed(s.FirstFrameShift!.Value)})"))} frame(s), against the first slice ({coverage}{part}).");

        double frame = SourceFrame(fingerprint, stats) ?? 0;
        var joins = done.Where(s => s.JoinMs is { } j && frame > 0 && Math.Abs(j) > 0.5 * frame * 1000).ToList();
        var jumps = done.Where(s => s.InnerJumps > 0).ToList();
        if (joins.Count > 0 || jumps.Count > 0)
        {
            string text = joins.Count > 0 ? $"timestamps jump between slices {string.Join(", ", joins.Take(20).Select(s => $"{s.Number}-{s.Number + 1} ({Ms(s.JoinMs!.Value)} ms)"))}" : "";
            if (jumps.Count > 0)
                text += (text.Length > 0 ? "; " : "timestamps jump ") + $"inside slices {Numbers(jumps.Select(s => s.Number))}";
            return new(VerdictClass.GapBetweenSlices, $"frame counts match but {text} ({coverage}{part}).");
        }

        var listing = done.Where(s => s.ConcatMs != null).ToList();
        var off = listing.Where(s => Math.Abs(s.ConcatMs!.Value) >= 1).ToList();
        double total = listing.Sum(s => s.ConcatMs!.Value);
        double? reported = ReportedExcess(error, check) * 1000;
        string explains = reported is { } rep && rep != 0 ? $"; the check reported {Ms(rep)} ms" : "";
        if (off.Count > 0 && frame > 0 && Math.Abs(total) >= frame * 500)
        {
            double min = off.Min(s => s.ConcatMs!.Value), max = off.Max(s => s.ConcatMs!.Value);
            if (max - min <= 2)
            {
                double x = Median(off.Select(s => s.ConcatMs!.Value).OrderBy(v => v).ToList());
                string origin = "";
                if (fingerprint?.Video?.Start is { } st && st > 0)
                {
                    double late = (frame - st % frame) * 1000;
                    if (Math.Abs(late - Math.Abs(x)) <= 1.5)
                        origin = $" The same value as frame duration - (video start mod frame duration) = {late.ToString("0.0", CultureInfo.InvariantCulture)} ms, which is a measure, not a cause.";
                }
                return new(VerdictClass.ListingLate,
                    $"frame counts match ({coverage}) and first frames are in place on {compared} slices compared, but listed {(x > 0 ? "late" : "early")} by a uniform {Ms(x)} ms on {off.Count} of {listing.Count} slices ({Numbers(off.Select(s => s.Number))}), {Ms(total)} ms in all in the concat list{explains}{part}.{origin}");
            }
            return new(VerdictClass.ListingDrift,
                $"frame counts match ({coverage}) and first frames are in place on {compared} slices compared, but the concat list differs from the slices' lengths on {off.Count} of {listing.Count} slices, from {Ms(min)} to {Ms(max)} ms, {Ms(total)} ms in all{explains}{part}.");
        }
        if (measured.Count == done.Count && error.Contains("durée", StringComparison.OrdinalIgnoreCase))
        {
            string shortfall = ends?.Video is { } ve && ends.Audio.Count > 0 ? $" In the source, the longest audio track ends {Ms((ends.Audio.Values.Max() - ve) * 1000)} ms after the video." : "";
            return new(VerdictClass.Audio, $"frame counts and list match ({coverage}): the file's duration, not its video, failed the check.{shortfall}");
        }
        return new(VerdictClass.Undetermined, $"frame counts as expected, first frames in place on {compared} slices compared and list within {Ms(total)} ms ({coverage}{part}): nothing measured here explains the rejection{explains}.");
    }

    /// <summary>A failure judged on ffmpeg's error lines: fixed patterns, the verdict names the pattern, never the text.</summary>
    public static Verdict ClassifyError(string error, IReadOnlyList<SliceMeasure> slices)
    {
        foreach (var (pattern, cls, label) in ErrorPatterns)
            if (pattern.IsMatch(error))
                return new(cls, $"ffmpeg's error matches \"{label}\".");
        int done = slices.Count(s => s.Done);
        return new(VerdictClass.Undetermined, slices.Count > 0
            ? $"ffmpeg's error matches no known pattern ({done} of {slices.Count} slices were done)."
            : "ffmpeg's error matches no known pattern.");
    }

    private static Regex Pattern(string text) => new(text, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly (Regex Pattern, VerdictClass Class, string Label)[] ErrorPatterns =
    [
        (Pattern("No space left|disk full"), VerdictClass.DiskFull, "no space left"),
        (Pattern("Cannot allocate memory|out of memory"), VerdictClass.OutOfMemory, "out of memory"),
        (Pattern("sans rien encoder"), VerdictClass.IdleRuns, "runs ended without encoding"),
        (Pattern("No such filter|Error (?:re)?initializing (?:complex )?filter|Failed to (?:configure|inject)|filtergraph"), VerdictClass.FilterError, "filter error"),
        (Pattern("opening encoder|open encoder|Unknown encoder|nvenc|OpenEncodeSession|No capable devices|Cannot load|_qsv|\\bMFX\\b|vaapi"), VerdictClass.EncoderStart, "encoder could not start"),
        (Pattern("Connection|timed out|HTTP error|Server returned|\\bTLS\\b|certificate|Input/output error|I/O error"), VerdictClass.NetworkInput, "source read over the network"),
        (Pattern("Read error|Invalid data|moov atom|corrupt|Error while decoding|Invalid NAL|End of file"), VerdictClass.SourceRead, "source could not be read"),
    ];
}

/// <summary>
/// Removes from a conversion command anything that is not a technical setting. An allow-list: options outside it keep
/// their name but lose their value, filters outside it keep their name but lose their arguments, and a value must
/// also have the plain shape of a setting. So -metadata title=..., a subtitle or font path and the output file go.
/// </summary>
public static partial class CommandSanitizer
{
    public const string Removed = "<removed>";

    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "-map", "-map_metadata", "-map_chapters", "-c", "-codec", "-vcodec", "-acodec", "-scodec", "-preset", "-tune", "-crf", "-qp", "-cq", "-rc",
        "-b", "-maxrate", "-minrate", "-bufsize", "-g", "-keyint_min", "-sc_threshold", "-force_key_frames", "-profile", "-level", "-pix_fmt",
        "-r", "-fps_mode", "-vsync", "-s", "-aspect", "-bf", "-refs", "-threads", "-ss", "-t", "-to", "-itsoffset", "-output_ts_offset",
        "-avoid_negative_ts", "-f", "-segment_time", "-segment_format", "-segment_list_type", "-segment_start_number", "-segment_time_delta",
        "-segment_format_options", "-segment_list", "-reset_timestamps", "-break_non_keyframes", "-max_muxing_queue_size", "-muxdelay", "-muxpreload",
        "-copytb", "-start_number", "-frames", "-ac", "-ar", "-af", "-sample_fmt", "-channel_layout", "-color_primaries", "-color_trc", "-colorspace",
        "-color_range", "-movflags", "-x264opts", "-x264-params", "-x265-params", "-hwaccel", "-hwaccel_output_format", "-probesize", "-analyzeduration",
        "-fflags", "-flags", "-strict", "-disposition", "-autoscale", "-enc_time_base", "-vf", "-filter_complex", "-filter", "-lavfi", "-tag",
    };

    private static readonly HashSet<string> FilterOptions = new(StringComparer.Ordinal) { "-vf", "-af", "-filter_complex", "-filter", "-lavfi" };

    private static readonly HashSet<string> SafeFilters = new(StringComparer.Ordinal)
    {
        "scale", "scale_cuda", "scale_vaapi", "scale_qsv", "vpp_qsv", "zscale", "format", "setparams", "setsar", "setdar", "fps", "tonemap", "tonemapx",
        "tonemap_opencl", "tonemap_vaapi", "supertonemap", "libplacebo", "yadif", "yadif_cuda", "bwdif", "bwdif_cuda", "deinterlace_vaapi", "hwupload",
        "hwupload_cuda", "hwdownload", "hwmap", "crop", "pad", "transpose", "setpts", "trim", "null", "anull", "aresample", "volume", "pan", "loudnorm",
        "dynaudnorm", "aformat", "asetpts", "atrim", "split",
    };

    /// <summary>The arguments as one line, quoted where a shell needs it.</summary>
    public static string Line(IReadOnlyList<string> args) =>
        string.Join(" ", Sanitize(args).Select(a => a.Any(c => c is ' ' or '(' or ')' or ';' or '|' or '[') ? $"\"{a}\"" : a));

    public static IReadOnlyList<string> Sanitize(IReadOnlyList<string> args)
    {
        var result = new List<string>(args.Count);
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (Placeholder().IsMatch(a))
                result.Add(a);
            else if (!IsOption(a))
                result.Add(i == args.Count - 1 ? "<output>" : Removed);
            else
            {
                result.Add(a);
                // A flag: the next word is an option, or the output.
                if (i + 1 >= args.Count || IsOption(args[i + 1]) || (i + 1 == args.Count - 1 && !ValueOptions.Contains(a.Split(':')[0])))
                    continue;
                i++;
                result.Add(SanitizeValue(a, args[i]));
            }
        }
        return result;
    }

    private static string SanitizeValue(string option, string value)
    {
        if (Placeholder().IsMatch(value))
            return value;
        string name = option.Split(':')[0];
        if (!ValueOptions.Contains(name))
            return Removed;
        if (FilterOptions.Contains(name))
            return Graph(value);
        return value.Length <= 300 && Value().IsMatch(value) && !LetterSlash().IsMatch(value) ? value : Removed;
    }

    /// <summary>A filter graph with only the arguments of known filters, and none that takes a file or a text.</summary>
    public static string Graph(string graph)
    {
        var output = new System.Text.StringBuilder();
        var part = new System.Text.StringBuilder();
        int depth = 0;
        bool quoted = false;
        for (int i = 0; i < graph.Length; i++)
        {
            char c = graph[i];
            if (c == '\\' && i + 1 < graph.Length)
            {
                part.Append(c).Append(graph[++i]);
                continue;
            }
            if (c == '\'')
                quoted = !quoted;
            else if (!quoted && c is '(' or '[')
                depth++;
            else if (!quoted && c is ')' or ']')
                depth--;
            if (!quoted && depth == 0 && c is ',' or ';')
            {
                output.Append(Filter(part.ToString())).Append(c);
                part.Clear();
            }
            else
                part.Append(c);
        }
        return output.Append(Filter(part.ToString())).ToString();
    }

    private static string Filter(string filter)
    {
        var m = FilterShape().Match(filter.Trim());
        if (!m.Success)
            return "<filter>";
        string name = m.Groups["name"].Value, labelsIn = m.Groups["in"].Value, labelsOut = m.Groups["out"].Value;
        string args = m.Groups["args"].Value;
        if (args.Length == 0)
            return labelsIn + name + labelsOut;
        bool safe = SafeFilters.Contains(name) && args.Length <= 300 && FilterArgs().IsMatch(args) && !FileKey().IsMatch(args) && !SlashWord().IsMatch(args);
        return $"{labelsIn}{name}={(safe ? args : Removed)}{labelsOut}";
    }

    private static bool IsOption(string a) => a.Length > 1 && a[0] == '-' && !char.IsDigit(a[1]) && a[1] != '.' && OptionName().IsMatch(a);

    [GeneratedRegex(@"^\{[a-z0-9:_]{1,24}\}$")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^-[A-Za-z_][A-Za-z0-9_\-]{0,40}(?::[A-Za-z0-9_:]{1,16})?$")]
    private static partial Regex OptionName();

    [GeneratedRegex(@"^[A-Za-z0-9_.:+\-=,()*/|]{1,300}$")]
    private static partial Regex Value();

    // A slash touching a letter: a path, not a fraction.
    [GeneratedRegex(@"[A-Za-z_.]/|/[A-Za-z_.]")]
    private static partial Regex LetterSlash();

    [GeneratedRegex(@"^(?<in>(?:\[[A-Za-z0-9_:.]{1,24}\])*)(?<name>[A-Za-z0-9_]{1,32})(?:=(?<args>.*?))?(?<out>(?:\[[A-Za-z0-9_:.]{1,24}\])*)$", RegexOptions.Singleline)]
    private static partial Regex FilterShape();

    [GeneratedRegex(@"^[A-Za-z0-9_.:+\-=,()*/<>' ]+$")]
    private static partial Regex FilterArgs();

    // Filter options that take a file or a text.
    [GeneratedRegex(@"(?:^|:)[a-z_]*(?:file|path|font|text|shader|lut|sub|title)[a-z_]*=", RegexOptions.IgnoreCase)]
    private static partial Regex FileKey();

    // A slash before a word or at the start of a value is a path; "iw/2" and "trunc(ih/2)*2" are arithmetic.
    [GeneratedRegex(@"/[A-Za-z_.\-]|^/|[:=]/")]
    private static partial Regex SlashWord();
}

/// <summary>A synthetic file with the source's characteristics, and what it cannot reproduce.</summary>
public sealed record Recipe(string Command, IReadOnlyList<string> Notes);

/// <summary>
/// Builds a copy-pasteable ffmpeg command for a synthetic file with the same technical characteristics as the source.
/// Only numbers and values from fixed tables or checked tokens go into it; the output is called synthetic.*.
/// </summary>
public static class RecipeBuilder
{
    /// <summary>Long enough for several slices and short to encode; a length mismatch between video and audio is kept.</summary>
    public const double DefaultLength = 420;

    private static readonly Dictionary<string, string> VideoEncoders = new(StringComparer.Ordinal)
    {
        ["h264"] = "libx264", ["hevc"] = "libx265", ["av1"] = "libsvtav1", ["vp9"] = "libvpx-vp9", ["vp8"] = "libvpx",
        ["mpeg2video"] = "mpeg2video", ["mpeg4"] = "mpeg4", ["mpeg1video"] = "mpeg1video",
    };

    private static readonly Dictionary<string, string> AudioEncoders = new(StringComparer.Ordinal)
    {
        ["aac"] = "aac", ["ac3"] = "ac3", ["eac3"] = "eac3", ["mp3"] = "libmp3lame", ["mp2"] = "mp2", ["flac"] = "flac", ["opus"] = "libopus", ["vorbis"] = "libvorbis",
        ["dts"] = "dca", ["truehd"] = "truehd", ["pcm_s16le"] = "pcm_s16le", ["pcm_s24le"] = "pcm_s24le", ["alac"] = "alac",
    };

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Extension(string? container) => container switch
    {
        null => "mkv",
        _ when container.StartsWith("matroska", StringComparison.Ordinal) => "mkv",
        _ when container.StartsWith("mov", StringComparison.Ordinal) => "mp4",
        _ when container.Contains("mpegts", StringComparison.Ordinal) => "ts",
        "avi" => "avi",
        _ => "mkv",
    };

    /// <param name="machineFileStart">The file's start as a worker's ffmpeg read it: used when the server's ffprobe shows the video starting at 0.</param>
    public static Recipe? Build(SourceFingerprint source, PacketStats? stats, StreamEnds? ends, double? machineFileStart = null)
    {
        if (source.Video is not { } v || v.Width <= 0 || v.Height <= 0)
            return null;
        var notes = new List<string>();
        string rate = v.RFrameRate is { } r && Regex.IsMatch(r, @"^[1-9]\d{0,6}/[1-9]\d{0,6}$") ? r : "25/1";
        if (v.AvgFrameRate != null && v.AvgFrameRate != v.RFrameRate && v.AvgFrameRate != "0/0")
            notes.Add($"declared average rate {v.AvgFrameRate}, base rate {rate}");
        if (stats?.ConstantRate == false)
            notes.Add($"the sample shows a variable frame rate (frame times {F(stats.MinDelta ?? 0)}-{F(stats.MaxDelta ?? 0)} s): this command gives a constant one");
        if (v.FieldOrder is { } order && order != "progressive" && order != "unknown")
            notes.Add($"source interlaced ({order}): this command gives progressive video");

        double duration = source.Duration ?? DefaultLength;
        double videoStart = v.Start is { } own && own != 0 ? own : machineFileStart ?? 0;
        // Under a second, streams end together as far as a conversion is concerned.
        double shorter = ends?.Video is { } ve && ends.Audio.Count > 0 && ends.Audio.Values.Max() - ve >= 1 ? ends.Audio.Values.Max() - ve : 0;
        double length = Math.Min(duration, Math.Max(DefaultLength, shorter + 120));
        var args = new List<string> { "ffmpeg", "-y", "-hide_banner" };

        // The whole output is moved to the video's start (ffmpeg snaps an input offset of the video to its frame grid),
        // and each audio input relative to it. Checked with ffmpeg 9: streams then start where the source's do.
        args.AddRange(["-t", F(Math.Max(1, length - shorter)), "-f", "lavfi", "-i", $"\"testsrc2=size={v.Width}x{v.Height}:rate={rate}\""]);
        var maps = new List<string> { "-map", "0:v" };
        var audioOptions = new List<string>();
        int n = 0;
        bool strict = false;
        foreach (var a in source.Audio)
        {
            if ((a.Start ?? 0) - videoStart is var relative && relative != 0)
                args.AddRange(["-itsoffset", F(relative)]);
            int sampleRate = a.SampleRate is > 0 and var sr ? sr : 48000;
            int channels = Math.Clamp(a.Channels ?? 2, 1, 16);
            string layout = a.ChannelLayout is { } cl && cl != "unknown" ? cl : channels switch { 1 => "mono", 6 => "5.1", 8 => "7.1", _ => "stereo" };
            args.AddRange(["-t", F(length), "-f", "lavfi", "-i", $"\"anullsrc=r={sampleRate}:cl={layout}\""]);
            maps.AddRange(["-map", $"{n + 1}:a"]);
            string? codec = a.Codec;
            if (codec is null || !AudioEncoders.TryGetValue(codec, out string? encoder))
            {
                notes.Add($"audio track {n + 1}: no encoder for codec {codec ?? "?"}, aac used instead");
                encoder = "aac";
            }
            strict |= encoder is "dca" or "truehd";
            audioOptions.AddRange([$"-c:a:{n}", encoder]);
            n++;
        }

        args.AddRange(maps);
        string? videoCodec = v.Codec;
        if (videoCodec is null || !VideoEncoders.TryGetValue(videoCodec, out string? video))
        {
            notes.Add($"no encoder for video codec {videoCodec ?? "?"}: libx264 used instead");
            video = "libx264";
        }
        args.AddRange(["-c:v", video]);
        int gop = stats?.GopMedian is > 0 and var g ? g : 250;
        int bframes = Math.Clamp(v.HasBFrames ?? 0, 0, 8);
        if (stats?.GopMedian is null)
            notes.Add("GOP longer than the sample or unknown: 250 frames assumed");
        switch (video)
        {
            case "libx265":
                args.AddRange(["-preset", "ultrafast", "-x265-params", $"\"log-level=error:keyint={gop}:min-keyint={gop}:scenecut=0:bframes={bframes}\""]);
                break;
            case "libx264":
                args.AddRange(["-preset", "ultrafast", "-g", $"{gop}", "-keyint_min", $"{gop}", "-sc_threshold", "0", "-bf", $"{bframes}"]);
                break;
            case "libsvtav1":
                args.AddRange(["-preset", "12", "-g", $"{gop}"]);
                break;
            default:
                args.AddRange(["-g", $"{gop}"]);
                break;
        }
        if (v.PixelFormat != null)
            args.AddRange(["-pix_fmt", v.PixelFormat]);
        if (Profile(videoCodec, v.Profile) is { } profile)
            args.AddRange(["-profile:v", profile]);
        foreach (var (option, value) in new[] { ("-color_primaries", v.ColorPrimaries), ("-color_trc", v.ColorTransfer), ("-colorspace", v.ColorSpace), ("-color_range", v.ColorRange) })
            if (value != null && value != "unknown")
                args.AddRange([option, value]);
        args.AddRange(audioOptions);
        if (strict)
            args.AddRange(["-strict", "-2"]);
        if (videoStart != 0)
            args.AddRange(["-output_ts_offset", F(videoStart)]);
        args.Add("synthetic." + Extension(source.Container));

        if (shorter > 0)
            notes.Add($"the source's video ends {F(shorter)} s before its audio: kept in this command");
        if (length < duration)
            notes.Add($"length cut to {F(length)} s (the source lasts {F(duration)} s)");
        if (source.Audio.Any(a => a.Codec == "aac"))
            notes.Add("the aac encoder moves the audio's start 1024 samples earlier (21.3 ms at 48 kHz): compensate in -itsoffset when the file's start matters");
        if (source.Streams.Any(s => s.Cover))
            notes.Add("the source has cover art (attached picture): not reproduced");
        int subtitles = source.Streams.Count(s => s.Type == "subtitle");
        if (subtitles > 0)
            notes.Add($"{subtitles} subtitle track(s) not reproduced");
        if (source.Chapters > 0)
            notes.Add($"{source.Chapters} chapter(s) not reproduced");
        if (source.Start is { } cs && Math.Abs(cs - new[] { videoStart }.Concat(source.Audio.Select(a => a.Start ?? 0)).Min()) > 1e-6)
            notes.Add($"the container's start ({F(cs)} s) differs from its streams': check it on the result");
        return new Recipe(string.Join(" ", args), notes);
    }

    private static string? Profile(string? codec, string? profile) => (codec, profile) switch
    {
        ("h264", "Constrained Baseline" or "Baseline") => "baseline",
        ("h264", "Main") => "main",
        ("h264", "High") => "high",
        ("h264", "High 10") => "high10",
        ("h264", "High 4:2:2") => "high422",
        ("hevc", "Main") => "main",
        ("hevc", "Main 10") => "main10",
        _ => null,
    };
}
