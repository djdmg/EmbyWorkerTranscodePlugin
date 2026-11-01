using RemoteTranscode.Common;

namespace RemoteTranscode.Tests;

public class HardwareDecodingTests
{
    // Worker side of an Emby 4.10 HDR conversion slice: Emby names the decoder before its input, seeks frame-exactly.
    private static readonly string[] HdrConversion =
    [
        "-y", "-copyts", "-start_at_zero", "-f", "matroska,webm", "-c:v:0", "hevc", "-noautorotate", "-ss", "105.039", "-t", "105.02",
        "-i", "{input:0}", "-map", "[v]", "-filter_complex", "[0:0]format=p010,supertonemap=tonemap=hable,format=yuv420p[v]",
        "-c:v:0", "libx264", "-crf:v:0", "22", "-maxrate:v:0", "8000000", "-f", "segment", "-segment_list", "{list}", "{out}",
    ];

    private static WorkerCommand Build(string[] template, string[] encoders, string[] decoders, bool preferHardware = true,
        IReadOnlyDictionary<int, string>? localInputs = null) =>
        WorkerCommandBuilder.Build(template, new WorkerCommandContext
        {
            CoordinatorBaseUrl = "https://c", JobId = "j", JobToken = "t", CaFile = null,
            Files = [new JobFile(0, JobFileKind.Input, "a.mkv", 1), new JobFile(1, JobFileKind.Input, "b.mka", 1)],
            SegmentSeconds = 6, VideoEncoders = encoders, PreferHardwareEncoding = preferHardware, QualityMode = true,
            HardwareDecoders = decoders, LocalInputs = localInputs,
        });

    private static int Count(string[] args, string value) => args.Count(a => a == value);

    [Fact]
    public void Nvenc_decodes_with_cuda_next_to_the_video_input()
    {
        var cmd = Build(HdrConversion, ["h264_nvenc", "libx264"], ["nvenc/hevc", "nvenc/h264"]);

        Assert.Equal("h264_nvenc", cmd.VideoEncoder);
        Assert.Equal("nvenc/hevc", cmd.HardwareDecoder);
        int hw = Array.IndexOf(cmd.Args, "-hwaccel");
        Assert.Equal("cuda", cmd.Args[hw + 1]);
        Assert.Equal(1, Count(cmd.Args, "-hwaccel"));
        // Before the input, after Emby's own input options, which keep their values (frame-exact slice boundaries).
        Assert.True(hw < Array.IndexOf(cmd.Args, "-i"));
        Assert.True(hw > Array.IndexOf(cmd.Args, "-ss"));
        Assert.Equal("105.039", cmd.Args[Array.IndexOf(cmd.Args, "-ss") + 1]);
        Assert.Equal("105.02", cmd.Args[Array.IndexOf(cmd.Args, "-t") + 1]);
        Assert.Equal("hevc", cmd.Args[Array.IndexOf(cmd.Args, "-c:v:0") + 1]);   // Emby's decoder kept
        Assert.Contains("-copyts", cmd.Args);
        Assert.Contains("-start_at_zero", cmd.Args);
        Assert.Contains("-noautorotate", cmd.Args);
        // Frames come back to system memory: the software filter graph is unchanged.
        Assert.DoesNotContain("-hwaccel_output_format", cmd.Args);
        Assert.Contains(cmd.Args, a => a.StartsWith("[0:0]format=p010,zscale@", StringComparison.Ordinal));
    }

    [Fact]
    public void Qsv_decodes_with_d3d11va_on_the_intel_adapter()
    {
        var cmd = Build(HdrConversion, ["hevc_qsv", "h264_qsv"], ["qsv/hevc"]);

        Assert.Equal("h264_qsv", cmd.VideoEncoder);
        int init = Array.IndexOf(cmd.Args, "-init_hw_device");
        Assert.Equal("d3d11va=hwdec:,vendor_id=0x8086", cmd.Args[init + 1]);
        Assert.Equal("d3d11va", cmd.Args[Array.IndexOf(cmd.Args, "-hwaccel") + 1]);
        Assert.Equal("hwdec", cmd.Args[Array.IndexOf(cmd.Args, "-hwaccel_device") + 1]);
        Assert.True(Array.IndexOf(cmd.Args, "-hwaccel_device") < Array.IndexOf(cmd.Args, "-i"));
        // Never the qsv hwaccel: it swaps in hevc_qsv, which fails instead of falling back on unsupported streams.
        Assert.DoesNotContain("qsv", cmd.Args[init..Array.IndexOf(cmd.Args, "-i")]);
    }

    [Fact]
    public void Amf_decodes_with_d3d11va_on_the_amd_adapter()
    {
        var cmd = Build(HdrConversion, ["h264_amf"], ["amf/hevc"]);
        Assert.Equal("d3d11va=hwdec:,vendor_id=0x1002", cmd.Args[Array.IndexOf(cmd.Args, "-init_hw_device") + 1]);
    }

    [Fact]
    public void Software_encoding_keeps_software_decoding()
    {
        // Emby's own encoder (GPU encoding not preferred for this kind of job), even with validated hardware decoders.
        var cmd = Build(HdrConversion, ["h264_nvenc", "libx264"], ["nvenc/hevc"], preferHardware: false);
        Assert.Equal("libx264", cmd.VideoEncoder);
        Assert.Null(cmd.HardwareDecoder);
        Assert.DoesNotContain("-hwaccel", cmd.Args);
        Assert.DoesNotContain("-init_hw_device", cmd.Args);
    }

