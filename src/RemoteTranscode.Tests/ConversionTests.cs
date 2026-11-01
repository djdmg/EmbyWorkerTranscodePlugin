using RemoteTranscode.Common;

namespace RemoteTranscode.Tests;

public class ConversionTests
{
    private static readonly string[] Roots = ["/volume4/Film", "/volume4/Series"];

    // Shape of Emby 4.10 conversion commands (from the server logs), with video re-encoding as Emby's
    // quality profiles produce it.
    private static readonly string[] EmbyConversion =
    [
        "-loglevel", "+timing", "-y", "-print_graphs_file", "/var/packages/EmbyServer/var/logs/ffmpeg-remux-x_1graph.txt",
        "-copyts", "-start_at_zero", "-f", "matroska,webm", "-re", "-c:v:0", "hevc", "-noautorotate",
        "-i", "/volume4/Film/Bernie.mkv",
        "-map", "0:0", "-map", "0:1", "-map", "0:2", "-sn",
        "-c:v:0", "libx264", "-vf", "scale=w=1280:h=720", "-pix_fmt:v:0", "yuv420p", "-maxrate:v:0", "4000000", "-bufsize:v:0", "8000000",
        "-preset:v:0", "veryfast", "-crf:v:0", "23", "-profile:v:0", "high",
        "-c:a:0", "aac", "-ab:a:0", "384000", "-ac:a:0", "6", "-metadata:s:a:0", "language=fre", "-disposition:a:0", "default",
        "-c:a:1", "aac", "-ab:a:1", "192000", "-ac:a:1", "2",
        "-avoid_negative_ts", "disabled", "-f", "mp4", "-map_metadata", "-1", "-map_chapters", "-1", "-vtag", "hvc1",
        "/var/packages/EmbyServer/var/sync/30/17321/24db02e3.mp4",
    ];

    [Fact]
    public void Splits_an_emby_conversion_into_video_chunks_and_an_assembly()
    {
        var plan = EmbyConversionPlanner.Plan(EmbyConversion, Roots);
        Assert.True(plan.Remote, plan.Reason);
        Assert.Equal("/volume4/Film/Bernie.mkv", plan.Input);
        Assert.Equal("/var/packages/EmbyServer/var/sync/30/17321/24db02e3.mp4", plan.Output);
        Assert.Equal("libx264", plan.Requirements!.VideoEncoder);
        Assert.Equal(["scale"], plan.Requirements.Filters);

        // Worker: Emby's video settings only, full speed, chunked, valid for workers.
        string[] c = plan.ChunkTemplate;
        Assert.True(ArgTemplateValidator.Validate(c, out var error), error);
        Assert.DoesNotContain("-re", c);
        Assert.DoesNotContain("-c:a:0", c);
        Assert.DoesNotContain("-vtag", c);
        Assert.DoesNotContain("-print_graphs_file", c);
        Assert.Equal("0:0", c[Array.IndexOf(c, "-map") + 1]);
        Assert.Single(c, a => a == "-map");
        Assert.Equal("4000000", c[Array.IndexOf(c, "-maxrate:v:0") + 1]);
        Assert.Equal("scale=w=1280:h=720", c[Array.IndexOf(c, "-vf") + 1]);
        Assert.Equal("hevc", c[Array.IndexOf(c, "-c:v:0") + 1]);           // input decoder kept before -i
        Assert.Equal("libx264", c[Array.LastIndexOf(c, "-c:v:0") + 1]);
        Assert.Equal("{out}", c[^1]);

        // Server: Emby's command, video copied from the chunks, everything else untouched.
        string[] a = plan.AssemblyTemplate;
        Assert.DoesNotContain("-re", a);
        Assert.Equal(ConversionPlan.ListPlaceholder, a[Array.LastIndexOf(a, "-i") + 1]);
        Assert.Equal("concat", a[Array.LastIndexOf(a, "-i") - 3]);
        Assert.Equal("1:v:0", a[Array.IndexOf(a, "-map") + 1]);
        Assert.Contains("0:1", a);
        Assert.Contains("0:2", a);
        Assert.Equal("copy", a[Array.LastIndexOf(a, "-c:v:0") + 1]);
        Assert.DoesNotContain("-vf", a);
        Assert.DoesNotContain("-crf:v:0", a);
        Assert.DoesNotContain("-maxrate:v:0", a);
        Assert.Equal("384000", a[Array.IndexOf(a, "-ab:a:0") + 1]);
        Assert.Equal("hvc1", a[Array.IndexOf(a, "-vtag") + 1]);
        Assert.Equal("mp4", a[Array.LastIndexOf(a, "-f") + 1]);
        Assert.Equal(plan.Output, a[^1]);
    }

