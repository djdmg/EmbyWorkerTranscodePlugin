using RemoteTranscode.Common;

namespace RemoteTranscode.Tests;

/// <summary>HDR → SDR on the GPU (libplacebo): translation, choice per conversion, worker matching, worker command.</summary>
public class GpuToneMappingTests
{
    private const string EmbyHdrGraph = "[0:0]scale@f1=width=426:height=176,setparams@f2=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,format@f3=pix_fmts=p010,supertonemap@f4=tonemap=hable,setparams@f5=color_primaries=bt709:color_trc=bt709:colorspace=bt709,format@f6=pix_fmts=yuv420p[f6_out0]";

    private const string IntelDevice = "vulkan=vk:Intel,disable_multiplane=1,linear_images=1";

    private static readonly string[] HdrSlice =
    [
        "-y", "-copyts", "-start_at_zero", "-c:v:0", "hevc", "-noautorotate", "-ss", "105.039", "-t", "105.02",
        "-i", "{input:0}", "-map", "[v]", "-filter_complex", "[0:0]format=p010,supertonemap@f4=tonemap=bt2390,format=yuv420p[v]",
        "-c:v:0", "libx264", "-crf:v:0", "22", "-maxrate:v:0", "8000000", "-f", "segment", "-segment_list", "{list}", "{out}",
    ];

    private static readonly WorkerCapabilities CpuWorker = new()
    {
        VideoEncoders = ["libx264", "libx265"],
        Filters = ["scale", "setparams", "format", "zscale", "tonemap"],
    };

    private static readonly WorkerCapabilities GpuWorker = CpuWorker with
    {
        Filters = ["scale", "setparams", "format", "zscale", "tonemap", "hwupload", "hwdownload", "libplacebo"],
        GpuToneMapping = new GpuToneMapping(GpuToneMapping.LibplaceboMethod, IntelDevice),
    };

    private static readonly JobRequirements HdrJob = new("libx264", ["format", "supertonemap"]);

    // ---- Translation ------------------------------------------------------------------------------

    [Fact]
    public void Libplacebo_translation_uploads_tone_maps_and_downloads()
    {
        string t = EmbyFilters.Translate(EmbyHdrGraph, ToneMapper.Libplacebo);
        Assert.DoesNotContain("supertonemap", t);
        Assert.Contains("format@f3=pix_fmts=p010,hwupload@f4_up,libplacebo@f4=tonemapping=hable:peak_detect=0:colorspace=bt709:" +
            "color_primaries=bt709:color_trc=bt709:range=tv:format=yuv420p,hwdownload@f4_dl,format@f4_f=yuv420p,setparams@f5", t);
        Assert.EndsWith("format@f6=pix_fmts=yuv420p[f6_out0]", t);
        Assert.DoesNotContain("zscale", t);
        Assert.True(EmbyFilters.UsesLibplacebo(t));
        Assert.True(ArgTemplateValidator.Validate(["-i", "{input:0}", "-filter_complex", t, "{out}"], out string? error), error);
    }

    [Theory]
    [InlineData("supertonemap", "tonemapping=bt.2390:")]                  // supertonemap's default
    [InlineData("supertonemap=bt2390", "tonemapping=bt.2390:")]
    [InlineData("supertonemap=tonemap=bt2390", "tonemapping=bt.2390:")]
    [InlineData("supertonemap=hable", "tonemapping=hable:")]
    [InlineData("supertonemap=tonemap=mobius:desat=0.5", "tonemapping=mobius:")]
    [InlineData("supertonemap=tonemap=reinhard", "tonemapping=reinhard:")]
    [InlineData("supertonemap=tonemap=clip", "tonemapping=clip:")]
    [InlineData("supertonemap=tonemap=linear", "tonemapping=linear:")]
    [InlineData("supertonemap=tonemap=gamma", "tonemapping=gamma:")]
    [InlineData("supertonemap=tonemap=unknown", "tonemapping=bt.2390:")]
    public void Libplacebo_algorithms(string filter, string expected)
    {
        string t = EmbyFilters.Translate(filter, ToneMapper.Libplacebo);
        Assert.Contains(expected, t);
        Assert.Contains("peak_detect=0", t);
        Assert.DoesNotContain("desat", t);
        Assert.StartsWith("hwupload@stm_up,libplacebo@stm=", t);
    }

