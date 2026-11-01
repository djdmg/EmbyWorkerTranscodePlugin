using RemoteTranscode.Common;

namespace RemoteTranscode.Tests;

public class PlannerTests
{
    // Real command captured from Emby 4.10.2 (seek to 10:00 on "Tanguy, le retour").
    private static readonly string[] EmbySeekCommand =
    [
        "-loglevel", "+timing", "-y", "-print_graphs_file", "/var/packages/EmbyServer/var/logs/ffmpeg-transcode-d1884bb8_1graph.txt",
        "-copyts", "-start_at_zero", "-f", "mp4", "-ss", "00:10:00.000", "-c:v:0", "h264", "-noautorotate",
        "-i", "/volume4/Film/Tanguy, le retour.mp4",
        "-filter_complex", "[0:0]scale@f1=width=720:height=388,subtitles@f2=filename='/volume4/Film/Tanguy, le retour.fre.forced.vtt':fontsdir='/var/packages/EmbyServer/var/fonts':charenc=utf-8[f2_out0]",
        "-map", "[f2_out0]", "-map", "0:1", "-sn", "-c:v:0", "libx264", "-g:v:0", "72", "-maxrate:v:0", "1244000",
        "-bufsize:v:0", "2488000", "-sc_threshold:v:0", "0", "-keyint_min:v:0", "72", "-r:v:0", "24", "-pix_fmt:v:0", "yuv420p",
        "-preset:v:0", "veryfast", "-profile:v:0", "main", "-x264opts:v:0", "subme=0:me_range=4:rc_lookahead=10:partitions=none",
        "-crf:v:0", "23", "-c:a:0", "aac", "-ab:a:0", "192000", "-ac:a:0", "2", "-metadata:s:a:0", "language=fre",
        "-filter:a:0", "volume=2", "-disposition:a:0", "default", "-max_delay", "5000000", "-avoid_negative_ts", "disabled",
        "-f", "segment", "-map_metadata", "-1", "-map_chapters", "-1", "-segment_format", "mpegts",
        "-segment_list", "/var/packages/EmbyServer/var/transcoding-temp/73B033/73B033.m3u8", "-segment_list_type", "m3u8",
        "-segment_time", "00:00:03.000", "-segment_start_number", "200", "-individual_header_trailer", "0",
        "-write_header_trailer", "0", "-segment_write_temp", "1", "/var/packages/EmbyServer/var/transcoding-temp/73B033/73B033_%d.ts",
    ];

    private static PlannerOptions Options() => new()
    {
        AllowedRoots = ["/volume4/Film", "/var/packages/EmbyServer/var/fonts"],
        OutputRoots = ["/var/packages/EmbyServer/var/transcoding-temp"],
        GetFileSize = _ => 1000,
        ListDirectory = _ => ["/var/packages/EmbyServer/var/fonts/GoNotoKurrent.ttf"],
    };