    [Fact]
    public void Audio_is_converted_ahead_with_embys_options_and_copied_at_assembly()
    {
        var plan = EmbyConversionPlanner.Plan(EmbyConversion, Roots);
        var ahead = plan.AudioAhead;
        Assert.True(ahead != null, plan.AudioAheadReason);
        Assert.Equal([1, 2], ahead.SourceStreams);
        Assert.True(ahead.CopyTs && ahead.StartAtZero);

        // Prepared run: Emby's input options and source, its audio maps and options, no video, MP4 shifted by 10 s.
        string[] p = ahead.PrepareTemplate;
        Assert.DoesNotContain("-re", p);
        Assert.DoesNotContain("-loglevel", p);
        Assert.DoesNotContain("-print_graphs_file", p);
        Assert.Contains("-copyts", p);
        Assert.Contains("-start_at_zero", p);
        Assert.Equal("/volume4/Film/Bernie.mkv", p[Array.IndexOf(p, "-i") + 1]);
        Assert.Equal("hevc", p[Array.IndexOf(p, "-c:v:0") + 1]);              // input decoder option, before -i
        Assert.Equal(1, p.Count(a => a == "-c:v:0"));
        Assert.Equal(["0:1", "0:2"], p.Select((a, i) => (a, i)).Where(x => x.a == "-map").Select(x => p[x.i + 1]));
        Assert.Equal("aac", p[Array.IndexOf(p, "-c:a:0") + 1]);
        Assert.Equal("384000", p[Array.IndexOf(p, "-ab:a:0") + 1]);
        Assert.Equal("6", p[Array.IndexOf(p, "-ac:a:0") + 1]);
        Assert.Equal("language=fre", p[Array.IndexOf(p, "-metadata:s:a:0") + 1]);
        Assert.Equal("default", p[Array.IndexOf(p, "-disposition:a:0") + 1]);
        foreach (string video in new[] { "-vf", "-vtag", "-crf:v:0", "-map_metadata" })
            Assert.DoesNotContain(video, p);
        Assert.Single(p, x => x == "mp4");   // the prepared file's own format, not Emby's output options
        Assert.Equal(["-avoid_negative_ts", "disabled", "-output_ts_offset", "10", "-f", "mp4", AudioAheadPlan.AudioPlaceholder], p[^7..]);

        // Assembly: the usual one, the converted audio copied from input 2, put back in place by -itsoffset.
        string[] a = ahead.AssemblyTemplate;
        int list = Array.IndexOf(a, ConversionPlan.ListPlaceholder);
        Assert.Equal(["-itsoffset", AudioAheadPlan.OffsetPlaceholder, "-i", AudioAheadPlan.AudioPlaceholder], a[(list + 1)..(list + 5)]);
        Assert.Equal(["1:v:0", "2:0", "2:1"], a.Select((x, i) => (x, i)).Where(x => x.x == "-map").Select(x => a[x.i + 1]));
        Assert.Equal("copy", a[Array.IndexOf(a, "-c:a:0") + 1]);
        Assert.Equal("copy", a[Array.IndexOf(a, "-c:a:1") + 1]);
        Assert.Equal("copy", a[Array.LastIndexOf(a, "-c:v:0") + 1]);
        Assert.Equal("384000", a[Array.IndexOf(a, "-ab:a:0") + 1]);           // same stream parameters as Emby's
        Assert.Equal("language=fre", a[Array.IndexOf(a, "-metadata:s:a:0") + 1]);
        Assert.Equal("hvc1", a[Array.IndexOf(a, "-vtag") + 1]);
        Assert.Equal("mp4", a[Array.LastIndexOf(a, "-f") + 1]);
        Assert.Equal(plan.Output, a[^1]);
        // Apart from those, the very same command as the usual assembly.
        var usual = plan.AssemblyTemplate.ToList();
        Assert.Equal(usual.Count + 4, a.Length);

        string[] filled = ahead.Assembly("/spool/x/audio.mp4", "0.478667");
        Assert.Equal("/spool/x/audio.mp4", filled[Array.IndexOf(filled, "-itsoffset") + 3]);
        Assert.Equal("0.478667", filled[Array.IndexOf(filled, "-itsoffset") + 1]);
    }

