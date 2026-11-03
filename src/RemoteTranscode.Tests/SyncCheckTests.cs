using RemoteTranscode.Common;

namespace RemoteTranscode.Tests;

/// <summary>
/// Whole-file A/V check of an assembled conversion. Times below are built the way ffprobe reads them: measured with ffmpeg 9.0.2 on real
/// assemblies (decoded and matched frame by frame), and the facts measured with Emby's ffmpeg 5.1.
/// </summary>
public class SyncCheckTests
{
    private const double Duration = 600;
    private const double Frame25 = 0.04;
    private const double Frame2398 = 1001.0 / 24000;
    private const double Ac3Packet = 1536.0 / 48000;
    private const double AacPacket = 1024.0 / 48000;
    private const double AacPriming = 1024.0 / 48000;

    private static ProbedStream Video(IEnumerable<double> times, double frame = Frame25, int index = 0, bool pts = true, double? start = null)
    {
        var packets = times.Select(t => new ProbedPacket(pts ? t : null, t, frame)).ToList();
        return new ProbedStream(index, "video", "h264", 0, start ?? packets[0].Time, frame, false, packets);
    }

    // The last packet ends with the stream, as an encoder's does.
    private static ProbedStream Audio(int index, string codec, IEnumerable<double> times, double packet, double? start = null, int rate = 48000, double end = Duration)
    {
        var packets = times.Select(t => new ProbedPacket(t, t, t + packet > end ? end - t : packet)).ToList();
        return new ProbedStream(index, "audio", codec, rate, start ?? packets[0].Time, 0, false, packets);
    }

    private static IEnumerable<double> Grid(double first, double step, double end)
    {
        for (long k = 0; first + k * step < end; k++)
            yield return first + k * step;
    }

    private static MediaProbe File(double? fileStart, params ProbedStream[] streams) => new(fileStart, Duration, "matroska,webm", 720, streams);

    private static MediaProbe Source(double fileStart = 0, double videoStart = 0, double audioStart = 0) => File(fileStart,
        Video(Grid(fileStart + videoStart, Frame25, fileStart + Duration)),
        Audio(1, "ac3", Grid(fileStart + audioStart, Ac3Packet, fileStart + Duration), Ac3Packet));

    private static MediaProbe Result(double videoStart = 0, double audioStart = 0) => File(0,
        Video(Grid(videoStart, Frame25, Duration)),
        Audio(1, "aac", Grid(audioStart - AacPriming, AacPacket, Duration), AacPacket, Math.Max(0, audioStart - AacPriming)));

    private static readonly SyncExpectation Encoded = new([1], ["aac"], null);
    private static readonly SyncExpectation Copied = new([1], ["copy"], null);
    private static IReadOnlyList<double> Windows => SyncCheck.WindowTimes(Duration, Grid(120, 120, Duration));

    private static string? Verify(MediaProbe source, double? firstFrame, MediaProbe result, SyncExpectation? expected = null) =>
        SyncCheck.Verify(source, firstFrame, result, expected ?? Encoded, Windows);

    // ---- A correct assembly passes ----

    [Fact]
    public void A_correct_assembly_passes() => Assert.Null(Verify(Source(), 0, Result()));

    [Fact]
    public void A_copied_stream_passes_with_its_packet_before_the_stream_start()
    {
        // AC-3 in Matroska: first packet at -5 ms for a stream starting at 0 (measured, 5.1), copied as it is.
        var packets = Grid(-0.005, Ac3Packet, Duration).ToList();
        var source = File(0, Video(Grid(0, Frame25, Duration)), Audio(1, "ac3", packets, Ac3Packet, 0));
        var result = File(0, Video(Grid(0, Frame25, Duration)), Audio(1, "ac3", packets, Ac3Packet, 0));
        Assert.Null(Verify(source, 0, result, Copied));
    }

    [Fact]
    public void A_file_starting_late_counts_from_its_start()
    {
        // MPEG-TS: everything from 1.4 s, the video 5.3 ms after the audio (measured), the result from 0.
        var source = Source(1.474667, 0.005333);
        source = source with { Format = "mpegts" };
        Assert.Null(Verify(source, 0.005333, Result(0.005333)));
    }