    // Real command captured from Emby 4.10.2 (Superman, 720p, MKV with text subtitles): the video output, then one
    // WebVTT segment output per text subtitle track.
    private static readonly string[] EmbyCommandWithSubtitles =
    [
        "-loglevel", "+timing", "-y", "-print_graphs_file", "/var/packages/EmbyServer/var/logs/ffmpeg-transcode-dcefc985_1graph.txt",
        "-copyts", "-start_at_zero", "-f", "matroska,webm", "-ss", "00:30:24.000", "-c:v:0", "hevc", "-noautorotate",
        "-i", "/volume4/Film/Superman (2025).mkv",
        "-filter_complex", "[0:0]scale@f1=width=720:height=380,setparams@f2=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,format@f3=pix_fmts=p010,supertonemap@f4=tonemap=hable,setparams@f5=color_primaries=bt709:color_trc=bt709:colorspace=bt709,format@f6=pix_fmts=yuv420p[f6_out0]",
        "-map", "[f6_out0]", "-map", "0:1", "-sn", "-c:v:0", "libx265", "-b:v:0", "1616000", "-g:v:0", "72",
        "-maxrate:v:0", "1616000", "-bufsize:v:0", "3232000", "-keyint_min:v:0", "72", "-r:v:0", "23.976024627685547",
        "-preset:v:0", "veryfast", "-profile:v:0", "main", "-c:a:0", "libmp3lame", "-ab:a:0", "192000", "-ac:a:0", "2",
        "-metadata:s:a:0", "language=fre", "-filter:a:0", "volume=2", "-disposition:a:0", "default", "-max_delay", "5000000",
        "-avoid_negative_ts", "disabled", "-f", "segment", "-map_metadata", "-1", "-map_chapters", "-1", "-segment_format", "mpegts",
        "-segment_list", "/var/packages/EmbyServer/var/transcoding-temp/5B5716/5B5716.m3u8", "-segment_list_type", "m3u8",
        "-segment_time", "00:00:03.000", "-segment_start_number", "608", "-individual_header_trailer", "0",
        "-write_header_trailer", "0", "-segment_write_temp", "1", "/var/packages/EmbyServer/var/transcoding-temp/5B5716/5B5716_%d.ts",
        "-map", "0:3", "-map", "0:0", "-an", "-c:v:0", "copy", "-c:s:0", "webvtt", "-max_delay", "5000000", "-avoid_negative_ts", "disabled",
        "-f", "segment", "-map_metadata", "-1", "-segment_format", "webvtt",
        "-segment_list", "/var/packages/EmbyServer/var/transcoding-temp/5B5716/5B5716_s3.m3u8", "-segment_list_type", "m3u8",
        "-segment_time", "00:00:03.000", "-segment_start_number", "608", "-break_non_keyframes", "1", "-individual_header_trailer", "1",
        "-write_header_trailer", "0", "-write_empty_segments", "1", "-segment_write_temp", "1", "-min_frame_time", "00:30:24.000",
        "/var/packages/EmbyServer/var/transcoding-temp/5B5716/5B5716_s3_%d.vtt",
        "-map", "0:4", "-map", "0:0", "-an", "-c:v:0", "copy", "-c:s:0", "webvtt", "-max_delay", "5000000", "-avoid_negative_ts", "disabled",
        "-f", "segment", "-map_metadata", "-1", "-segment_format", "webvtt",
        "-segment_list", "/var/packages/EmbyServer/var/transcoding-temp/5B5716/5B5716_s4.m3u8", "-segment_list_type", "m3u8",
        "-segment_time", "00:00:03.000", "-segment_start_number", "608", "-break_non_keyframes", "1", "-individual_header_trailer", "1",
        "-write_header_trailer", "0", "-write_empty_segments", "1", "-segment_write_temp", "1", "-min_frame_time", "00:30:24.000",
        "/var/packages/EmbyServer/var/transcoding-temp/5B5716/5B5716_s4_%d.vtt",
    ];

    [Fact]
    public void Subtitle_outputs_are_split_from_the_video_output()
    {
        var (main, subtitles) = EmbyJobPlanner.SplitSubtitleOutputs(EmbyCommandWithSubtitles);

        // The video run: inputs and the first output only, ending with its segment pattern.
        Assert.Equal("/var/packages/EmbyServer/var/transcoding-temp/5B5716/5B5716_%d.ts", main[^1]);
        Assert.DoesNotContain(main, a => a.EndsWith(".vtt", StringComparison.Ordinal));
        var plan = EmbyJobPlanner.Plan(main, Options());
        Assert.True(plan.Remote, plan.Reason);
        Assert.Equal("libx265", plan.Requirements!.VideoEncoder);
        Assert.Equal(608, plan.StartIndex);

        // The subtitle run: same inputs (same seek), both WebVTT outputs, no video encode, no graph file.
        Assert.NotNull(subtitles);
        Assert.Equal("00:30:24.000", EmbyJobPlanner.ValueOf(subtitles, "-ss"));
        Assert.Equal(2, subtitles.Count(a => a.EndsWith("_%d.vtt", StringComparison.Ordinal)));
        Assert.DoesNotContain("libx265", subtitles);
        Assert.DoesNotContain("-print_graphs_file", subtitles);
        Assert.Contains("-print_graphs_file", main);
    }

    [Fact]
    public void Whole_command_with_subtitle_outputs_stays_local()
    {
        // Never planned as is: it would run the subtitle outputs on the worker, where nobody reads them.
        var plan = EmbyJobPlanner.Plan(EmbyCommandWithSubtitles, Options());
        Assert.False(plan.Remote);
    }