    [Fact]
    public void Copied_audio_stays_a_copy_of_the_source_and_filters_are_not_applied_twice()
    {
        string[] args = (string[])EmbyConversion.Clone();
        args[Array.IndexOf(args, "-c:a:1") + 1] = "copy";
        var list = args.ToList();
        list.InsertRange(list.IndexOf("-c:a:0"), ["-af", "volume=2"]);
        var ahead = EmbyConversionPlanner.Plan(list, Roots).AudioAhead;
        Assert.NotNull(ahead);
        Assert.Equal("volume=2", ahead.PrepareTemplate[Array.IndexOf(ahead.PrepareTemplate, "-af") + 1]);
        string[] a = ahead.AssemblyTemplate;
        Assert.Equal(["1:v:0", "2:0", "0:2"], a.Select((x, i) => (x, i)).Where(x => x.x == "-map").Select(x => a[x.i + 1]));
        Assert.DoesNotContain("-af", a);
        Assert.Equal("copy", a[Array.IndexOf(a, "-c:a:1") + 1]);
    }

    [Theory]
    [InlineData("-f", "mp4", "-f", "matroska")]                   // checked to be identical for MP4 only
    [InlineData("-c:a:0", "aac", "-c:a:0", "ac3")]                // AC-3: last frame's length lost on the way
    [InlineData("-c:a:1", "aac", "-c:a:1", "libopus")]
    [InlineData("-f", "matroska,webm", "-f", "mpegts")]           // timestamp discontinuities shared with the video
    [InlineData("-sn", null, "-shortest", null)]                  // ties the audio to the video
    [InlineData("-map", "0:1", "-map", "0:a:0")]
    public void Commands_outside_the_checked_shape_keep_the_usual_assembly(string option, string? value, string replacement, string? newValue)
    {
        var list = EmbyConversion.ToList();
        int at = value is null ? list.IndexOf(option) : Enumerable.Range(0, list.Count - 1).First(i => list[i] == option && list[i + 1] == value);
        list[at] = replacement;
        if (value != null)
            list[at + 1] = newValue!;
        var plan = EmbyConversionPlanner.Plan(list, Roots);
        Assert.True(plan.Remote, plan.Reason);   // the chunks are still encoded remotely
        Assert.Null(plan.AudioAhead);
        Assert.NotNull(plan.AudioAheadReason);
    }

    [Fact]
    public void Only_copied_audio_needs_no_conversion_ahead()
    {
        string[] args = EmbyConversion.Select(a => a == "aac" ? "copy" : a).ToArray();
        Assert.Null(EmbyConversionPlanner.Plan(args, Roots).AudioAhead);
    }

    [Theory]
    [InlineData(true, true, 10_478_667L, "0.478667")]      // -copyts -start_at_zero: offset by itsoffset - start
    [InlineData(false, false, 10_478_667L, "0.478667")]    // no -copyts: the same
    [InlineData(true, false, 10_478_667L, "-10.000000")]   // -copyts only: offset by itsoffset alone
    [InlineData(true, true, 9_990_000L, "-0.010000")]
    public void The_prepared_audio_goes_back_exactly_where_it_was(bool copyTs, bool startAtZero, long start, string expected)
    {
        var plan = new AudioAheadPlan([], [], [1], copyTs, startAtZero);
        Assert.Equal(expected, plan.AssemblyOffset(start));
    }

    [Fact]
    public void Resumed_runs_seek_to_the_last_chunk()
    {
        var plan = EmbyConversionPlanner.Plan(EmbyConversion, Roots);
        string[] run = EmbyConversionPlanner.ChunkArgs(plan.ChunkTemplate, 12, 360.04, true);
        Assert.Equal("360.039", run[Array.IndexOf(run, "-ss") + 1]);
        Assert.True(Array.IndexOf(run, "-ss") < Array.IndexOf(run, "-i"));
        Assert.Equal("12", run[Array.IndexOf(run, "-segment_start_number") + 1]);
        Assert.True(ArgTemplateValidator.Validate(run, out var error), error);
    }