    [Fact]
    public void A_stream_starting_late_reads_its_priming_exactly()
    {
        // AAC at 48 kHz, sound at 0.5 s: 0.478667 measured.
        Assert.Null(Verify(Source(audioStart: 0.5), 0, Result(audioStart: 0.5)));
        // Video starting after the audio (79 ms and 21 ms, measured with 5.1).
        Assert.Null(Verify(Source(videoStart: 0.079), 0.079, Result(0.079)));
        Assert.Null(Verify(Source(videoStart: 0.021), 0.021, Result(0.021)));
    }

    [Fact]
    public void Video_rounded_by_matroska_and_audio_within_a_millisecond_pass()
    {
        var rounded = Grid(0, Frame2398, Duration).Select(t => Math.Round(t, 3));
        var source = File(0, Video(rounded, Frame2398), Audio(1, "ac3", Grid(0, Ac3Packet, Duration), Ac3Packet, 0));
        // Measured: ±0.9 ms between a frame and its source frame.
        var result = File(0, Video(Grid(0.0009, Frame2398, Duration), Frame2398, start: 0.0009), Audio(1, "aac", Grid(-AacPriming, AacPacket, Duration), AacPacket, 0));
        Assert.Null(Verify(source, 0, result));
    }

    [Fact]
    public void A_regridded_rate_passes_within_half_an_output_frame()
    {
        // -r 25 on 23.976 fps.
        var source = File(0, Video(Grid(0, Frame2398, Duration), Frame2398), Audio(1, "ac3", Grid(0, Ac3Packet, Duration), Ac3Packet, 0));
        var result = File(0, Video(Grid(0, Frame25, Duration)), Audio(1, "aac", Grid(-AacPriming, AacPacket, Duration), AacPacket, 0));
        Assert.Null(Verify(source, 0, result, new SyncExpectation([1], ["aac"], Frame25)));
    }

    [Fact]
    public void A_filled_hole_passes()
    {
        // 85 ms without frame at 100 s, filled with a repeated frame under the grid: no shift. The frames after it lie 5 ms off the output's grid.
        var source = Grid(0, Frame25, 100).Concat(Grid(100.085, Frame25, Duration)).ToList();
        var result = File(0, Video(Grid(0, Frame25, Duration)), Audio(1, "aac", Grid(-AacPriming, AacPacket, Duration), AacPacket, 0));
        Assert.Null(Verify(File(0, Video(source), Audio(1, "ac3", Grid(0, Ac3Packet, Duration), Ac3Packet, 0)), 0, result));
    }

    [Fact]
    public void A_long_gap_in_the_video_filled_by_repeats_passes()
    {
        var source = Grid(0, Frame25, 300).Concat(Grid(303, Frame25, Duration)).ToList();
        var result = File(0, Video(Grid(0, Frame25, Duration)), Audio(1, "aac", Grid(-AacPriming, AacPacket, Duration), AacPacket, 0));
        Assert.Null(Verify(File(0, Video(source), Audio(1, "ac3", Grid(0, Ac3Packet, Duration), Ac3Packet, 0)), 0, result));
    }

    [Fact]
    public void Variable_frame_rate_passes()
    {
        // 25 fps, then 12.5 fps: the output's grid holds every source frame, and repeats between them.
        var source = Grid(0, Frame25, 300).Concat(Grid(300, 2 * Frame25, Duration)).ToList();
        var result = File(0, Video(Grid(0, Frame25, source[^1] + 0.001)), Audio(1, "aac", Grid(-AacPriming, AacPacket, Duration), AacPacket, 0));
        Assert.Null(Verify(File(0, Video(source), Audio(1, "ac3", Grid(0, Ac3Packet, Duration), Ac3Packet, 0)), 0, result));
    }

    [Fact]
    public void A_video_shorter_than_its_audio_passes()
    {
        // The audio runs 34 s past the video, on both sides.
        var source = File(0, Video(Grid(0, Frame25, Duration - 34)), Audio(1, "ac3", Grid(0, Ac3Packet, Duration), Ac3Packet, 0));
        var result = File(0, Video(Grid(0, Frame25, Duration - 34)), Audio(1, "aac", Grid(-AacPriming, AacPacket, Duration), AacPacket, 0));
        Assert.Null(Verify(source, 0, result));
    }

    [Fact]
    public void An_audio_shorter_than_its_video_passes()
    {
        var source = File(0, Video(Grid(0, Frame25, Duration)), Audio(1, "ac3", Grid(0, Ac3Packet, Duration - 200), Ac3Packet, 0, end: Duration - 200));
        var result = File(0, Video(Grid(0, Frame25, Duration)), Audio(1, "aac", Grid(-AacPriming, AacPacket, Duration - 200), AacPacket, 0, end: Duration - 200));
        Assert.Null(Verify(source, 0, result));
    }