    [Fact]
    public void Zscale_stays_the_default_and_the_fallback()
    {
        Assert.Equal(EmbyFilters.Translate(EmbyHdrGraph), EmbyFilters.Translate(EmbyHdrGraph, ToneMapper.Zscale));
        Assert.Contains("tonemap@f4=tonemap=hable:desat=0", EmbyFilters.Translate(EmbyHdrGraph));
        Assert.False(EmbyFilters.UsesLibplacebo(EmbyFilters.Translate(EmbyHdrGraph)));
        // bt2390 has no tonemap counterpart: hable on the CPU, as before.
        Assert.Contains("tonemap@stm=tonemap=hable:desat=0", EmbyFilters.Translate("supertonemap=bt2390"));
    }

    [Fact]
    public void Requirements_expand_per_tone_mapper()
    {
        Assert.Equal(["format", "zscale", "tonemap"], EmbyFilters.ExpandRequirements(["format", "supertonemap"]).ToArray());
        Assert.Equal(["format", "hwupload", "libplacebo", "hwdownload"], EmbyFilters.ExpandRequirements(["format", "supertonemap"], ToneMapper.Libplacebo).ToArray());
    }

    [Fact]
    public void Translated_arguments_follow_the_mode()
    {
        string[] gpu = EmbyFilters.TranslateArgs(HdrSlice, ToneMapper.Libplacebo);
        Assert.True(EmbyFilters.UsesLibplacebo(gpu));
        Assert.False(EmbyFilters.UsesLibplacebo(EmbyFilters.TranslateArgs(HdrSlice)));
        Assert.False(EmbyFilters.UsesLibplacebo(HdrSlice));
        // Only filter graphs are translated.
        Assert.Equal(HdrSlice.Length, gpu.Length);
        Assert.Equal("{input:0}", gpu[Array.IndexOf(gpu, "-i") + 1]);
    }

    [Fact]
    public void Validator_refuses_libplacebo_user_shaders()
    {
        Assert.False(ArgTemplateValidator.Validate(["-i", "{input:0}", "-vf", "hwupload,libplacebo=custom_shader_path=x.glsl,hwdownload", "{out}"], out _));
        Assert.False(ArgTemplateValidator.Validate(["-i", "{input:0}", "-vf", "libplacebo=custom_shader_path={aux:1}", "{out}"], out _));
    }

    // ---- Choice per conversion and worker matching --------------------------------------------------

    [Fact]
    public void Conversion_uses_libplacebo_when_a_connected_worker_can()
    {
        Assert.Equal(ToneMapper.Libplacebo, WorkerMatching.WithToneMapping(HdrJob, [CpuWorker, GpuWorker]).ToneMapping);
        Assert.Equal(ToneMapper.Zscale, WorkerMatching.WithToneMapping(HdrJob, [CpuWorker]).ToneMapping);
        Assert.Equal(ToneMapper.Zscale, WorkerMatching.WithToneMapping(HdrJob, []).ToneMapping);
        // Without HDR there is nothing to choose.
        Assert.Equal(ToneMapper.Zscale, WorkerMatching.WithToneMapping(new JobRequirements("libx264", ["scale"]), [GpuWorker]).ToneMapping);
        // A worker with the capability but without the encoder does not count.
        Assert.Equal(ToneMapper.Zscale, WorkerMatching.WithToneMapping(HdrJob with { VideoEncoder = "libx265" },
            [GpuWorker with { VideoEncoders = ["libx264"] }]).ToneMapping);
    }

    [Fact]
    public void Libplacebo_conversion_goes_to_capable_workers_only()
    {
        var gpuJob = HdrJob with { ToneMapping = ToneMapper.Libplacebo };
        Assert.Null(WorkerMatching.Incompatibility(gpuJob, GpuWorker));
        Assert.NotNull(WorkerMatching.Incompatibility(gpuJob, CpuWorker));
        // A full FFmpeg (libplacebo listed) whose probe failed or whose owner switched it off: no.
        Assert.NotNull(WorkerMatching.Incompatibility(gpuJob, GpuWorker with { GpuToneMapping = null }));
        // The server's capabilities never carry the GPU tone mapping.
        Assert.NotNull(WorkerMatching.Incompatibility(gpuJob, new WorkerCapabilities { VideoEncoders = ["libx264"], Filters = GpuWorker.Filters }));
        // A zscale conversion stays open to every worker, GPU-capable ones included.
        Assert.Null(WorkerMatching.Incompatibility(HdrJob, CpuWorker));
        Assert.Null(WorkerMatching.Incompatibility(HdrJob, GpuWorker));
    }