    [Fact]
    public void Slice_runs_stop_at_the_slice_end()
    {
        var plan = EmbyConversionPlanner.Plan(EmbyConversion, Roots);
        string[] run = EmbyConversionPlanner.ChunkArgs(plan.ChunkTemplate, 203, 1290.5, true, 1300);
        // Just below the start frame, and a duration ending just below the next slice's first frame.
        Assert.Equal("1290.499", run[Array.IndexOf(run, "-ss") + 1]);
        Assert.Equal("9.499", run[Array.IndexOf(run, "-t") + 1]);
        Assert.True(Array.IndexOf(run, "-t") < Array.IndexOf(run, "-i"));
        Assert.Equal("203", run[Array.IndexOf(run, "-segment_start_number") + 1]);
        Assert.True(ArgTemplateValidator.Validate(run, out var error), error);
        // First slice: no seek, only the duration.
        string[] first = EmbyConversionPlanner.ChunkArgs(plan.ChunkTemplate, 0, 0, false, 600);
        Assert.Equal("599.999", first[Array.IndexOf(first, "-t") + 1]);
        Assert.DoesNotContain("-ss", first);
        Assert.True(Array.IndexOf(first, "-t") < Array.IndexOf(first, "-i"));
    }

    [Theory]
    [InlineData(120.12, "32.03")]   // 4 chunks of 30 s and 0.12 s left: 4 longer chunks instead
    [InlineData(30.12, "32.12")]    // a last slice just over one chunk: one chunk
    [InlineData(125, "30")]         // a remainder of 5 s is a normal chunk
    [InlineData(118, "30")]
    public void Runs_never_end_with_a_tiny_chunk(double length, string segmentTime)
    {
        var plan = EmbyConversionPlanner.Plan(EmbyConversion, Roots);
        string[] run = EmbyConversionPlanner.ChunkArgs(plan.ChunkTemplate, 0, 0, false, null, length);
        Assert.Equal(segmentTime, run[Array.IndexOf(run, "-segment_time") + 1]);
        Assert.True(ArgTemplateValidator.Validate(run, out var error), error);
    }

    [Fact]
    public void Video_span_check_counts_frames()
    {
        const double frame = 1 / 23.976;
        var source = new VideoSpan(0, 2643.640, frame);
        Assert.Null(SourceInfo.VerifyVideoSpan(source, new VideoSpan(0, 2643.640, frame)));
        Assert.Null(SourceInfo.VerifyVideoSpan(source, new VideoSpan(0.042, 2643.682, frame)));   // shifted, same length
        Assert.NotNull(SourceInfo.VerifyVideoSpan(source, new VideoSpan(0, 2643.640 - 3 * frame, frame)));   // 3 frames lost
        // AVI without presentation times: end known to two frames (Monk S07E06: +2.5 frames, the reference encode too).
        var avi = new VideoSpan(0, 2380.320, 0.04, Approximate: true);
        Assert.Null(SourceInfo.VerifyVideoSpan(avi, new VideoSpan(0, 2380.420, 0.04)));
        Assert.NotNull(SourceInfo.VerifyVideoSpan(avi, new VideoSpan(0, 2381.674, 0.04)));   // sliced: 34 frames too many
        Assert.NotNull(SourceInfo.VerifyVideoSpan(source, new VideoSpan(0, 2643.640 + 2 * frame, frame)));
        Assert.Null(SourceInfo.VerifyVideoSpan(source, new VideoSpan(0, double.NaN, frame)));   // end unreadable: not judged here
    }

    [Theory]
    [InlineData(5929.47, 52)]
    [InlineData(60, 1)]
    [InlineData(200, 2)]
    [InlineData(500, 6)]
    [InlineData(1800, 18)]
    [InlineData(300000, 999)]
    public void Slices_cover_the_source_without_gap(double duration, int count)
    {
        var slices = EmbyConversionPlanner.Slices(duration);
        Assert.Equal(count, slices.Count);
        Assert.Equal(0, slices[0].Start);
        Assert.Equal(duration, slices[^1].End);
        Assert.True(slices[^1].Last);
        for (int i = 1; i < slices.Count; i++)
        {
            Assert.Equal(slices[i - 1].End, slices[i].Start);
            Assert.False(slices[i - 1].Last);
        }
        // Shorter slices at the end of long sources, so the last pieces spread over the workers.
        if (duration > 600)
            Assert.Equal([60, 60, 30, 30], slices.TakeLast(4).Select(s => Math.Round(s.End - s.Start)));
        // Chunk numbers stay within a slice's range (30 s chunks).
        Assert.All(slices, s => Assert.True((s.End - s.Start) / EmbyConversionPlanner.ChunkSeconds < EmbyConversionPlanner.ChunksPerSlice));
    }

