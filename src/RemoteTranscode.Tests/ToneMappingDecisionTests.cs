using RemoteTranscode.Common;
using RemoteTranscode.Coordinator;

namespace RemoteTranscode.Tests;

/// <summary>Tone mapper of a conversion before its first slice starts: chosen again each pass, held back right after start-up.</summary>
public class ToneMappingDecisionTests
{
    private static readonly WorkerCapabilities CpuWorker = new()
    {
        VideoEncoders = ["libx264", "libx265"],
        Filters = ["scale", "setparams", "format", "zscale", "tonemap"],
    };

    private static readonly WorkerCapabilities GpuWorker = CpuWorker with
    {
        Filters = ["scale", "setparams", "format", "zscale", "tonemap", "hwupload", "hwdownload", "libplacebo"],
        GpuToneMapping = new GpuToneMapping(GpuToneMapping.LibplaceboMethod, "Intel(R) UHD Graphics"),
    };

    private static readonly JobRequirements HdrJob = new("libx264", ["format", "supertonemap"]);
    private static readonly JobRequirements SdrJob = new("libx264", ["scale"]);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

    private static ToneMappingDecision.Outcome Decide(JobRequirements job, WorkerCapabilities[] connected, double sinceStart, bool missing = true) =>
        ToneMappingDecision.Decide(job, connected, TimeSpan.FromSeconds(sinceStart), Grace, () => missing);

    [Fact]
    public void Gpu_worker_connected_gives_libplacebo_at_once()
    {
        var o = Decide(HdrJob, [CpuWorker, GpuWorker], 1);
        Assert.Equal(ToneMapper.Libplacebo, o.Requirements.ToneMapping);
        Assert.False(o.Wait);
    }

    [Fact]
    public void Just_after_start_without_gpu_worker_waits()
    {
        // Emby restarted: its conversion arrives 1 s later, before the workers reconnect.
        var o = Decide(HdrJob, [], 1);
        Assert.True(o.Wait);
        Assert.Equal(ToneMapper.Zscale, o.Requirements.ToneMapping);
        // A CPU worker back first does not end the wait: the GPU one may follow.
        Assert.True(Decide(HdrJob, [CpuWorker], 30).Wait);
    }

    [Fact]
    public void Re_evaluated_choice_switches_to_libplacebo_when_the_gpu_worker_reconnects()
    {
        var first = Decide(HdrJob, [CpuWorker], 5);
        Assert.True(first.Wait);
        var next = Decide(first.Requirements, [CpuWorker, GpuWorker], 15);
        Assert.False(next.Wait);
        Assert.Equal(ToneMapper.Libplacebo, next.Requirements.ToneMapping);
        // And back to zscale if it leaves again before anything started.
        var gone = Decide(next.Requirements, [CpuWorker], 90);
        Assert.False(gone.Wait);
        Assert.Equal(ToneMapper.Zscale, gone.Requirements.ToneMapping);
    }

    [Fact]
    public void After_the_grace_period_zscale_starts_at_once()
    {
        var o = Decide(HdrJob, [CpuWorker], 60);
        Assert.False(o.Wait);
        Assert.Equal(ToneMapper.Zscale, o.Requirements.ToneMapping);
        Assert.False(Decide(HdrJob, [], 3600).Wait);
    }

    [Fact]
    public void No_wait_when_every_paired_worker_is_connected()
    {
        bool asked = false;
        var o = ToneMappingDecision.Decide(HdrJob, [CpuWorker], TimeSpan.FromSeconds(5), Grace, () => { asked = true; return false; });
        Assert.True(asked);
        Assert.False(o.Wait);
        Assert.Equal(ToneMapper.Zscale, o.Requirements.ToneMapping);
    }

    [Fact]
    public void Worker_store_is_not_read_when_it_does_not_matter()
    {
        bool asked = false;
        bool Missing() { asked = true; return true; }
        ToneMappingDecision.Decide(HdrJob, [GpuWorker], TimeSpan.FromSeconds(1), Grace, Missing);
        ToneMappingDecision.Decide(SdrJob, [], TimeSpan.FromSeconds(1), Grace, Missing);
        ToneMappingDecision.Decide(HdrJob, [], TimeSpan.FromSeconds(120), Grace, Missing);
        Assert.False(asked);
    }

    [Fact]
    public void Without_hdr_nothing_waits()
    {
        var o = Decide(SdrJob, [], 1);
        Assert.False(o.Wait);
        Assert.Equal(ToneMapper.Zscale, o.Requirements.ToneMapping);
    }

    [Fact]
    public void Zero_grace_never_waits()
    {
        Assert.False(ToneMappingDecision.Decide(HdrJob, [], TimeSpan.Zero, TimeSpan.Zero, () => true).Wait);
    }

    [Fact]
    public void Gpu_worker_without_the_encoder_does_not_count()
    {
        var o = Decide(HdrJob with { VideoEncoder = "libx265" }, [GpuWorker with { VideoEncoders = ["libx264"] }], 1);
        Assert.Equal(ToneMapper.Zscale, o.Requirements.ToneMapping);
        Assert.True(o.Wait);
    }
}