    // ---- Worker command ---------------------------------------------------------------------------

    private static WorkerCommand Build(string[] template, GpuToneMapping? toneMapping, string[]? encoders = null, string[]? decoders = null) =>
        WorkerCommandBuilder.Build(template, new WorkerCommandContext
        {
            CoordinatorBaseUrl = "https://c", JobId = "j", JobToken = "t", CaFile = null,
            Files = [new JobFile(0, JobFileKind.Input, "a.mkv", 1)],
            SegmentSeconds = 6, VideoEncoders = encoders ?? ["libx264"], PreferHardwareEncoding = true, QualityMode = true,
            HardwareDecoders = decoders ?? [], GpuToneMapping = toneMapping,
        });

    [Fact]
    public void Coordinator_translated_slice_gets_the_vulkan_device_before_the_first_input()
    {
        var cmd = Build(EmbyFilters.TranslateArgs(HdrSlice, ToneMapper.Libplacebo), GpuWorker.GpuToneMapping);
        int init = Array.IndexOf(cmd.Args, "-init_hw_device");
        Assert.Equal(IntelDevice, cmd.Args[init + 1]);
        Assert.Equal("vk", cmd.Args[Array.IndexOf(cmd.Args, "-filter_hw_device") + 1]);
        Assert.True(init < Array.IndexOf(cmd.Args, "-i"));
        Assert.True(cmd.GpuToneMapped);
        Assert.False(cmd.ToneMappedHere);
    }

    [Fact]
    public void Zscale_slice_is_left_as_is_on_a_capable_worker()
    {
        // A conversion frozen in zscale mode stays zscale even on a worker that could tone map on the GPU.
        var cmd = Build(EmbyFilters.TranslateArgs(HdrSlice), GpuWorker.GpuToneMapping);
        Assert.DoesNotContain("-init_hw_device", cmd.Args);
        Assert.False(cmd.GpuToneMapped);
        Assert.Contains(cmd.Args, a => a.Contains("tonemap@f4=tonemap=hable:desat=0", StringComparison.Ordinal));
    }

    [Fact]
    public void Playback_is_tone_mapped_on_the_gpu_when_the_worker_can()
    {
        var gpu = Build(HdrSlice, GpuWorker.GpuToneMapping);
        Assert.True(gpu.GpuToneMapped);
        Assert.True(gpu.ToneMappedHere);
        Assert.Contains(gpu.Args, a => a.Contains("libplacebo@f4=tonemapping=bt.2390:peak_detect=0", StringComparison.Ordinal));
        Assert.Equal(1, gpu.Args.Count(a => a == "-init_hw_device"));

        var cpu = Build(HdrSlice, null);
        Assert.False(cpu.GpuToneMapped);
        Assert.DoesNotContain("-init_hw_device", cpu.Args);
        Assert.Contains(cpu.Args, a => a.Contains("zscale@f4_lin", StringComparison.Ordinal));
    }

    [Fact]
    public void Vulkan_device_goes_with_gpu_decoding()
    {
        var cmd = Build(HdrSlice, GpuWorker.GpuToneMapping, ["h264_qsv", "libx264"], ["qsv/hevc"]);
        Assert.Equal("qsv/hevc", cmd.HardwareDecoder);
        Assert.Equal(2, cmd.Args.Count(a => a == "-init_hw_device"));
        Assert.Contains("d3d11va", cmd.Args);
        Assert.Contains(IntelDevice, cmd.Args);
        Assert.Equal(1, cmd.Args.Count(a => a == "-filter_hw_device"));
    }

    [Fact]
    public void Libplacebo_slice_without_the_capability_is_not_built()
    {
        Assert.Throws<InvalidOperationException>(() => Build(EmbyFilters.TranslateArgs(HdrSlice, ToneMapper.Libplacebo), null));
    }
}