    [Fact]
    public void An_avi_without_presentation_times_is_read_on_its_decoding_times()
    {
        // Packets at the frame index, no pts; the decoder gives the first frame 0.08 s later (2 B-frames, measured), and a conversion follows it.
        var source = File(0, Video(Grid(0, Frame25, Duration), pts: false), Audio(1, "mp3", Grid(0, 0.024, Duration), 0.024, 0));
        var result = File(0, Video(Grid(0.08, Frame25, Duration + 0.08 - Frame25)), Audio(1, "aac", Grid(-AacPriming, AacPacket, Duration), AacPacket, 0));
        Assert.Null(Verify(source, 0.08, result));
        // That AVI's first chunk at 0 instead (today's fault): its frames are early.
        var early = File(0, Video(Grid(0, Frame25, 10).Concat(Grid(10.08, Frame25, Duration)).ToList()), Audio(1, "aac", Grid(-AacPriming, AacPacket, Duration), AacPacket, 0));
        Assert.Contains("vidéo", Verify(source, 0.08, early));
    }

    [Fact]
    public void A_hole_in_the_sound_is_passed_when_it_is_in_both()
    {
        // 2.037 s without sound at 100 s: ffmpeg keeps it, measured, to within a packet.
        var source = File(0, Video(Grid(0, Frame25, Duration)), Audio(1, "ac3", Grid(0, Ac3Packet, 100).Concat(Grid(102.037, Ac3Packet, Duration)).ToList(), Ac3Packet, 0));
        var result = File(0, Video(Grid(0, Frame25, Duration)), Audio(1, "aac", Grid(-AacPriming, AacPacket, 100).Concat(Grid(102.037 - AacPriming, AacPacket, Duration)).ToList(), AacPacket, 0));
        Assert.Null(SyncCheck.Verify(source, 0, result, Encoded, [99.5, 150]));
    }

    [Fact]
    public void Several_tracks_are_compared_through_the_maps()
    {
        var video = Video(Grid(0, Frame25, Duration));
        var source = File(0, video, Audio(1, "ac3", Grid(0, Ac3Packet, Duration), Ac3Packet, 0), new ProbedStream(2, "subtitle", "subrip", 0, 1, 0, false, []),
            Audio(3, "ac3", Grid(0.5, Ac3Packet, Duration), Ac3Packet, 0.5));
        var result = File(0, video, Audio(1, "aac", Grid(0.5 - AacPriming, AacPacket, Duration), AacPacket), Audio(2, "aac", Grid(-AacPriming, AacPacket, Duration), AacPacket, 0));
        // Emby asks for the second track first; a subtitle map counts for nothing.
        Assert.Null(Verify(source, 0, result, new SyncExpectation([3, 2, 1], ["aac", "aac"], null)));
        Assert.Contains("audio 1", Verify(source, 0, result, new SyncExpectation([1, 3], ["aac", "aac"], null)));
    }

    // ---- Desynchronized: rejected ----

    [Theory]
    [InlineData(0.004)]
    [InlineData(-0.004)]
    [InlineData(0.02)]     // half a frame at 25 fps, which the former check let through
    [InlineData(0.079)]    // video put at the file's start, the 0.7.34 fault
    public void A_video_moved_at_the_start_is_rejected(double shift)
    {
        Assert.Contains("vidéo commençant", Verify(Source(), 0, Result(shift)));
        Assert.Contains("vidéo commençant", Verify(Source(videoStart: 0.5), 0.5, Result(0.5 + shift)));
    }

    [Theory]
    [InlineData(0.0, 0.004)]
    [InlineData(0.0, -0.004)]
    [InlineData(0.0, -0.02)]    // ...which a stream start would not see: it reads 0 for all of this
    [InlineData(0.5, -0.004)]
    [InlineData(0.5, -0.02)]    // the former blind spot: a move by the priming read as the priming
    [InlineData(0.5, 0.016)]
    [InlineData(0.021, -0.015)]
    public void An_audio_moved_at_the_start_is_rejected(double sourceStart, double move) =>
        Assert.Contains("commençant", Verify(Source(audioStart: sourceStart), 0, Result(audioStart: sourceStart + move)));

