using System.Globalization;
using System.Text.Json;

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
    /// The result's video starts where the source's does from the file's start, within half a frame (5 ms at least):
    /// otherwise it is out of step with its audio from the first frame to the last, which the length check cannot see.
    /// Not checked when the source's times are approximate (AVI without presentation times) or unknown.
    /// </summary>
    public static string? VerifyVideoStart(VideoSpan source, double? sourceFileStart, VideoSpan result)
    {
        if (!source.IsKnown || !result.IsKnown || source.Approximate || sourceFileStart is not { } fileStart)
            return null;
        double frame = source.FrameSeconds > 0 ? source.FrameSeconds : result.FrameSeconds > 0 ? result.FrameSeconds : 0.04;
        double expected = source.Start - fileStart;
        return Math.Abs(result.Start - expected) > Math.Max(0.005, frame / 2)
            ? $"vidéo commençant à {result.Start:0.000}s au lieu de {expected:0.000}s : décalée par rapport à l'audio"
            : null;
    }

    /// <summary>Largest difference allowed between where an audio stream starts in the source and in the result.</summary>
    public const double AudioStartTolerance = 0.015;

    /// <summary>
    /// Each audio stream of the result starts where it does in the source, from the file's start, within
    /// <see cref="AudioStartTolerance"/>; <paramref name="maps"/> lists the source streams Emby's command maps, in order (the
    /// result holds its audio streams in that order). A stream starting with the file reads 0 in an MP4 whatever the encoder's
    /// priming, the edit list hides it; a stream starting later reads its first packet, priming included (0.478667 s for AAC at
    /// 48 kHz where the sound starts at 0.5 s, measured with ffmpeg 9.0.2), so such a stream may read that much earlier
    /// (see <see cref="StreamStart.Priming"/>). Nothing is judged on a value that is missing, nor when the streams do not
    /// correspond (their number is not this check's business).
    /// </summary>
    public static string? VerifyAudioStarts(StreamStarts source, IReadOnlyList<int> maps, StreamStarts result)
    {
        if (source.FileStart is not { } fileStart)
            return null;
        var from = maps.Select(m => source.Streams.FirstOrDefault(s => s.Index == m)).Where(s => s?.Type == "audio").ToList();
        var to = result.Streams.Where(s => s.Type == "audio").ToList();
        if (from.Count != to.Count)
            return null;
        for (int i = 0; i < to.Count; i++)
        {
            if (from[i]?.Start is not { } start || to[i].Start is not { } got)
                continue;
            double expected = start - fileStart;
            double early = expected > AudioStartTolerance ? to[i].Priming : 0;
            if (got > expected + AudioStartTolerance || got < expected - AudioStartTolerance - early)
                return string.Create(CultureInfo.InvariantCulture, $"audio {i + 1} commençant à {got:0.000}s au lieu de {expected:0.000}s : décalé par rapport à la vidéo");
        }
        return null;
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

/// <summary>A stream of a file as ffprobe reads it; <see cref="Start"/> is null when it gives none.</summary>
public sealed record StreamStart(int Index, string? Type, string? Codec, int SampleRate, double? Start)
{
    /// <summary>
    /// Seconds of priming the encoder puts before the sound, for the encoders Emby's assembly may use: 1024 samples for AAC and
    /// 1105 for MP3 (measured); AC-3's 256 samples and a copied stream's first packet stay within the tolerance.
    /// </summary>
    public double Priming => SampleRate <= 0 ? 0 : Codec switch
    {
        "aac" => 1024.0 / SampleRate,
        "mp3" => 1105.0 / SampleRate,
        _ => 0,
    };
}

/// <summary>Where a file and its streams start, as ffprobe reads them (see <see cref="Parse"/>).</summary>
public sealed record StreamStarts(double? FileStart, IReadOnlyList<StreamStart> Streams)
{
    /// <summary>The ffprobe arguments for <see cref="Parse"/>.</summary>
    public static string[] ProbeArgs(string path) =>
        ["-v", "error", "-show_entries", "format=start_time:stream=index,codec_type,codec_name,sample_rate,start_time", "-print_format", "json", path];

    /// <summary>ffprobe's JSON; what it does not give (start_time "N/A" or absent) stays null.</summary>
    public static StreamStarts Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        double? fileStart = root.TryGetProperty("format", out var format) ? Number(format, "start_time") : null;
        var streams = new List<StreamStart>();
        if (root.TryGetProperty("streams", out var list))
            foreach (var s in list.EnumerateArray())
                streams.Add(new StreamStart(s.TryGetProperty("index", out var i) ? i.GetInt32() : -1, Text(s, "codec_type"), Text(s, "codec_name"),
                    (int)(Number(s, "sample_rate") ?? 0), Number(s, "start_time")));
        return new StreamStarts(fileStart, streams);

        static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static double? Number(JsonElement e, string name) =>
            double.TryParse(Text(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
    }
}