    [Theory]
    [InlineData("")]                        // switched off in the settings, or nothing validated
    [InlineData("nvenc/h264,nvenc/av1")]    // HEVC not validated on this GPU
    [InlineData("qsv/hevc")]                // validated on another GPU than the encoder's
    public void Unvalidated_decoders_are_not_used(string decoders)
    {
        var cmd = Build(HdrConversion, ["h264_nvenc"], decoders.Split(',', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("h264_nvenc", cmd.VideoEncoder);
        Assert.Null(cmd.HardwareDecoder);
        Assert.DoesNotContain("-hwaccel", cmd.Args);
    }

    [Fact]
    public void Only_the_video_input_gets_the_decode_method()
    {
        // A second input (external audio, subtitles) without a video decoder of its own.
        string[] template = ["-c:v:0", "h264", "-i", "{input:0}", "-i", "{input:1}", "-map", "0:0", "-map", "1:0",
            "-c:v:0", "libx264", "-f", "segment", "-segment_list", "{list}", "{out}"];
        var cmd = Build(template, ["h264_nvenc"], ["nvenc/h264"]);

        int firstInput = Array.IndexOf(cmd.Args, "-i");
        int secondInput = Array.LastIndexOf(cmd.Args, "-i");
        int hw = Array.IndexOf(cmd.Args, "-hwaccel");
        Assert.Equal(1, Count(cmd.Args, "-hwaccel"));
        Assert.True(hw < firstInput);
        Assert.Equal(-1, Array.IndexOf(cmd.Args, "-hwaccel", firstInput + 1, secondInput - firstInput - 1));
    }

    [Fact]
    public void Video_input_after_another_input_is_found()
    {
        string[] template = ["-i", "{input:1}", "-c:v:0", "hevc", "-i", "{input:0}", "-map", "1:0", "-map", "0:0",
            "-c:v:0", "libx264", "-f", "segment", "-segment_list", "{list}", "{out}"];
        var cmd = Build(template, ["h264_nvenc"], ["nvenc/hevc"]);
        int hw = Array.IndexOf(cmd.Args, "-hwaccel");
        Assert.True(hw > Array.IndexOf(cmd.Args, "-i") && hw < Array.LastIndexOf(cmd.Args, "-i"));
    }

    [Fact]
    public void Without_a_decoder_named_by_emby_nothing_is_added()
    {
        // ffmpeg would pick its decoder itself (for AV1 the native one, which has no software path): no guess.
        string[] template = ["-i", "{input:0}", "-c:v:0", "libx264", "-f", "segment", "-segment_list", "{list}", "{out}"];
        Assert.DoesNotContain("-hwaccel", Build(template, ["h264_nvenc"], ["nvenc/h264", "nvenc/av1"]).Args);
    }

    [Fact]
    public void A_template_choosing_its_own_decode_method_is_left_alone()
    {
        string[] template = ["-hwaccel", "auto", "-c:v:0", "h264", "-i", "{input:0}", "-c:v:0", "libx264", "-f", "segment", "-segment_list", "{list}", "{out}"];
        var cmd = Build(template, ["h264_nvenc"], ["nvenc/h264"]);
        Assert.Equal(1, Count(cmd.Args, "-hwaccel"));
        Assert.Equal("auto", cmd.Args[Array.IndexOf(cmd.Args, "-hwaccel") + 1]);
        Assert.Null(cmd.HardwareDecoder);
    }

    [Fact]
    public void Cached_inputs_get_the_decode_method_too()
    {
        var cmd = Build(HdrConversion, ["h264_nvenc"], ["nvenc/hevc"], localInputs: new Dictionary<int, string> { [0] = "http://127.0.0.1:5000/x" });
        int input = Array.IndexOf(cmd.Args, "-i");
        Assert.Equal("http://127.0.0.1:5000/x", cmd.Args[input + 1]);
        Assert.InRange(Array.IndexOf(cmd.Args, "-hwaccel"), 0, input);
    }

    [Fact]
    public void Conversion_chunks_keep_their_seek_with_hardware_decoding()
    {
        string[] args = ["-y", "-c:v:0", "hevc", "-i", "/volume4/Film/a.mkv", "-map", "0:0", "-map", "0:1", "-c:v:0", "libx264", "-crf:v:0", "22",
            "-maxrate:v:0", "8000000", "-bufsize:v:0", "16000000", "-c:a:0", "aac", "-f", "mp4", "/var/packages/EmbyServer/var/sync/1/2/x.mp4"];
        var plan = EmbyConversionPlanner.Plan(args, ["/volume4/Film"]);
        Assert.True(plan.Remote, plan.Reason);
        var chunk = EmbyConversionPlanner.ChunkArgs(plan.ChunkTemplate, 100, 240.04, true, 360.08);
        var cmd = Build(chunk, ["h264_nvenc"], ["nvenc/hevc"]);

        int input = Array.IndexOf(cmd.Args, "-i");
        Assert.Equal("nvenc/hevc", cmd.HardwareDecoder);
        Assert.InRange(Array.IndexOf(cmd.Args, "-hwaccel"), 0, input);
        Assert.InRange(Array.IndexOf(cmd.Args, "-ss"), 0, input);
        Assert.Equal("240.039", cmd.Args[Array.IndexOf(cmd.Args, "-ss") + 1]);
        Assert.Equal("120.039", cmd.Args[Array.IndexOf(cmd.Args, "-t") + 1]);
    }

    [Theory]
    [InlineData("h264_nvenc", "nvenc")]
    [InlineData("hevc_qsv", "qsv")]
    [InlineData("av1_amf", "amf")]
    [InlineData("libx264", null)]
    [InlineData("copy", null)]
    public void Encoder_families(string encoder, string? family) => Assert.Equal(family, HardwareDecoding.FamilyOf(encoder));
}