    [Theory]
    [InlineData("aac", 1024)]
    [InlineData("libmp3lame", 1105)]
    [InlineData("ac3", 256)]
    [InlineData("eac3", 256)]
    [InlineData("libopus", 312)]
    [InlineData("mp2", 481)]
    public void Each_encoder_has_its_own_exact_priming(string encoder, int samples)
    {
        var video = Video(Grid(0, Frame25, Duration));
        var source = File(0, video, Audio(1, "flac", Grid(0.5, Ac3Packet, Duration), Ac3Packet));
        double first = 0.5 - samples / 48000.0;
        var at = (double t) => File(0, video, Audio(1, encoder, Grid(t, AacPacket, Duration), AacPacket));
        var expected = new SyncExpectation([1], [encoder], null);
        Assert.Null(Verify(source, 0, at(first), expected));
        Assert.NotNull(Verify(source, 0, at(first - 0.004), expected));
        Assert.NotNull(Verify(source, 0, at(first + 0.004), expected));
        // No allowance from another encoder: AAC's 21 ms early is not an AC-3's 5 ms.
        Assert.NotNull(Verify(source, 0, at(0.5 - AacPriming - 0.02), expected));
    }

    [Fact]
    public void A_copied_stream_has_no_priming()
    {
        var source = Source(audioStart: 0.5);
        var result = File(0, Video(Grid(0, Frame25, Duration)), Audio(1, "ac3", Grid(0.5 - 0.005, Ac3Packet, Duration), Ac3Packet));
        Assert.Contains("commençant", Verify(source, 0, result, Copied));
    }

    [Theory]
    [InlineData(0.2)]
    [InlineData(-0.2)]
    [InlineData(0.012)]    // under half a frame
    [InlineData(-0.012)]
    public void A_video_drifting_mid_file_is_rejected(double shift)
    {
        var result = File(0, Video(Grid(0, Frame25, 300).Concat(Grid(300 + shift, Frame25, Duration + shift)).ToList()),
            Audio(1, "aac", Grid(-AacPriming, AacPacket, Duration), AacPacket, 0));
        Assert.Contains("vidéo", SyncCheck.Verify(Source(), 0, result, Encoded, [310]));
    }

    [Fact]
    public void A_whole_frame_too_much_in_a_chunk_shows_as_a_hole_at_its_boundary_or_at_the_end()
    {
        var result = File(0, Video(Grid(0, Frame25, 300).Concat(Grid(300 + Frame25, Frame25, Duration + Frame25)).ToList()),
            Audio(1, "aac", Grid(-AacPriming, AacPacket, Duration), AacPacket, 0));
        // At its boundary: a source frame has no result frame.
        Assert.Contains("vidéo décalée", SyncCheck.Verify(Source(), 0, result, Encoded, [299.5]));
        // Elsewhere, whole frames on the same grid look alike: the last frame tells.
        Assert.Contains("dernière image", SyncCheck.Verify(Source(), 0, result, Encoded, [310]));
    }

    [Fact]
    public void A_whole_frame_too_little_overlaps_at_its_boundary()
    {
        var result = File(0, Video(Grid(0, Frame25, 300).Concat(Grid(300 - Frame25 + 0.001, Frame25, Duration)).ToList()),
            Audio(1, "aac", Grid(-AacPriming, AacPacket, Duration), AacPacket, 0));
        Assert.Contains("superposées", SyncCheck.Verify(Source(), 0, result, Encoded, [299.5]));
    }

    [Fact]
    public void A_regridded_video_moved_by_more_than_the_grid_is_rejected()
    {
        var source = File(0, Video(Grid(0, Frame2398, Duration), Frame2398), Audio(1, "ac3", Grid(0, Ac3Packet, Duration), Ac3Packet, 0));
        // 200 ms late from 300 s to the end: a hole at the boundary, and the end.
        var result = File(0, Video(Grid(0, Frame25, 300).Concat(Grid(300.2, Frame25, Duration + 0.2)).ToList()), Audio(1, "aac", Grid(-AacPriming, AacPacket, Duration), AacPacket, 0));
        var expected = new SyncExpectation([1], ["aac"], Frame25);
        Assert.Contains("vidéo décalée", SyncCheck.Verify(source, 0, result, expected, [299.5]));
        Assert.Contains("dernière image", SyncCheck.Verify(source, 0, result, expected, [310]));
    }

