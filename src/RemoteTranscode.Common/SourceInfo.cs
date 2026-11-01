namespace RemoteTranscode.Common;

/// <summary>What ffprobe says about a media file.</summary>
public sealed record SourceInfo(double DurationSeconds, int Width, int Height, string? ColorTransfer, int AudioStreams, int SubtitleStreams)
{
    /// <summary>PQ (HDR10/Dolby Vision) or HLG transfer.</summary>
    public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67";

    /// <summary>
    /// A conversion result is accepted only if it is as long as its source (0.05 %, at least 1.5 s of tolerance) and
    /// has a video stream; anything else is a failed conversion, never a delivered file.
    /// </summary>
    public static string? VerifyDuration(SourceInfo source, SourceInfo result)
    {
        double tolerance = Math.Max(1.5, source.DurationSeconds * 0.0005);
        if (Math.Abs(result.DurationSeconds - source.DurationSeconds) > tolerance)
            return $"durée {result.DurationSeconds:0.0}s au lieu de {source.DurationSeconds:0.0}s";
        return result.Height <= 0 ? "pas de piste vidéo" : null;
    }

    /// <summary>
    /// The result's video lasts as long as the source's, within one and a half frames: a missing frame at the end, or
    /// a repeated one, fails. A source whose packets lack presentation times (AVI with packed B-frames) has its end
    /// known to a couple of frames only (its last decoded frame has no time): three frames there. Not checked (null)
    /// when either end could not be read.
    /// </summary>
    public static string? VerifyVideoSpan(VideoSpan source, VideoSpan result)
    {
        if (!source.IsKnown || !result.IsKnown)
            return null;
        double frame = source.FrameSeconds > 0 ? source.FrameSeconds : result.FrameSeconds > 0 ? result.FrameSeconds : 0.04;
        double difference = result.Length - source.Length;
        return Math.Abs(difference) > frame * (source.Approximate ? 3 : 1.5)
            ? $"vidéo de {result.Length:0.000}s au lieu de {source.Length:0.000}s ({difference / frame:+0;-0} images)"
            : null;
    }
}

/// <summary>
/// Where a file's video starts and ends (seconds), and its frame duration (0 when unknown). Approximate: some packets
/// had no presentation time, the end comes from decoding times.
/// </summary>
public sealed record VideoSpan(double Start, double End, double FrameSeconds, bool Approximate = false)
{
    public bool IsKnown => !double.IsNaN(End) && End > Start;
    public double Length => End - Start;
    public override string ToString() => IsKnown ? $"{Start:0.000}-{End:0.000}s" : "inconnue";
}
