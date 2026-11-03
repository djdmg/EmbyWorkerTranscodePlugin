using System.Globalization;
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
        Assert.Equal(["-avoid_negative_ts", "disabled", "-output_ts_offset", "10", "-movie_timescale", AudioAheadPlan.MovieTimescale, "-f", "mp4", AudioAheadPlan.AudioPlaceholder], p[^9..]);

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

    [Theory]
    [InlineData(1001.0 / 24000, 0.020854)]   // 23.976 fps: halfway between two frames
    [InlineData(0.04, 0.02)]
    [InlineData(1.0 / 60, 0.008333)]
    [InlineData(1.0, EmbyConversionPlanner.MaxSeekMargin)]   // slow video: capped
    [InlineData(0.0005, EmbyConversionPlanner.FrameMargin)]  // frames almost together: never under the frame margin
    public void Seek_margin_is_half_the_smallest_frame_gap(double gap, double expected) =>
        Assert.Equal(expected, EmbyConversionPlanner.SeekMargin(gap), 6);

    [Fact]
    public void Seek_margin_without_a_gap_is_the_frame_margin()
    {
        Assert.Equal(EmbyConversionPlanner.FrameMargin, EmbyConversionPlanner.SeekMargin(null));
        Assert.Equal(EmbyConversionPlanner.FrameMargin, EmbyConversionPlanner.SeekMargin(0));
    }

    // Issue #3: a boundary at 483.363 for Emby's ffmpeg, whose file starts at 0, is 483.358 for the
    // workers' ffmpeg, whose file starts at 0.005. A 1 ms margin aims after that frame: the run would skip it.
    [Fact]
    public void A_run_aims_between_two_frames_so_builds_that_disagree_on_the_file_start_keep_the_same_frames()
    {
        const double frame = 1001.0 / 24000, start = 483.363, end = 604.191, workerFileStart = 0.005;
        double margin = EmbyConversionPlanner.SeekMargin(frame);
        string[] run = EmbyConversionPlanner.ChunkArgs(["-copyts", "-i", "{input:0}", "seg_%d.ts"], 100, start, true, end, null, margin);
        double ss = double.Parse(run[Array.IndexOf(run, "-ss") + 1], CultureInfo.InvariantCulture);
        double t = double.Parse(run[Array.IndexOf(run, "-t") + 1], CultureInfo.InvariantCulture);
        foreach (double fileStart in new[] { 0, workerFileStart })
        {
            double threshold = ss + fileStart;   // ffmpeg counts -ss from the file's start
            Assert.True(threshold <= start && threshold > start - frame, $"first frame kept, file start {fileStart}");
            Assert.True(start + t < end && start + t > end - frame, $"last frame kept, file start {fileStart}");
        }
        double old = start - EmbyConversionPlanner.FrameMargin + workerFileStart;
        Assert.True(old > start, "the 1 ms margin skipped the slice's first frame");
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

    [Theory]
    [InlineData(0.079, 0.0, 0.079, 0.0167, true)]    // put back in place
    [InlineData(0.079, 0.0, 0.0, 0.0167, false)]     // moved to 0: ahead of its audio
    [InlineData(0.083, 0.083, 0.0, 0.04, true)]      // the whole file starts late: everything moves together
    [InlineData(0.021, 0.0, 0.021, 0.04, true)]
    [InlineData(0.021, 0.0, 0.0, 0.04, false)]
    [InlineData(0.0, 0.0, 0.004, 0.0167, true)]      // within the tolerance
    public void Video_start_check_sees_a_video_out_of_step(double sourceStart, double fileStart, double resultStart, double frame, bool passes)
    {
        var source = new VideoSpan(sourceStart, 100, frame);
        var result = new VideoSpan(resultStart, 100, frame);
        Assert.Equal(passes, SourceInfo.VerifyVideoStart(source, fileStart, result) is null);
    }

    [Fact]
    public void Video_start_check_is_skipped_without_exact_times()
    {
        Assert.Null(SourceInfo.VerifyVideoStart(new VideoSpan(0.5, 100, 0.04, Approximate: true), 0, new VideoSpan(0, 100, 0.04)));
        Assert.Null(SourceInfo.VerifyVideoStart(new VideoSpan(0.5, 100, 0.04), null, new VideoSpan(0, 100, 0.04)));
    }

    private static StreamStarts Source(double? fileStart, double? audioStart) =>
        new(fileStart, [new(0, "video", "h264", 0, 0.079), new(1, "audio", "ac3", 48000, audioStart)]);

    private static StreamStarts Result(double? audioStart, string codec = "aac", int rate = 48000) =>
        new(0, [new(0, "video", "h264", 0, 0.079), new(1, "audio", codec, rate, audioStart)]);

    [Theory]
    [InlineData(0.0, 0.0, 0.0, true)]              // AAC in MP4: first packet at -1024 samples, the edit list hides it
    [InlineData(0.0, 0.0, 0.014, true)]
    [InlineData(0.0, 0.0, 0.016, false)]
    [InlineData(0.0, 0.0, -0.016, false)]
    [InlineData(0.021, 0.021, 0.0, true)]          // the whole file starts late: everything moves together
    [InlineData(0.021, 0.0, 0.021, true)]          // audio after the file's start stays so
    [InlineData(0.021, 0.0, 0.0, true)]            // ...within the priming: a stream starting later reads its first packet
    [InlineData(0.5, 0.0, 0.478667, true)]         // measured: AAC at 48 kHz, sound at 0.5 s
    [InlineData(0.5, 0.0, 0.5, true)]
    [InlineData(0.5, 0.0, 0.46, false)]            // more than the priming and the tolerance
    [InlineData(0.5, 0.0, 0.516, false)]
    [InlineData(0.5, 0.0, 0.0, false)]             // moved to 0
    public void Audio_start_check_sees_an_audio_out_of_step(double sourceStart, double fileStart, double resultStart, bool passes) =>
        Assert.Equal(passes, SourceInfo.VerifyAudioStarts(Source(fileStart, sourceStart), [1], Result(resultStart)) is null);

    [Fact]
    public void Audio_start_check_allows_the_priming_of_the_results_codec_only()
    {
        // 1105 samples of MP3 at 48 kHz, measured: 0.476979 for 0.5 s.
        Assert.Null(SourceInfo.VerifyAudioStarts(Source(0, 0.5), [1], Result(0.476979, "mp3")));
        // AAC at 44.1 kHz: 23.2 ms.
        Assert.Null(SourceInfo.VerifyAudioStarts(Source(0, 0.5), [1], Result(0.4768, "aac", 44100)));
        // A copied AC-3 has no priming allowed: 25 ms early is a move; 5 ms (its first packet, measured) is not.
        Assert.NotNull(SourceInfo.VerifyAudioStarts(Source(0, 0.5), [1], Result(0.475, "ac3")));
        Assert.Null(SourceInfo.VerifyAudioStarts(Source(0, 0.5), [1], Result(0.495, "ac3")));
    }

    [Fact]
    public void Audio_start_check_goes_through_the_maps()
    {
        var source = new StreamStarts(0, [
            new(0, "video", "h264", 0, 0), new(1, "audio", "ac3", 48000, 0), new(2, "subtitle", "subrip", 0, 1), new(3, "audio", "ac3", 48000, 0.5)]);
        var inOrder = new StreamStarts(0, [new(0, "video", "h264", 0, 0), new(1, "audio", "aac", 48000, 0.5), new(2, "audio", "aac", 48000, 0), new(3, "subtitle", "mov_text", 0, 1)]);
        // Emby asks for the second track first; the subtitle's map counts for nothing.
        Assert.Null(SourceInfo.VerifyAudioStarts(source, [3, 2, 1], inOrder));
        string? problem = SourceInfo.VerifyAudioStarts(source, [1, 3], inOrder);
        Assert.Contains("audio 1", problem);
    }

    [Fact]
    public void Audio_start_check_is_skipped_without_values()
    {
        Assert.Null(SourceInfo.VerifyAudioStarts(Source(null, 0.5), [1], Result(0)));
        Assert.Null(SourceInfo.VerifyAudioStarts(Source(0, null), [1], Result(0)));
        Assert.Null(SourceInfo.VerifyAudioStarts(Source(0, 0.5), [1], Result(null)));
        // Streams that do not correspond: another check's business.
        Assert.Null(SourceInfo.VerifyAudioStarts(Source(0, 0.5), [1, 1], Result(0)));
        Assert.Null(SourceInfo.VerifyAudioStarts(Source(0, 0.5), [], Result(0)));
    }

    [Fact]
    public void Stream_starts_are_read_from_ffprobe_and_what_it_lacks_stays_unknown()
    {
        var starts = StreamStarts.Parse("""
            {"streams":[{"index":0,"codec_name":"h264","codec_type":"video","start_time":"0.079000"},
                        {"index":1,"codec_name":"aac","codec_type":"audio","sample_rate":"48000","start_time":"0.000000"},
                        {"index":2,"codec_name":"ac3","codec_type":"audio","sample_rate":"44100","start_time":"N/A"},
                        {"index":3,"codec_type":"audio"}],
             "format":{"start_time":"0.021000"}}
            """);
        Assert.Equal(0.021, starts.FileStart);
        Assert.Equal(0.079, starts.Streams[0].Start);
        Assert.Equal(1024.0 / 48000, starts.Streams[1].Priming);
        Assert.Null(starts.Streams[2].Start);
        Assert.Null(starts.Streams[3].Start);
        Assert.Null(StreamStarts.Parse("""{"format":{}}""").FileStart);
    }

    private static readonly string[] AudioAssembly =
        ["-y", "-copyts", "-start_at_zero", "-f", "matroska,webm", "-c:v:0", "h264", "-i", "/m/a.mkv", "-f", "concat", "-safe", "0", "-i", "{chunklist}",
         "-map", "1:v:0", "-map", "0:1", "-map", "0:3", "-sn", "-c:v:0", "copy", "-c:a:0", "aac", "-af", "volume=2", "-avoid_negative_ts", "disabled", "-f", "mp4", "/o/a.mp4"];

    [Fact]
    public void Audio_maps_of_an_assembly_leave_the_chunks_out()
    {
        Assert.Equal([1, 3], EmbyConversionPlanner.AssemblySourceMaps(AudioAssembly));
        Assert.Null(EmbyConversionPlanner.AssemblySourceMaps(["-map", "1:v:0", "-map", "0:a:0", "/o/a.mp4"]));
    }

    [Fact]
    public void Audio_start_is_compared_on_a_usual_assembly_and_on_embys_shape_of_command()
    {
        Assert.Null(EmbyConversionPlanner.WhyAudioStartIsNotCompared(AudioAssembly));
        var plan = EmbyConversionPlanner.Plan(EmbyConversion, Roots);
        Assert.Null(EmbyConversionPlanner.WhyAudioStartIsNotCompared(plan.AssemblyTemplate));
        Assert.Equal([1, 2], EmbyConversionPlanner.AssemblySourceMaps(plan.AssemblyTemplate));
    }

    [Theory]
    [InlineData("-async", "1")]
    [InlineData("-ss", "10")]
    [InlineData("-itsoffset", "0.5")]
    [InlineData("-output_ts_offset", "1")]
    [InlineData("-af", "aresample=async=1:first_pts=0")]
    [InlineData("-filter:a:0", "adelay=500")]
    [InlineData("-avoid_negative_ts", "make_zero")]
    public void Audio_start_is_not_compared_when_emby_asks_to_move_the_audio(string option, string value)
    {
        var assembly = AudioAssembly.ToList();
        assembly.InsertRange(assembly.Count - 1, [option, value]);
        Assert.NotNull(EmbyConversionPlanner.WhyAudioStartIsNotCompared(assembly));
    }

    [Fact]
    public void Audio_start_is_compared_with_shortest_which_only_cuts_the_end()
    {
        var assembly = AudioAssembly.ToList();
        assembly.Insert(assembly.Count - 1, "-shortest");
        Assert.Null(EmbyConversionPlanner.WhyAudioStartIsNotCompared(assembly));
    }

    [Theory]
    [InlineData("matroska,webm", "mpegts")]     // an MPEG-TS result starts at 1.4 s
    [InlineData("matroska,webm", "avi")]
    [InlineData("mpegts", "mp4")]               // a stream's first packets may not decode
    [InlineData("avi", "mp4")]                  // approximate times
    public void Audio_start_is_not_compared_on_containers_not_measured(string input, string output)
    {
        var assembly = AudioAssembly.ToArray();
        assembly[4] = input;
        assembly[^2] = output;
        Assert.NotNull(EmbyConversionPlanner.WhyAudioStartIsNotCompared(assembly));
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
