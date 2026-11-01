using System.Globalization;

namespace RemoteTranscode.Common;

/// <summary>
/// Formats stderr lines exactly the way Emby's ffmpeg fork prints them, because Emby's
/// TranscodingStateReader parses them to learn about progress and finished segments.
/// </summary>
public static partial class EmbyFfmpegOutput
{
    // Same commands as the fork's interactive help, minus "t" (throttling) which the relay does not implement:
    // Emby then simply disables throttling for this transcode.
    public const string HelpText =
        "key    function\n" +
        "?      show this help\n" +
        "q      quit\n";

    /// <summary>Local-time "HH:mm:ss.fff " prefix of -loglevel +timing.</summary>
    public static string Prefix(DateTime now) => now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " ";

    /// <summary>
    /// The line the fork prints when a segment file is closed; Emby considers segment <paramref name="index"/>
    /// servable once it reads it. Times are absolute seconds; <paramref name="fileName"/> has no directory.
    /// </summary>
    public static string SegmentComplete(int index, double start, double end, string fileName) =>
        string.Create(CultureInfo.InvariantCulture,
            $"SegmentComplete=video:0 Index={index} Start={start:F6} End={end:F6} Duration={end - start:F6} offset_pts=0 start_pts={(long)Math.Round(start * 1_000_000)} Frames=0 filename={fileName}");

    [System.Text.RegularExpressions.GeneratedRegex(@"SegmentComplete=\S* Index=(\d+) Start=(-?[\d.]+) End=(-?[\d.]+).*?filename=(\S+)")]
    private static partial System.Text.RegularExpressions.Regex SegmentCompleteLine();

    /// <summary>Reads a "SegmentComplete=" line printed by Emby's ffmpeg (the line Emby itself follows).</summary>
    public static bool TryParseSegmentComplete(string line, out int index, out double start, out double end, out string? fileName)
    {
        var m = SegmentCompleteLine().Match(line);
        index = 0; start = 0; end = 0; fileName = null;
        if (!m.Success)
            return false;
        index = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        start = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        end = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        fileName = m.Groups[4].Value;
        return true;
    }

    /// <summary>
    /// A progress line. Emby reads "time=" as the position transcoded since its -ss, and "throttle=off" tells it the
    /// relay does not implement throttling.
    /// </summary>
    public static string Progress(TimeSpan elapsed, long frame, double fps, double timeSeconds, double speed) =>
        string.Create(CultureInfo.InvariantCulture,
            $"elapsed={Clock(elapsed.TotalSeconds)} frame={frame,5} fps={fps:0.#} q=-1.0 size=N/A time={Clock(timeSeconds)} bitrate=N/A throttle=off speed={speed:0.##}x");

    private static string Clock(double seconds)
    {
        if (seconds < 0)
            seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return string.Create(CultureInfo.InvariantCulture, $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 10:00}");
    }
}
