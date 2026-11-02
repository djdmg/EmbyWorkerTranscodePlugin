using System.Globalization;
using System.Text.RegularExpressions;

namespace RemoteTranscode.Common;

/// <summary>What is read from ffmpeg's log.</summary>
public static partial class FfmpegLog
{
    /// <summary>
    /// The input's start from the line under "Input #0" ("  Duration: 00:52:01.12, start: 0.005000, bitrate: ..."); null on
    /// any other line. Two builds of ffmpeg can read different starts on one file (Emby's 5.1 and the workers' 9). Only the
    /// number is taken: that line holds no name, and the one above it, which holds the path, does not match.
    /// </summary>
    public static double? InputStart(string line)
    {
        var m = StartLine().Match(line);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double start) ? start : null;
    }

    [GeneratedRegex(@"^(?:\[\w+\] )?\s*Duration: [^,]+, start: (-?\d+(?:\.\d+)?),")]
    private static partial Regex StartLine();
}