    [Fact]
    public void Single_output_command_is_not_split()
    {
        var (main, subtitles) = EmbyJobPlanner.SplitSubtitleOutputs(EmbySeekCommand);
        Assert.Null(subtitles);
        Assert.Equal(EmbySeekCommand, main);
    }

    [Fact]
    public void Plans_real_emby_seek_command()
    {
        var plan = EmbyJobPlanner.Plan(EmbySeekCommand, Options());

        Assert.True(plan.Remote, plan.Reason);
        Assert.Equal(3, plan.SegmentSeconds);
        Assert.Equal(200, plan.StartIndex);
        Assert.Equal("/var/packages/EmbyServer/var/transcoding-temp/73B033/73B033_%d.ts", plan.OutputPattern);
        Assert.Collection(plan.Files,
            f => Assert.Equal((JobFileKind.Input, "/volume4/Film/Tanguy, le retour.mp4"), (f.Kind, f.ServerPath)),
            f => Assert.Equal((JobFileKind.Aux, "/volume4/Film/Tanguy, le retour.fre.forced.vtt"), (f.Kind, f.ServerPath)),
            f => Assert.Equal(JobFileKind.Font, f.Kind));
        Assert.DoesNotContain("-segment_write_temp", plan.Template);
        Assert.DoesNotContain("-print_graphs_file", plan.Template);
        Assert.DoesNotContain("+timing", plan.Template);
        Assert.Contains("[0:0]scale@f1=width=720:height=388,subtitles@f2=filename='{aux:1}':fontsdir='{fonts}':charenc=utf-8[f2_out0]", plan.Template);
        Assert.Equal("{out}", plan.Template[^1]);
        Assert.True(ArgTemplateValidator.Validate(plan.Template, out var error), error);
    }

    [Fact]
    public void Remux_stays_local()
    {
        string[] remux = ["-y", "-i", "/volume4/Film/a.mkv", "-c:v:0", "copy", "-c:a:0", "copy", "-f", "segment",
            "-segment_list", "/var/packages/EmbyServer/var/transcoding-temp/A/A.m3u8", "/var/packages/EmbyServer/var/transcoding-temp/A/A_%d.ts"];
        Assert.False(EmbyJobPlanner.Plan(remux, Options()).Remote);
    }

    [Fact]
    public void Inputs_outside_allowed_roots_stay_local()
    {
        string[] cmd = (string[])EmbySeekCommand.Clone();
        cmd[Array.IndexOf(cmd, "-i") + 1] = "/volume4/Film/../Admin/secret.mkv";
        var plan = EmbyJobPlanner.Plan(cmd, Options());
        Assert.False(plan.Remote);
        Assert.Equal("input outside allowed roots", plan.Reason);
    }

    [Theory]
    [InlineData("filename='/etc/passwd'")]
    [InlineData("filename='{aux:0}',movie=/etc/passwd")]
    public void Validator_rejects_file_access_in_filters(string filter)
    {
        string[] template = ["-i", "{input:0}", "-filter_complex", $"[0:0]subtitles@f1={filter}[o]", "-f", "segment", "{out}"];
        Assert.False(ArgTemplateValidator.Validate(template, out _));
    }

    [Theory]
    [InlineData("-dump_attachment:t", "C:/x")]
    [InlineData("-segment_list", "C:/Windows/evil")]
    [InlineData("-metadata", "file:/etc/passwd")]
    public void Validator_rejects_unknown_options_and_paths(string option, string value)
    {
        string[] template = ["-i", "{input:0}", option, value, "{out}"];
        Assert.False(ArgTemplateValidator.Validate(template, out _));
    }