    [Fact]
    public void A_video_ending_early_or_late_is_rejected_on_its_own()
    {
        var audio = Audio(1, "aac", Grid(-AacPriming, AacPacket, Duration), AacPacket, 0);
        Assert.Contains("dernière image", Verify(Source(), 0, File(0, Video(Grid(0, Frame25, Duration - 2 * Frame25)), audio)));
        Assert.Contains("dernière image", Verify(Source(), 0, File(0, Video(Grid(0, Frame25, Duration + 2 * Frame25)), audio)));
    }

    [Fact]
    public void An_audio_ending_early_is_rejected_even_with_nothing_else_wrong()
    {
        var result = File(0, Video(Grid(0, Frame25, Duration)), Audio(1, "aac", Grid(-AacPriming, AacPacket, Duration - 0.2), AacPacket, 0));
        Assert.Contains("finissant", Verify(Source(), 0, result));
    }

    [Fact]
    public void An_encoded_audio_moved_by_whole_packets_to_the_end_is_seen_at_its_end()
    {
        // One AAC packet lost mid-file with its times closed up: the grid is the same, the place is not. Only the end says so.
        var result = File(0, Video(Grid(0, Frame25, Duration)), Audio(1, "aac", Grid(-AacPriming, AacPacket, Duration - AacPacket), AacPacket, 0, end: Duration - AacPacket));
        Assert.Contains("finissant", SyncCheck.Verify(Source(), 0, result, Encoded, [310]));
    }

    [Fact]
    public void An_aac_may_end_a_packet_late_but_not_two()
    {
        var video = Video(Grid(0, Frame25, Duration));
        Assert.Null(SyncCheck.Verify(Source(), 0, File(0, video, Audio(1, "aac", Grid(-AacPriming, AacPacket, Duration), AacPacket, 0, end: Duration + 0.010)), Encoded, [310]));
        Assert.Contains("finissant", SyncCheck.Verify(Source(), 0, File(0, video, Audio(1, "aac", Grid(-AacPriming, AacPacket, Duration + 2 * AacPacket), AacPacket, 0, end: Duration + 2 * AacPacket)), Encoded, [310]));
    }

    [Fact]
    public void An_encoder_padding_its_last_packet_may_end_later_but_not_earlier()
    {
        var video = Video(Grid(0, Frame25, Duration));
        var expected = new SyncExpectation([1], ["libmp3lame"], null);
        const double first = -1105.0 / 48000;
        // 17 ms past the source's end, measured.
        Assert.Null(SyncCheck.Verify(Source(), 0, File(0, video, Audio(1, "mp3", Grid(first, 0.024, Duration), 0.024, 0, end: Duration + 0.017)), expected, [310]));
        Assert.Contains("finissant", SyncCheck.Verify(Source(), 0, File(0, video, Audio(1, "mp3", Grid(first, 0.024, Duration - 0.05), 0.024, 0, end: Duration - 0.05)), expected, [310]));
    }

    [Fact]
    public void An_encoded_audio_moved_mid_file_by_a_fraction_of_a_packet_leaves_its_grid()
    {
        var result = File(0, Video(Grid(0, Frame25, Duration)),
            Audio(1, "aac", Grid(-AacPriming, AacPacket, 300).Concat(Grid(300.015, AacPacket, Duration + 0.015)).ToList(), AacPacket, 0));
        Assert.Contains("audio 1 décalé", SyncCheck.Verify(Source(), 0, result, Encoded, [310]));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(-0.5)]
    public void A_hole_in_the_sound_on_one_side_only_is_rejected(double side)
    {
        var whole = Grid(-AacPriming, AacPacket, Duration).ToList();
        var cut = Grid(-AacPriming, AacPacket, 300).Concat(Grid(300.32 - AacPriming, AacPacket, Duration)).ToList();
        var video = Video(Grid(0, Frame25, Duration));
        var wholeSource = Grid(0, Ac3Packet, Duration).ToList();
        var cutSource = Grid(0, Ac3Packet, 300).Concat(Grid(300.32, Ac3Packet, Duration)).ToList();
        var source = File(0, video, Audio(1, "ac3", side > 0 ? wholeSource : cutSource, Ac3Packet, 0));
        var result = File(0, video, Audio(1, "aac", side > 0 ? cut : whole, AacPacket, 0));
        Assert.Contains("audio 1 décalé", SyncCheck.Verify(source, 0, result, Encoded, [299.5]));
    }