    [Fact]
    public void Remux_and_playback_stay_with_emby()
    {
        string[] remux = (string[])EmbyConversion.Clone();
        remux[Array.LastIndexOf(remux, "-c:v:0") + 1] = "copy";
        Assert.False(EmbyConversionPlanner.Plan(remux, Roots).Remote);

        string[] outside = (string[])EmbyConversion.Clone();
        outside[Array.IndexOf(outside, "-i") + 1] = "/volume4/Admin/secret.mkv";
        Assert.False(EmbyConversionPlanner.Plan(outside, Roots).Remote);
    }
}

public class VerificationTests
{
    private static readonly SourceInfo Source = new(5600, 1920, 1080, "bt709", 2, 3);

    [Theory]
    [InlineData(5600.0, 1080, null)]
    [InlineData(5601.0, 720, null)]          // scaled, within tolerance
    [InlineData(5590.0, 720, "durée")]       // 10 s missing: truncated result
    [InlineData(5600.0, 0, "vidéo")]
    public void Truncated_or_videoless_results_are_rejected(double duration, int height, string? expected)
    {
        string? error = SourceInfo.VerifyDuration(Source, new SourceInfo(duration, 1280, height, "bt709", 2, 3));
        if (expected is null)
            Assert.Null(error);
        else
            Assert.Contains(expected, error);
    }
}

public class HardwareQualityTests
{
    private static WorkerCommand Build(bool quality, string encoder)
    {
        string[] args = ["-y", "-i", "/volume4/Film/a.mkv", "-map", "0:0", "-map", "0:1", "-c:v:0", "libx264", "-crf:v:0", "22",
            "-maxrate:v:0", "8000000", "-bufsize:v:0", "16000000", "-c:a:0", "aac", "-f", "mp4", "/var/packages/EmbyServer/var/sync/1/2/x.mp4"];
        var plan = EmbyConversionPlanner.Plan(args, ["/volume4/Film"]);
        Assert.True(plan.Remote, plan.Reason);
        return WorkerCommandBuilder.Build(EmbyConversionPlanner.ChunkArgs(plan.ChunkTemplate, 0, 0, false), new WorkerCommandContext
        {
            CoordinatorBaseUrl = "https://c", JobId = "j", JobToken = "t", CaFile = null,
            Files = [new JobFile(0, JobFileKind.Input, "a.mkv", 1)], SegmentSeconds = EmbyConversionPlanner.ChunkSeconds,
            VideoEncoders = [encoder, "libx264"], QualityMode = quality, PreferHardwareEncoding = true,
        });
    }

    [Theory]
    [InlineData("h264_qsv", "-global_quality:v:0")]
    [InlineData("h264_nvenc", "-cq:v:0")]
    [InlineData("h264_amf", "-qvbr_quality_level:v:0")]
    public void Conversions_use_constant_quality_on_gpu(string encoder, string qualityOption)
    {
        var cmd = Build(quality: true, encoder);
        Assert.Equal("22", cmd.Args[Array.IndexOf(cmd.Args, qualityOption) + 1]);   // Emby's CRF
        Assert.Contains("-maxrate:v:0", cmd.Args);                                      // Emby's cap kept
        string? average = Array.IndexOf(cmd.Args, "-b:v:0") is int b and >= 0 ? cmd.Args[b + 1] : null;
        // NVENC: constant quality with -b 0 (no average target); QSV: an average next to the cap selects QVBR (with
        // -maxrate alone it falls back to constant QP); AMF: QVBR from its quality level.
        Assert.Equal(encoder switch { "h264_nvenc" => "0", "h264_qsv" => "6400000", _ => null }, average);
    }

    [Fact]
    public void Playback_keeps_a_bitrate_target()
    {
        var cmd = Build(quality: false, "h264_qsv");
        Assert.Contains("-b:v:0", cmd.Args);
        Assert.DoesNotContain("-global_quality:v:0", cmd.Args);
    }
}
