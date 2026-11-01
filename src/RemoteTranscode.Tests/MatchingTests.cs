using RemoteTranscode.Common;

namespace RemoteTranscode.Tests;

public class MatchingTests
{
    private static WorkerCapabilities Caps(params string[] encoders) => new()
    {
        VideoEncoders = encoders,
        Filters = ["scale", "subtitles", "format", "zscale", "tonemap"],
    };

    private static readonly JobRequirements H264WithSubs = new("libx264", ["scale", "subtitles"]);

    [Fact]
    public void Requirements_come_from_the_output_encoder_and_filter_graphs()
    {
        string[] args = ["-c:v:0", "h264", "-i", "/volume4/Film/a.mkv",
            "-filter_complex", "[0:0]scale@f1=width=720:height=388,subtitles@f2=filename='/volume4/Film/a.srt'[o]",
            "-c:v:0", "libx265", "-f", "segment", "-segment_list", "/t/A/A.m3u8", "/t/A/A_%d.ts"];
        var plan = EmbyJobPlanner.Plan(args, new PlannerOptions
        {
            AllowedRoots = ["/volume4/Film"],
            OutputRoots = ["/t"],
            GetFileSize = _ => 1,
            ListDirectory = _ => [],
        });
        Assert.True(plan.Remote, plan.Reason);
        Assert.Equal("libx265", plan.Requirements!.VideoEncoder);   // not the input decoder "h264"
        Assert.Equal(["scale", "subtitles"], plan.Requirements.Filters);
        Assert.Equal("/t/A", plan.SessionKey);
    }

    [Fact]
    public void Worker_without_needed_filter_or_encoder_is_incompatible()
    {
        Assert.Null(WorkerMatching.Incompatibility(H264WithSubs, Caps("libx264")));
        Assert.Null(WorkerMatching.Incompatibility(H264WithSubs, Caps("h264_nvenc")));   // hardware equivalent is enough
        Assert.NotNull(WorkerMatching.Incompatibility(new JobRequirements("libx265", []), Caps("libx264", "h264_qsv")));
        Assert.NotNull(WorkerMatching.Incompatibility(new JobRequirements("libx264", ["libplacebo"]), Caps("libx264")));
    }

    [Fact]
    public void Same_session_beats_hardware_which_beats_free_capacity()
    {
        var idle = new WorkerLoad(5, 0, 30, 0, 0, 0);
        double previousCpuWorker = WorkerMatching.Score(H264WithSubs, Caps("libx264"), idle, 1, 2, sameSession: true);
        double gpuWorker = WorkerMatching.Score(H264WithSubs, Caps("h264_nvenc", "libx264"), idle, 0, 2, sameSession: false);
        double cpuWorker = WorkerMatching.Score(H264WithSubs, Caps("libx264"), idle, 0, 2, sameSession: false);
        Assert.True(previousCpuWorker > gpuWorker);
        Assert.True(gpuWorker > cpuWorker);
    }

    [Fact]
    public void Busier_and_noisier_workers_score_lower()
    {
        var caps = Caps("h264_qsv", "libx264");
        double free = WorkerMatching.Score(H264WithSubs, caps, new WorkerLoad(10, 5, 30, 0, 0, 5), 0, 2, false);
        double halfFull = WorkerMatching.Score(H264WithSubs, caps, new WorkerLoad(10, 5, 30, 0, 0, 5), 1, 2, false);
        double gaming = WorkerMatching.Score(H264WithSubs, caps, new WorkerLoad(80, 90, 60, 0, 0, 90), 0, 2, false);
        Assert.True(free > halfFull);
        Assert.True(free > gaming);
    }
}

public class EmbyFilterTests
{
    // Filter graph Emby 4.10 generated for an HDR (PQ) HEVC film played on a web client.
    private const string EmbyHdrGraph = "[0:0]scale@f1=width=426:height=176,setparams@f2=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,format@f3=pix_fmts=p010,supertonemap@f4=tonemap=hable,setparams@f5=color_primaries=bt709:color_trc=bt709:colorspace=bt709,format@f6=pix_fmts=yuv420p[f6_out0]";

    [Fact]
    public void Supertonemap_becomes_standard_filters()
    {
        string t = EmbyFilters.Translate(EmbyHdrGraph);
        Assert.DoesNotContain("supertonemap", t);
        Assert.Contains("tonemap@f4=tonemap=hable:desat=0", t);
        Assert.Contains("zscale@f4_lin=t=linear:npl=100", t);
        Assert.EndsWith("format@f6=pix_fmts=yuv420p[f6_out0]", t);
        Assert.All(ArgTemplateValidator.FilterNames(t), f => Assert.Contains(f, new[] { "scale", "setparams", "format", "zscale", "tonemap" }));
    }

    [Theory]
    [InlineData("supertonemap", "tonemap=hable:desat=0")]
    [InlineData("supertonemap=bt2390", "tonemap=hable:desat=0")]          // no standard bt2390
    [InlineData("supertonemap=tonemap=mobius:desat=0.5", "tonemap=mobius:desat=0.5")]
    public void Options_are_carried_over(string filter, string expected) =>
        Assert.Contains(expected, EmbyFilters.Translate(filter));

    [Fact]
    public void Worker_with_zscale_and_tonemap_can_run_emby_hdr_playback()
    {
        var job = new JobRequirements("libx265", ["scale", "setparams", "format", "supertonemap"]);
        var caps = new WorkerCapabilities { VideoEncoders = ["libx265"], Filters = ["scale", "setparams", "format", "zscale", "tonemap"] };
        Assert.Null(WorkerMatching.Incompatibility(job, caps));
        Assert.NotNull(WorkerMatching.Incompatibility(job, caps with { Filters = ["scale", "setparams", "format"] }));
    }
}
