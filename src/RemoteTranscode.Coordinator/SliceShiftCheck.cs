using System.Globalization;

namespace RemoteTranscode.Coordinator;

/// <summary>A worker run whose first frame is known: the source frame it must start on and the file's start as each side reads it.</summary>
public sealed record RunStart(int Slice, int Chunk, double Start, double MachineFileStart, double ServerFileStart);

/// <summary>
/// Before assembling: did a worker run keep the frames it was asked for? A run that starts one frame late also ends one
/// frame late, so its frame count, its listed times and the assembled length all stay right (seen on 2026-11-02: the
/// picture alternated between -60 and -20 ms from the source at each worker/server boundary). Only the timestamps it
/// encoded tell, once they are read in the right numbering. With -copyts -start_at_zero the worker's ffmpeg numbers frames
/// from the file's start as it reads it, the slice's boundaries come from the server's reading, and MPEG-TS adds its
/// muxer delay, so a run on the right frame has
/// <code>first encoded time - muxer delay + (worker's file start - server's file start) = slice start</code>
/// Measured with ffmpeg 9.0.2 on a source starting at 0.021 s (server reading 0): 0 on the right frame, one frame on the
/// late one, at 25, 29.97 and 60 fps. The same comparison without the two file starts gives +19 ms and -21 ms at 25 fps,
/// under half a frame both: it cannot tell them apart.
/// <para>
/// With a frame rate set (Emby's -r, otherwise the source's own), ffmpeg puts encoded times on its grid: up to half an output
/// frame from a frame's own time, either way even from one slice to the next when the video lies halfway on it (+20.7 and -20.7 ms
/// measured at 23.976 fps). So only more than <see cref="Threshold"/> of a frame counts as a shift. This never rejects a run
/// on the right frame; it misses a late run whose grid rounding happens to pull it back by more than a quarter of a frame,
/// which takes a video that does not start with the file on top of a disagreement on the file's start.
/// </para>
/// Not judged, because their times say nothing: a run without seek (the muxer moves its first chunk by the encoder's delay, 2 frames
/// measured, and no seek means no frame to miss), a run starting on a time that is not a known frame, a run whose worker did
/// not report its file's start, and a run near the file's start. A run past that whose times are not close to the source's (a build
/// counting from the run's start), or whose chunk cannot be read, rejects the conversion (see <see cref="MustBeJudged"/>): nothing else
/// could see it. Server runs are not judged: their build and the boundaries' ffprobe read the file alike, so
/// they cannot miss their frame this way, and the encoded times of Emby's 5.1 fork could not be measured here.
/// </summary>
public static class SliceShiftCheck
{
    /// <summary>Delay the MPEG-TS muxer adds to every timestamp (twice its default max_delay of 0.7 s).</summary>
    public const double MuxerDelay = 1.4;

    /// <summary>Half a frame is normal and a missed frame is a whole one: the threshold, in output frames, lies halfway.</summary>
    public const double Threshold = 0.75;

    /// <summary>Frames read in a run's first chunk: its first frame is the encoder's first, and the frame rate needs a few.</summary>
    public const int Frames = 8;

    // Far more than 8 frames of any video: a file that does not get there is not what is expected and stays unjudged.
    private const int MaxBytes = 32 << 20;
    private const int TsPacket = 188;

    /// <summary>A run starting closer to the file's start than this could count from its own start and still look close.</summary>
    public const double MinStart = 10;

    /// <summary>Further than this, the chunk's times are not source times: nothing can be said.</summary>
    public const double MaxShift = 5;

    /// <summary>Starts every rejection's reason: <see cref="DiagnosticsAnalysis"/> gives it its verdict.</summary>
    public const string ErrorPrefix = "images décalées";

    /// <summary>
    /// The presentation times of the first frames of an MPEG-TS chunk, read from the file itself: a chunk holds video only, one
    /// PES packet per frame. No ffprobe, because each process costs more than the read: one per slice is seconds of assembly on a slow
    /// machine (4 s each measured on a PC whose security software checks every process start) against 91 ms for 65 chunks, and
    /// ffprobe 9.0.2 lists no video packet in a chunk of AV1. Same first times as ffprobe on H.264 and HEVC chunks, software
    /// and QSV. Empty when the file is not plain MPEG-TS.
    /// </summary>
    public static List<double> FirstFrameTimes(Stream chunk)
    {
        var times = new List<double>(Frames);
        var buffer = new byte[TsPacket];
        for (int read = 0; read < MaxBytes && times.Count < Frames; read += TsPacket)
        {
            if (chunk.ReadAtLeast(buffer, TsPacket, throwOnEndOfStream: false) < TsPacket || buffer[0] != 0x47)
                break;
            if (PesTime(buffer) is { } time)
                times.Add(time);
        }
        return times;
    }