    [Fact]
    public void A_copied_audio_moved_mid_file_is_rejected_even_by_whole_packets()
    {
        var packets = Grid(0, Ac3Packet, Duration).ToList();
        var moved = Grid(0, Ac3Packet, 300).Concat(Grid(Math.Ceiling(300 / Ac3Packet) * Ac3Packet + 3 * Ac3Packet, Ac3Packet, Duration)).ToList();
        var video = Video(Grid(0, Frame25, Duration));
        var source = File(0, video, Audio(1, "ac3", packets, Ac3Packet, 0));
        Assert.Contains("audio 1 décalé", SyncCheck.Verify(source, 0, File(0, video, Audio(1, "ac3", moved, Ac3Packet, 0)), Copied, [299.5]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Each_audio_stream_is_judged_on_its_own(int wrong)
    {
        var video = Video(Grid(0, Frame25, Duration));
        var source = File(0, video, Audio(1, "ac3", Grid(0, Ac3Packet, Duration), Ac3Packet, 0), Audio(2, "ac3", Grid(0, Ac3Packet, Duration), Ac3Packet, 0));
        var good = Grid(-AacPriming, AacPacket, Duration).ToList();
        var shifted = good.Select(t => t >= 300 ? t + 0.01 : t).ToList();
        var result = File(0, video, Audio(1, "aac", wrong == 0 ? shifted : good, AacPacket, 0), Audio(2, "aac", wrong == 1 ? shifted : good, AacPacket, 0));
        Assert.Contains($"audio {wrong + 1} décalé", SyncCheck.Verify(source, 0, result, new SyncExpectation([1, 2], ["aac", "aac"], null), [310]));
    }

    // ---- Whatever cannot be proved is not delivered ----

    [Fact]
    public void A_missing_value_rejects_the_file()
    {
        Assert.Contains("vérification impossible", Verify(Source() with { FileStart = null }, 0, Result()));
        Assert.Contains("vérification impossible", Verify(Source(), null, Result()));
        // No stream start in the source.
        var noStart = File(0, Video(Grid(0, Frame25, Duration)), Audio(1, "ac3", Grid(0, Ac3Packet, Duration), Ac3Packet) with { Start = null });
        Assert.Contains("vérification impossible", Verify(noStart, 0, Result()));
        // No packet read on one side or the other.
        var empty = File(0, new ProbedStream(0, "video", "h264", 0, 0, Frame25, false, []), Audio(1, "aac", Grid(0, AacPacket, Duration), AacPacket));
        Assert.Contains("vérification impossible", Verify(Source(), 0, empty));
        Assert.Contains("vérification impossible", Verify(empty, 0, Result()));
        var silent = File(0, Video(Grid(0, Frame25, Duration)), new ProbedStream(1, "audio", "aac", 48000, 0, 0, false, []));
        Assert.Contains("vérification impossible", Verify(Source(), 0, silent));
        // No video at all.
        Assert.Contains("vérification impossible", Verify(Source(), 0, File(0, Audio(1, "aac", Grid(0, AacPacket, Duration), AacPacket))));
        // A stream mapped that the source does not have, a number of streams that does not match, an unknown encoder.
        Assert.Contains("vérification impossible", Verify(Source(), 0, Result(), new SyncExpectation([7], ["aac"], null)));
        Assert.Contains("piste(s) audio", Verify(Source(), 0, Result(), new SyncExpectation([1, 1], ["aac", "aac"], null)));
        Assert.Contains("vérification impossible", Verify(Source(), 0, Result(), new SyncExpectation([1], [null], null)));
        Assert.Contains("vérification impossible", Verify(Source(), 0, Result(), new SyncExpectation([1], ["libfdk_aac"], null)));
    }

    [Fact]
    public async Task A_failed_probe_rejects_the_file()
    {
        // The shim turns the exception into "vérification impossible".
        await Assert.ThrowsAsync<InvalidOperationException>(() => SyncCheck.RunAsync(_ => throw new InvalidOperationException("ffprobe code 1"), "s", "r", Encoded, []));
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => SyncCheck.RunAsync(_ => Task.FromResult("not json"), "s", "r", Encoded, []));
        var verdict = await SyncCheck.RunAsync(_ => Task.FromResult("{}"), "s", "r", Encoded, []);
        Assert.Contains("vérification impossible", verdict.Problem);
    }

    [Fact]
    public async Task The_whole_run_reads_each_file_and_finds_a_stream_ending_early()
    {
        // ffprobe for a file whose audio stops 100 s before the end: not in the last 30 s, found in the last 10 minutes.
        static string Json(string path, bool full)
        {
            var packets = new List<string>();
            if (full)
            {
                packets.AddRange(Grid(0, Frame25, 40).Select(t => Packet(0, t)));
                packets.AddRange(Grid(0, Ac3Packet, 40).Select(t => Packet(1, t)));
                packets.AddRange(Grid(160, Frame25, Duration).Select(t => Packet(0, t)));
                packets.AddRange(Grid(160, Ac3Packet, 500).Select(t => Packet(1, t)));
            }
            return $$"""
                { "packets": [{{string.Join(",", packets)}}],
                  "streams": [ { "index": 0, "codec_type": "video", "codec_name": "h264", "r_frame_rate": "25/1", "avg_frame_rate": "25/1", "start_time": "0.000000", "height": 720 },
                               { "index": 1, "codec_type": "audio", "codec_name": "aac", "sample_rate": "48000", "start_time": "0.000000" } ],
                  "format": { "format_name": "mov,mp4", "start_time": "0.000000", "duration": "{{Duration}}" } }
                """;
            static string Packet(int stream, double t) =>
                FormattableString.Invariant($$"""{ "stream_index": {{stream}}, "pts_time": "{{t:0.000000}}", "dts_time": "{{t:0.000000}}", "duration_time": "{{(stream == 0 ? Frame25 : Ac3Packet):0.000000}}" }""");
        }
        var asked = new List<string[]>();
        var verdict = await SyncCheck.RunAsync(a =>
        {
            asked.Add(a);
            if (a.Any(x => x.StartsWith("frame=", StringComparison.Ordinal)))
                return Task.FromResult("""{ "frames": [ { "best_effort_timestamp_time": "0.000000" } ], "format": { "start_time": "0.000000" } }""");
            return Task.FromResult(Json(a[^1], a.Contains("-read_intervals") || a.Contains("-select_streams")));
        }, "s", "r", new SyncExpectation([1], ["copy"], null), []);
        Assert.Null(verdict.Problem);
        Assert.NotNull(verdict.Source);
        Assert.Equal(2, asked.Count(a => a.Any(x => x.Contains("%+#400", StringComparison.Ordinal))));
    }

    // ---- Reading ----

    [Fact]
    public void Probe_is_read_with_what_it_lacks_left_unknown()
    {
        var probe = MediaProbe.Parse("""
            { "packets": [ { "stream_index": 0, "dts_time": "0.160000", "duration_time": "0.040000" },
                           { "stream_index": 1, "pts_time": "-0.021333", "dts_time": "-0.021333", "duration_time": "0.021333" } ],
              "streams": [ { "index": 0, "codec_type": "video", "codec_name": "mpeg4", "r_frame_rate": "24000/1001", "avg_frame_rate": "25/1", "start_time": "N/A", "height": 480, "disposition": { "attached_pic": 0 } },
                           { "index": 1, "codec_type": "audio", "codec_name": "aac", "sample_rate": "48000", "start_time": "0.000000" },
                           { "index": 2, "codec_type": "video", "codec_name": "mjpeg", "r_frame_rate": "0/0", "disposition": { "attached_pic": 1 } } ],
              "format": { "format_name": "avi", "start_time": "0.021000" } }
            """);
        Assert.Equal(0.021, probe.FileStart);
        Assert.Null(probe.Duration);
        Assert.Null(probe.Video!.Start);
        Assert.Equal(1001.0 / 24000, probe.Video.FrameSeconds, 9);
        Assert.Equal(0.04, probe.Video.AverageFrameSeconds, 9);
        Assert.Null(probe.Video.Packets[0].Pts);
        Assert.Equal(0.16, probe.Video.Packets[0].Time);
        Assert.Equal(-0.021333, probe.Audio[0].Packets[0].Pts);
        Assert.Equal(0, probe.Video.Index);   // the cover art is not the video
        Assert.False(probe.IsMpegTs);
        Assert.True((probe with { Format = "mpegts" }).IsMpegTs);
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => MediaProbe.Parse("nothing"));
    }

    [Fact]
    public void A_packet_read_in_two_windows_counts_once()
    {
        var one = File(0, Video([1.0, 1.04]));
        Assert.Equal(3, one.Merge(File(0, Video([1.04, 1.08]))).Video!.Packets.Count);
    }

    [Fact]
    public void First_frame_is_counted_from_the_files_start()
    {
        Assert.Equal(0.08, MediaProbe.ParseFirstFrame("""{ "frames": [ { "best_effort_timestamp_time": "0.080000" }, { "best_effort_timestamp_time": "0.120000" } ], "format": { "start_time": "0.000000" } }"""));
        Assert.Equal(0.1, MediaProbe.ParseFirstFrame("""{ "frames": [ { "best_effort_timestamp_time": "1.500000" } ], "format": { "start_time": "1.400000" } }""")!.Value, 9);
        Assert.Null(MediaProbe.ParseFirstFrame("""{ "frames": [ { "pkt_dts_time": "1.5" } ], "format": { "start_time": "1.400000" } }"""));
        Assert.Null(MediaProbe.ParseFirstFrame("""{ "format": {} }"""));
    }

    [Fact]
    public async Task First_frame_is_looked_for_further_when_the_first_packets_decode_to_nothing()
    {
        var asked = new List<string>();
        double? first = await MediaProbe.FirstFrameAsync(a =>
        {
            string interval = a[Array.IndexOf(a, "-read_intervals") + 1];
            asked.Add(interval);
            return Task.FromResult(interval == "%+#32"
                ? """{ "frames": [], "format": { "start_time": "1.400000" } }"""
                : """{ "frames": [ { "best_effort_timestamp_time": "2.620000" } ], "format": { "start_time": "1.400000" } }""");
        }, "cut.ts");
        Assert.Equal(1.22, first!.Value, 6);
        Assert.Equal(["%+#32", "%+15"], asked);
        // Really decoded: skipping frames moves an AVI's first frame by seconds.
        Assert.DoesNotContain("-skip_frame", MediaProbe.FirstFrameArgs("a.avi"));
    }

    [Fact]
    public void Windows_straddle_the_lists_boundaries_up_to_a_limit_and_always_cover_the_quarters()
    {
        // Two hours in chunks of 30 s: every boundary, a slice with no window could be wrong by any amount.
        var many = SyncCheck.WindowTimes(7200, Grid(30, 30, 7200));
        Assert.Equal(239, many.Count);
        // Beyond the limit: spread over the file (plus the quarters, which they already cover).
        Assert.InRange(SyncCheck.WindowTimes(72000, Grid(30, 30, 72000)).Count, SyncCheck.MaxWindows, SyncCheck.MaxWindows + 3);
        Assert.True(many.Zip(many.Skip(1), (a, b) => b - a).Max() < 7200 / 8.0);
        Assert.Equal(Enumerable.Range(0, many.Count).Select(i => many[i]).OrderBy(t => t), many);
        // Each around a boundary.
        var few = SyncCheck.WindowTimes(1000, [250, 500, 750]);
        Assert.Equal([250 - SyncCheck.WindowSeconds / 2, 500 - SyncCheck.WindowSeconds / 2, 750 - SyncCheck.WindowSeconds / 2], few);
        // Without any boundary: the quarters.
        var none = SyncCheck.WindowTimes(1000, []);
        Assert.Equal(3, none.Count);
        // Too short to have windows inside: start and end are read anyway.
        Assert.Empty(SyncCheck.WindowTimes(3, [1]));
    }

    [Fact]
    public void List_boundaries_add_up_the_listed_lengths_from_the_video_offset()
    {
        const string list = "file '/s/chunk_00000.ts'\nduration 30.5\nfile '/s/chunk_00001.ts'\nduration 29.5\n";
        Assert.Equal([30.5 + 0.079, 60 + 0.079], SyncCheck.ListBoundaries(list, 0.079));
    }

    [Fact]
    public void Intervals_are_in_the_files_own_time_and_read_further_before_a_window_in_mpegts()
    {
        var mkv = new MediaProbe(0, 100, "matroska,webm", 720, []);
        Assert.Equal("%+#400,49.5%53.5,70%3700", SyncCheck.Intervals(mkv, [50]));
        var ts = new MediaProbe(1.4, 100, "mpegts", 720, []);
        Assert.Equal("%+#400,48.9%54.9,71.4%3701.4", SyncCheck.Intervals(ts, [50]));
        // A stream starting late: its start is read too, from far enough to hold its priming.
        var late = mkv with { Streams = [new ProbedStream(1, "audio", "aac", 48000, 10, 0, false, [])] };
        Assert.Contains("9.5%13", SyncCheck.Intervals(late, []));
    }
}
