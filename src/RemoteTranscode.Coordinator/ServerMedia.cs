using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using RemoteTranscode.Common;

namespace RemoteTranscode.Coordinator;

/// <summary>Runs Emby's own ffprobe/ffmpeg on the server: source analysis and final assembly of background encodes.</summary>
public sealed class ServerMedia(CoordinatorOptions options)
{
    /// <summary>Duration, video dimensions and transfer, stream counts of a source; throws when ffprobe fails or the output is unexpected.</summary>
    public async Task<SourceInfo> ProbeAsync(string path, CancellationToken token)
    {
        var (code, output, error) = await RunAsync(options.ServerFfprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], token);
        if (code != 0)
            throw new InvalidOperationException($"ffprobe a échoué : {Tail(error)}");
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        double duration = double.Parse(root.GetProperty("format").GetProperty("duration").GetString() ?? "0", CultureInfo.InvariantCulture);
        int width = 0, height = 0, audio = 0, subs = 0;
        string? transfer = null;
        foreach (var s in root.GetProperty("streams").EnumerateArray())
        {
            switch (s.GetProperty("codec_type").GetString())
            {
                // Cover art is stored as a video stream: only the first real video counts.
                case "video" when height == 0 && !(s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1):
                    width = s.GetProperty("width").GetInt32();
                    height = s.GetProperty("height").GetInt32();
                    transfer = s.TryGetProperty("color_transfer", out var t) ? t.GetString() : null;
                    break;
                case "audio":
                    audio++;
                    break;
                case "subtitle":
                    subs++;
                    break;
            }
        }
        return new SourceInfo(duration, width, height, transfer, audio, subs);
    }

    /// <summary>
    /// Technical description of a source for a failure report (container, video codec and format, audio codecs);
    /// nothing else from the file (no tags: they hold titles). Throws when ffprobe fails.
    /// </summary>
    public async Task<SourceDescription> DescribeAsync(string path, CancellationToken token)
    {
        var (code, output, error) = await RunAsync(options.ServerFfprobe,
            ["-v", "error", "-print_format", "json",
             "-show_entries", "format=format_name,duration:stream=codec_type,codec_name,profile,pix_fmt,bits_per_raw_sample,width,height,avg_frame_rate,r_frame_rate,start_time,color_transfer,color_primaries:stream_disposition=attached_pic",
             path], token);
        if (code != 0)
            throw new InvalidOperationException($"ffprobe a échoué : {Tail(error)}");
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        static double? Number(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;
        var format = root.TryGetProperty("format", out var f) ? f : default;
        string? container = format.ValueKind == JsonValueKind.Object ? Text(format, "format_name") : null;
        double duration = format.ValueKind == JsonValueKind.Object ? Number(Text(format, "duration")) ?? 0 : 0;
        JsonElement? video = null;
        var audio = new List<string>();
        var list = root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array ? streams.EnumerateArray().ToList() : [];
        foreach (var s in list)
        {
            string? type = Text(s, "codec_type");
            bool cover = s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
            if (type == "video" && video is null && !cover)
                video = s.Clone();
            else if (type == "audio")
                audio.Add(Text(s, "codec_name") ?? "?");
        }
        if (video is not { } v)
            return new SourceDescription(container, null, null, null, null, 0, 0, null, null, duration, null, null, audio);
        string? pixFmt = Text(v, "pix_fmt");
        int? bits = int.TryParse(Text(v, "bits_per_raw_sample"), out int b) ? b
            : pixFmt is null ? null
            : System.Text.RegularExpressions.Regex.Match(pixFmt, @"p(\d+)(?:le|be)$") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 8;
        string? rate = Text(v, "avg_frame_rate") is { } avg && avg != "0/0" ? avg : Text(v, "r_frame_rate");
        if (rate?.Split('/') is [var num, var den] && Number(num) is { } n && Number(den) is { } dd && dd > 0)
            rate = $"{(n / dd).ToString("0.###", CultureInfo.InvariantCulture)} ({rate})";
        return new SourceDescription(container, Text(v, "codec_name"), Text(v, "profile"), pixFmt, bits, Int(v, "width"), Int(v, "height"),
            rate, Number(Text(v, "start_time")), duration, Text(v, "color_transfer"), Text(v, "color_primaries"), audio);
    }

    /// <summary>A stream as ffprobe lists it: its type and the disposition flags set on it.</summary>
    public sealed record StreamEntry(int Index, string Type, string Codec, IReadOnlyList<string> Dispositions);

    /// <summary>A file's start time (microseconds, exactly as ffmpeg computes it), duration and streams.</summary>
    public sealed record StreamList(long StartMicros, double DurationSeconds, IReadOnlyList<StreamEntry> Streams);

    /// <summary>Start time, duration and streams of a file; throws when ffprobe fails or prints something unexpected.</summary>
    public async Task<StreamList> StreamsAsync(string path, CancellationToken token)
    {
        var (code, output, error) = await RunAsync(options.ServerFfprobe,
            ["-v", "error", "-print_format", "json", "-show_entries", "format=start_time,duration:stream=index,codec_type,codec_name:stream_disposition", path], token);
        if (code != 0)
            throw new InvalidOperationException($"ffprobe a échoué : {Tail(error)}");
        using var doc = JsonDocument.Parse(output);
        var format = doc.RootElement.GetProperty("format");
        // Printed with six decimals from an integer number of microseconds: parsed as a decimal, it is exact.
        long start = format.TryGetProperty("start_time", out var st) && decimal.TryParse(st.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal s)
            ? (long)Math.Round(s * 1_000_000m)
            : throw new InvalidOperationException("début du fichier inconnu");
        double duration = double.Parse(format.GetProperty("duration").GetString() ?? "0", CultureInfo.InvariantCulture);
        var streams = new List<StreamEntry>();
        foreach (var e in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            var flags = new List<string>();
            if (e.TryGetProperty("disposition", out var d))
                foreach (var f in d.EnumerateObject())
                    if (f.Value.ValueKind == JsonValueKind.Number && f.Value.GetInt32() != 0)
                        flags.Add(f.Name);
            streams.Add(new StreamEntry(e.GetProperty("index").GetInt32(), e.GetProperty("codec_type").GetString() ?? "",
                e.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "", flags));
        }
        return new StreamList(start, duration, streams);
    }

    /// <summary>
    /// Where a file's packets end: the latest presentation time plus duration, in its own timestamps; null when no
    /// packet could be read. Reads every packet's times (no decoding, compact output): seeking with -read_intervals
    /// does not reliably find the last packets in every container.
    /// </summary>
    public async Task<double?> PacketsEndAsync(string path, CancellationToken token)
    {
        var (code, output, _) = await RunAsync(options.ServerFfprobe,
            ["-v", "error", "-show_entries", "packet=pts_time,duration_time", "-of", "csv=p=0", path], token);
        if (code != 0)
            return null;
        double? end = null;
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] f = line.Split(',');
            if (!double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double pts))
                continue;
            double length = f.Length > 1 && double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double l) ? l : 0;
            end = Math.Max(end ?? double.MinValue, pts + length);
        }
        return end;
    }
    /// <summary>
    /// A slice boundary: the time of the first video frame at or after the asked time (counted from the file's start
    /// like ffmpeg's -ss), and where in the file a run starting there begins reading (the keyframe it decodes from) and
    /// a run ending there stops (that frame). Positions are null when ffprobe did not report them.
    /// </summary>
    public sealed record Boundary(double Time, long? KeyframePosition, long? Position, double? SmallestFrameGap = null, double FileStart = 0, bool VariableRate = false);

    /// <summary>
    /// For each time, its <see cref="Boundary"/> (null where no frame was read). One ffprobe pass that reads packets
    /// only (no decoding) in a short window around each time, so even a large file with many slice boundaries is done
    /// in seconds.
    /// </summary>
    public async Task<Boundary?[]> BoundariesAsync(string path, IReadOnlyList<double> times, CancellationToken token)
    {
        var result = new Boundary?[times.Count];
        if (times.Count == 0)
            return result;
        // A video without presentation times on its packets (AVI with packed B-frames, placeholder frames): runs
        // seeking into it do not meet at the same frame, so it is not cut at all (no boundary: one slice).
        // MPEG-TS likewise: ffmpeg seeks on its PCR, ahead of the frames' times by the muxer's delay, so a run seeking to a frame
        // lands up to 0.7 s past it, on the next keyframe (measured with ffmpeg 9.0.2: 18 frames lost per slice). A run from the
        // start needs no seek: one slice.
        double fileStart = 0;
        string? format = null;
        if (await FramesAroundAsync(path, times, token, (s, f) => (fileStart, format) = (s, f)) is not { } frames
            || format?.Contains("mpegts", StringComparison.Ordinal) == true)
            return result;
        // For each time, the smallest presentation time at or after it. A window always contains that frame (and the
        // keyframe before it), so it is the true next frame.
        var times0 = frames.Select(f => f.Time).ToList();
        // Between frames read in the same window; the gap between two windows is seconds long and never the smallest.
        double? smallestGap = times0.Zip(times0.Skip(1), (a, b) => b - a).Where(gap => gap > 0).Select(gap => (double?)gap).Min();
        bool variable = IsVariableRate(times0);
        for (int i = 0; i < times.Count; i++)
        {
            int k = times0.BinarySearch(times[i] - 1e-6);
            if (k < 0)
                k = ~k;
            // From the start of the file: the first frame, however late it comes.
            if (k >= frames.Count || !(times[i] <= 0 || frames[k].Time < times[i] + 5))
                continue;
            long? keyframe = frames.Take(k + 1).LastOrDefault(f => f.Key).Position;
            result[i] = new Boundary(frames[k].Time, keyframe, frames[k].Position, smallestGap, fileStart, variable);
        }
        return result;
    }

    /// <summary>
    /// Frames read around the boundaries (sorted times) are not regularly spaced, by the library diagnostic's rules. Gaps over a second are
    /// the jumps between two read windows. A few holes do not count; too few gaps say nothing.
    /// </summary>
    public static bool IsVariableRate(IReadOnlyList<double> sortedTimes)
    {
        var gaps = sortedTimes.Zip(sortedTimes.Skip(1), (a, b) => b - a).Where(gap => gap is > 0 and < 1).OrderBy(gap => gap).ToList();
        if (gaps.Count < LibraryDiagnostic.MinimumGaps)
            return false;
        double usual = gaps[gaps.Count / 2];
        double allowed = Math.Max(usual * LibraryDiagnostic.IrregularShare, LibraryDiagnostic.IrregularMinimum);
        return gaps.Count(gap => Math.Abs(gap - usual) > allowed) >= gaps.Count * LibraryDiagnostic.VariableRateShare;
    }

    /// <summary>
    /// Where the first frame comes out of the decoder, from the file's start (<see cref="MediaProbe.ParseFirstFrame"/>): where
    /// the assembly must start the video. Null when it could not be read.
    /// </summary>
    public async Task<double?> FirstFrameAsync(string path, CancellationToken token)
    {
        return await MediaProbe.FirstFrameAsync(async args =>
        {
            var (code, output, error) = await RunAsync(options.ServerFfprobe, args, token);
            return code == 0 ? output : throw new InvalidOperationException($"ffprobe a échoué : {Tail(error)}");
        }, path);
    }

    /// <summary>
    /// The time of the video frame nearest to <paramref name="time"/>, counted like the boundaries; null when none was
    /// read around it. Where a run's last chunk ended is listed on the run's output frame grid, which is not the
    /// source's when the video does not start on it (start time 0.007 s): the listed time is the frame's own time
    /// rounded by up to half a frame (see EncodeQueue.ResumePointAsync).
    /// </summary>
    public async Task<double?> NearestFrameAsync(string path, double time, CancellationToken token) =>
        await FramesAroundAsync(path, [time], token) is { } frames ? NearestFrame(frames.Select(f => f.Time).ToList(), time) : null;

    /// <summary>
    /// The time of the video's last frame, counted like the boundaries; null when no frame was read in the last seconds of the file (a video
    /// ending more than 5 s before the file's <paramref name="duration"/>, or unreadable).
    /// </summary>
    public async Task<double?> LastFrameAsync(string path, double duration, CancellationToken token) =>
        await FramesAroundAsync(path, [duration], token) is { Count: > 0 } frames ? frames[^1].Time : null;

    /// <summary>The time nearest to <paramref name="time"/> in a sorted list, null when the list is empty.</summary>
    public static double? NearestFrame(List<double> sortedTimes, double time)
    {
        if (sortedTimes.Count == 0)
            return null;
        int k = sortedTimes.BinarySearch(time);
        if (k >= 0)
            return sortedTimes[k];
        k = ~k;
        if (k == 0)
            return sortedTimes[0];
        if (k == sortedTimes.Count)
            return sortedTimes[^1];
        return time - sortedTimes[k - 1] <= sortedTimes[k] - time ? sortedTimes[k - 1] : sortedTimes[k];
    }

    /// <summary>
    /// Video packets (presentation time from the file's start, position, keyframe) in a window around each time, in
    /// presentation order. One ffprobe pass that reads packets only (no decoding). Null when ffprobe fails or a packet
    /// has no presentation time.
    /// </summary>
    private async Task<List<(double Time, long? Position, bool Key)>?> FramesAroundAsync(string path, IReadOnlyList<double> times, CancellationToken token, Action<double, string?>? onFileStart = null)
    {
        static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        // "V": video but not cover art. Each interval seeks to the keyframe before its start and reads past the time;
        // intervals are in file timestamps, and the margins cover a start time of a few seconds. The start of the file
        // is read by packet count instead: the video may begin well after 0 (audio copied from earlier, for instance).
        string intervals = string.Join(",", times.Select(t => t <= 0 ? "%+#60" : $"{F(Math.Max(0, t - 5))}%{F(t + 5)}"));
        var (code, output, _) = await RunAsync(options.ServerFfprobe,
            ["-v", "error", "-select_streams", "V:0", "-read_intervals", intervals,
             "-show_entries", "packet=pts_time,pos,flags:format=start_time,format_name", "-print_format", "json", path], token);
        if (code != 0)
            return null;
        using var doc = JsonDocument.Parse(output);
        double start = 0;
        string? name = null;
        if (doc.RootElement.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("start_time", out var st) && double.TryParse(st.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
                start = s;
            if (format.TryGetProperty("format_name", out var fn))
                name = fn.GetString();
        }
        onFileStart?.Invoke(start, name);
        var frames = new List<(double Time, long? Position, bool Key)>();
        if (doc.RootElement.TryGetProperty("packets", out var packets))
            foreach (var p in packets.EnumerateArray())
            {
                if (!(p.TryGetProperty("pts_time", out var pt) && double.TryParse(pt.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double pts)))
                    return null;
                long? pos = p.TryGetProperty("pos", out var ps) && long.TryParse(ps.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;
                bool key = p.TryGetProperty("flags", out var fl) && (fl.GetString() ?? "").Contains('K');
                frames.Add((pts - start, pos, key));
            }
        // Packets come in decoding order and from all windows.
        frames.Sort((a, b) => a.Time.CompareTo(b.Time));
        return frames;
    }

    // Probed once; a concurrent first call may probe twice, which is harmless.
    private WorkerCapabilities? capabilities;

    /// <summary>
    /// What the server's own ffmpeg can do for a local fallback encode: software encoders and filters (Emby's build
    /// may lack some, e.g. zscale for HDR tone mapping, in which case such tasks wait for a worker).
    /// </summary>
    public async Task<WorkerCapabilities> CapabilitiesAsync(CancellationToken token)
    {
        if (capabilities != null)
            return capabilities;
        var (_, encoders, _) = await RunAsync(options.ServerFfmpeg, ["-hide_banner", "-encoders"], token);
        var (_, filters, _) = await RunAsync(options.ServerFfmpeg, ["-hide_banner", "-filters"], token);
        capabilities = new WorkerCapabilities
        {
            VideoEncoders = new[] { "libx264", "libx265" }.Where(e => encoders.Contains(" " + e + " ", StringComparison.Ordinal)).ToArray(),
            Filters = filters.Split('\n')
                .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"^\s*[T.][S.][C.]?\s+([A-Za-z0-9_]+)\s"))
                .Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct().ToArray(),
        };
        return capabilities;
    }

    /// <summary>
    /// Starts the server's ffmpeg at normal priority, exactly as Emby would without workers. The caller owns the
    /// process: it must drain stdout/stderr (or ffmpeg blocks) and dispose it.
    /// </summary>
    public Process Start(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(options.ServerFfmpeg)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        Prepare(psi);
        foreach (string a in args)
            psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    /// <summary>Same working folder and environment as Emby gives its own ffmpeg.</summary>
    private void Prepare(ProcessStartInfo psi)
    {
        // Emby's binaries load their libraries relative to the package folder.
        if (options.ServerToolsWorkingDirectory is { } dir && Directory.Exists(dir))
            psi.WorkingDirectory = dir;
        foreach (var (name, value) in options.ServerEnvironment)
            psi.Environment[name] = value;
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(string exe, IEnumerable<string> args, CancellationToken token)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        Prepare(psi);
        foreach (string a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        // Both pipes are read concurrently: reading one after the other can deadlock once the other's buffer is full.
        var stdout = p.StandardOutput.ReadToEndAsync(token);
        var stderr = p.StandardError.ReadToEndAsync(token);
        try
        {
            await p.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            throw;
        }
        return (p.ExitCode, await stdout, await stderr);
    }

    /// <summary>Largest output kept from an analysis tool (a whole video's packet times fit well under it).</summary>
    private const int MaxQuietOutput = 32 * 1024 * 1024;

    /// <summary>ffprobe at the lowest priority, for the failure reports: must not slow down a conversion.</summary>
    public Task<(int Code, string Output, string Error)> ProbeQuietlyAsync(IReadOnlyList<string> args, CancellationToken token) =>
        RunQuietlyAsync(options.ServerFfprobe, args, token);

    /// <summary>ffmpeg at the lowest priority, for the failure reports.</summary>
    public Task<(int Code, string Output, string Error)> FfmpegQuietlyAsync(IReadOnlyList<string> args, CancellationToken token) =>
        RunQuietlyAsync(options.ServerFfmpeg, args, token);

    private async Task<(int Code, string Output, string Error)> RunQuietlyAsync(string exe, IReadOnlyList<string> args, CancellationToken token)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        Prepare(psi);
        foreach (string a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        try
        {
            // Idle on Windows, nice 19 on Linux. Not allowed everywhere: the run goes on anyway.
            p.PriorityClass = ProcessPriorityClass.Idle;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException or NotSupportedException) { }
        using var running = CancellationTokenSource.CreateLinkedTokenSource(token);
        var stdout = ReadCappedAsync(p.StandardOutput, MaxQuietOutput, running);
        var stderr = ReadCappedAsync(p.StandardError, 64 * 1024, null);
        try
        {
            await p.WaitForExitAsync(running.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("output too long");
        }
        return (p.ExitCode, await stdout, await stderr);
    }

    /// <summary>Reads up to a limit; past it, stops the run (<paramref name="overflow"/>) or drops the rest, so a pipe never blocks the tool.</summary>
    private static async Task<string> ReadCappedAsync(StreamReader reader, int limit, CancellationTokenSource? overflow)
    {
        var text = new System.Text.StringBuilder();
        var buffer = new char[65536];
        int read;
        while ((read = await reader.ReadAsync(buffer, CancellationToken.None)) > 0)
        {
            if (text.Length + read <= limit)
                text.Append(buffer, 0, read);
            else
                overflow?.Cancel();
        }
        return text.ToString();
    }

    private static string Tail(string text) =>
        string.Join(" | ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3));
}
