using RemoteTranscode.Common;

namespace RemoteTranscode.Tests;

public class PlaybackPriorityTests
{
    private static readonly PlaybackPrioritySettings Settings = new();

    [Theory]
    [InlineData(40, 50.0, false)]    // encode running, plenty of headroom: playback joins, encode stays
    [InlineData(92, 50.0, true)]     // CPU saturated
    [InlineData(40, 95.0, true)]     // GPU encoder saturated
    [InlineData(40, null, false)]    // CPU-only worker below threshold
    public void Headroom_decides_whether_an_encode_must_move(double cpu, double? gpu, bool lacks) =>
        Assert.Equal(lacks, PlaybackPriority.LacksHeadroom(new WorkerLoad(cpu, gpu, 50, 30, gpu, 0), Settings));

    [Fact]
    public void Slow_playback_is_reported_once_after_consecutive_slow_reports()
    {
        var monitor = new PlaybackSpeedMonitor(Settings);
        var t = TimeSpan.FromSeconds(5);
        Assert.False(monitor.Report(t, 0.5));                     // grace period: start-up is slow anyway
        t = TimeSpan.FromSeconds(20);
        for (int i = 0; i < 4; i++)
            Assert.False(monitor.Report(t, 0.9));
        Assert.False(monitor.Report(t, 2.0));                     // recovered: counter reset
        for (int i = 0; i < 4; i++)
            Assert.False(monitor.Report(t, 0.9));
        Assert.True(monitor.Report(t, 0.9));                      // 5th consecutive slow report
        Assert.False(monitor.Report(t, 0.8));                     // only once
        monitor.Reset();
        for (int i = 0; i < 4; i++)
            Assert.False(monitor.Report(t, 0.9));
        Assert.True(monitor.Report(t, 0.9));
    }

    [Fact]
    public void Fast_playback_never_triggers()
    {
        var monitor = new PlaybackSpeedMonitor(Settings);
        for (int i = 0; i < 50; i++)
            Assert.False(monitor.Report(TimeSpan.FromSeconds(30 + i), 3.5));
    }
}