    [Fact]
    public void Builder_swaps_x264_for_validated_hardware_encoder()
    {
        var plan = EmbyJobPlanner.Plan(EmbySeekCommand, Options());
        var files = plan.Files.Select(f => new JobFile(f.Id, f.Kind, f.Name, f.Size)).ToArray();
        var cmd = WorkerCommandBuilder.Build(plan.Template, new WorkerCommandContext
        {
            CoordinatorBaseUrl = "https://nas:8097",
            JobId = "j1",
            JobToken = "tok",
            CaFile = "ca.pem",
            Files = files,
            SegmentSeconds = plan.SegmentSeconds,
            VideoEncoders = ["libx264", "h264_qsv"],
            PreferHardwareEncoding = true,
        });

        Assert.Equal("h264_qsv", cmd.VideoEncoder);
        Assert.DoesNotContain("-x264opts:v:0", cmd.Args);
        Assert.DoesNotContain("-crf:v:0", cmd.Args);
        Assert.Contains("https://nas:8097/jobs/j1/files/0", cmd.Args);
        Assert.Contains("[0:0]scale@f1=width=720:height=388,subtitles@f2=filename='aux/1.vtt':fontsdir='fonts':charenc=utf-8[f2_out0]", cmd.Args);
        Assert.Equal("seg_%d.ts", cmd.Args[^1]);
        Assert.Equal("csv", cmd.Args[Array.IndexOf(cmd.Args, "-segment_list_type") + 1]);
        Assert.Equal("995200", cmd.Args[Array.IndexOf(cmd.Args, "-b:v:0") + 1]);
        // Encoder options must follow the output codec option, never the input decoder one before "-i".
        Assert.True(Array.IndexOf(cmd.Args, "-force_key_frames:v:0") > Array.LastIndexOf(cmd.Args, "-i"));
    }

    [Fact]
    public void Filter_escaping_round_trips_apostrophes_and_colons()
    {
        string path = "/volume4/Film/L'arnaque: le retour.srt";
        Assert.Equal(path, FilterEscaping.Unescape(FilterEscaping.Escape(path)));
        var m = FilterEscaping.QuotedFileOption().Match($"subtitles=filename='{FilterEscaping.Escape(path)}':charenc=utf-8");
        Assert.Equal(path, FilterEscaping.Unescape(m.Groups["value"].Value));
    }
}

public class ReverseProxyTests
{
    [Fact]
    public void Behind_a_public_proxy_ffmpeg_uses_the_system_trust_store()
    {
        string[] template = ["-i", "{input:0}", "-c:v:0", "libx264", "-f", "segment", "-segment_list", "{list}", "{out}"];
        var cmd = WorkerCommandBuilder.Build(template, new WorkerCommandContext
        {
            CoordinatorBaseUrl = "https://coor.example.org",
            JobId = "j1",
            JobToken = "tok",
            CaFile = null,
            Files = [new JobFile(0, JobFileKind.Input, "a.mkv", 1)],
            SegmentSeconds = 3,
            VideoEncoders = ["libx264"],
        });
        Assert.DoesNotContain("-ca_file", cmd.Args);
        Assert.Equal("1", cmd.Args[Array.IndexOf(cmd.Args, "-tls_verify") + 1]);
        Assert.Contains("https://coor.example.org/jobs/j1/files/0", cmd.Args);
    }
}

public class SameEncoderTests
{
    [Fact]
    public void By_default_the_worker_runs_embys_own_encoder_and_settings()
    {
        string[] template = ["-i", "{input:0}", "-c:v:0", "libx265", "-preset:v:0", "veryfast", "-crf:v:0", "23", "-maxrate:v:0", "292000",
            "-f", "segment", "-segment_list", "{list}", "{out}"];
        var cmd = WorkerCommandBuilder.Build(template, new WorkerCommandContext
        {
            CoordinatorBaseUrl = "https://c", JobId = "j", JobToken = "t", CaFile = null,
            Files = [new JobFile(0, JobFileKind.Input, "a.mkv", 1)], SegmentSeconds = 3,
            VideoEncoders = ["hevc_qsv", "hevc_nvenc", "libx265"],
        });
        Assert.Equal("libx265", cmd.VideoEncoder);
        Assert.Equal("veryfast", cmd.Args[Array.IndexOf(cmd.Args, "-preset:v:0") + 1]);
        Assert.Equal("23", cmd.Args[Array.IndexOf(cmd.Args, "-crf:v:0") + 1]);
        Assert.DoesNotContain("-force_key_frames:v:0", cmd.Args);   // no hardware-specific additions
    }
}