    /// <summary>The frames of an MPEG-TS chunk, read from the whole file (one PES packet per frame, see <see cref="FirstFrameTimes"/>); 0 when it is not plain MPEG-TS.</summary>
    public static int CountFrames(Stream chunk)
    {
        int frames = 0;
        var buffer = new byte[TsPacket];
        while (chunk.ReadAtLeast(buffer, TsPacket, throwOnEndOfStream: false) == TsPacket)
        {
            if (buffer[0] != 0x47)
                return 0;
            if (PesTime(buffer) != null)
                frames++;
        }
        return frames;
    }

    /// <summary>The PTS of the PES packet starting in this TS packet, if any; tables (PAT, PMT...) never look like one.</summary>
    private static double? PesTime(byte[] ts)
    {
        if ((ts[1] & 0x40) == 0 || (ts[3] & 0x10) == 0)
            return null;
        int p = (ts[3] & 0x20) != 0 ? 5 + ts[4] : 4;
        if (p + 14 > TsPacket || ts[p] != 0 || ts[p + 1] != 0 || ts[p + 2] != 1 || (ts[p + 7] & 0x80) == 0)
            return null;
        long pts = ((long)(ts[p + 9] >> 1 & 7) << 30) | ((long)ts[p + 10] << 22) | ((long)(ts[p + 11] >> 1) << 15) | ((long)ts[p + 12] << 7) | ((long)ts[p + 13] >> 1);
        return pts / 90000.0;
    }

    /// <summary>What <see cref="DiagnosticsAnalysis"/> makes of those times: the lowest one and the frame's length.</summary>
    public static ChunkMeasure MeasureStart(int index, IReadOnlyList<double> times) =>
        new(index, times.Count, times.Count > 0 ? times.Min() : null, times.Count > 0 ? times.Max() : null,
            DiagnosticsAnalysis.Stats(times.Select(t => new PacketInfo(t, null, null, null, null)).ToList()).FrameSeconds);

    /// <summary>
    /// How far a run's first frame is from where it should be (seconds), null when its times say nothing.
    /// </summary>
    public static double? Shift(RunStart run, double? firstPts)
    {
        if (firstPts is not { } first || run.Start < MinStart)
            return null;
        double shift = first - MuxerDelay + run.MachineFileStart - run.ServerFileStart - run.Start;
        return Math.Abs(shift) < MaxShift ? shift : null;
    }

    /// <summary>A shift over <see cref="Threshold"/> of an output frame (the one set by Emby's command, else the one measured in the chunk).</summary>
    public static bool IsShifted(double shift, double? setFrame, double? measuredFrame) =>
        (setFrame ?? measuredFrame) is > 0 and var frame && Math.Abs(shift) > Threshold * frame;

    /// <summary>
    /// A run whose first frame must be placed for its conversion to pass: one that seeks far enough from the file's start for its times to say
    /// something. Nothing else can see a run on the wrong frame, so one that cannot be placed is not taken as a good one.
    /// </summary>
    public static bool MustBeJudged(RunStart run) => run.Start >= MinStart;

    /// <summary>The reason for runs that could not be placed; null when none. Same start as <see cref="Problem"/>: same fault, same verdict.</summary>
    public static string? Unread(IReadOnlyList<int> slices) =>
        slices.Count == 0 ? null : $"{ErrorPrefix} ? première image illisible dans les tranches {string.Join(", ", slices.Distinct().OrderBy(s => s).Take(12))}";

    /// <summary>The reason a conversion is rejected for, numbers only; null when no slice is shifted.</summary>
    public static string? Problem(IReadOnlyList<(int Slice, double Shift)> shifted)
    {
        if (shifted.Count == 0)
            return null;
        var listed = shifted.OrderBy(s => s.Slice).Take(12).Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Slice} ({s.Shift * 1000:+0;-0} ms)"));
        string more = shifted.Count > 12 ? $" et {shifted.Count - 12} autre(s)" : "";
        return $"{ErrorPrefix} par rapport à la source dans les tranches {string.Join(", ", listed)}{more}";
    }
}
