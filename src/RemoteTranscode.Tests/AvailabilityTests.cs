using RemoteTranscode.Common;

namespace RemoteTranscode.Tests;

public class AvailabilityTests
{
    private static readonly DateTime T0 = new(2026, 10, 27, 14, 0, 0); // Tuesday

    private static AvailabilitySample Sample(DateTime now, bool enabled = true, int idleMin = 60, bool battery = false,
        double cpu = 10, double ownCpu = 0, int jobs = 0, int max = 2, bool connected = true) =>
        new(now, enabled, TimeSpan.FromMinutes(idleMin), battery, new WorkerLoad(cpu, null, 40, ownCpu), jobs, max, connected);

    private static AvailabilityEngine Engine(Action<AvailabilitySettings>? configure = null)
    {
        var s = new AvailabilitySettings { StableSeconds = 20, LoadHysteresisSeconds = 30 };
        configure?.Invoke(s);
        return new AvailabilityEngine(s);
    }

    [Fact]
    public void Becomes_available_only_after_stable_period()
    {
        var e = Engine();
        Assert.Equal(WorkerState.Unavailable, e.Evaluate(Sample(T0)).State);
        Assert.Equal(WorkerState.Unavailable, e.Evaluate(Sample(T0.AddSeconds(10))).State);
        Assert.Equal(WorkerState.Available, e.Evaluate(Sample(T0.AddSeconds(21))).State);
    }

    [Fact]
    public void Offline_when_not_connected() =>
        Assert.Equal(WorkerState.Offline, Engine().Evaluate(Sample(T0, connected: false)).State);

    [Fact]
    public void Busy_when_all_slots_used()
    {
        var e = Engine();
        e.Evaluate(Sample(T0));
        Assert.Equal(WorkerState.Busy, e.Evaluate(Sample(T0.AddSeconds(30), jobs: 2)).State);
    }

    [Fact]
    public void Automatic_mode_requires_idle_time()
    {
        var e = Engine(s => { s.Mode = AvailabilityMode.Automatic; s.IdleMinutes = 10; });
        var d = e.Evaluate(Sample(T0, idleMin: 2));
        Assert.Equal(WorkerState.Unavailable, d.State);
        Assert.Equal("PC utilisé", d.Reason);
        e.Evaluate(Sample(T0.AddSeconds(1), idleMin: 11));
        Assert.Equal(WorkerState.Available, e.Evaluate(Sample(T0.AddSeconds(30), idleMin: 11)).State);
    }

    [Theory]
    [InlineData(ReclaimBehavior.FinishPlayback, WorkerState.Draining, ReclaimAction.Drain)]
    [InlineData(ReclaimBehavior.ReleaseImmediately, WorkerState.Unavailable, ReclaimAction.Release)]
    public void Owner_returning_applies_reclaim_behaviour_once(ReclaimBehavior behavior, WorkerState state, ReclaimAction action)
    {
        var e = Engine(s => { s.Mode = AvailabilityMode.Automatic; s.Reclaim = behavior; });
        e.Evaluate(Sample(T0, jobs: 1));
        var first = e.Evaluate(Sample(T0.AddSeconds(30), idleMin: 0, jobs: 1));
        var second = e.Evaluate(Sample(T0.AddSeconds(31), idleMin: 0, jobs: 1));
        Assert.Equal((state, action), (first.State, first.Action));
        Assert.Equal(ReclaimAction.None, second.Action);
    }

    [Fact]
    public void Battery_blocks_when_configured()
    {
        Assert.Equal("sur batterie", Engine(s => s.BlockOnBattery = true).Evaluate(Sample(T0, battery: true)).Reason);
        Assert.NotEqual("sur batterie", Engine().Evaluate(Sample(T0, battery: true)).Reason); // off by default
    }

    [Fact]
    public void Load_threshold_is_debounced_and_ignores_own_ffmpeg()
    {
        var e = Engine(s => s.MaxForeignCpuPercent = 50);
        e.Evaluate(Sample(T0));
        Assert.Equal(WorkerState.Available, e.Evaluate(Sample(T0.AddSeconds(25))).State);
        // 90% total but 70% is our own ffmpeg: only 20% foreign, stays available.
        Assert.Equal(WorkerState.Available, e.Evaluate(Sample(T0.AddSeconds(26), cpu: 90, ownCpu: 70)).State);
        // Foreign spike shorter than the hysteresis: no change.
        Assert.Equal(WorkerState.Available, e.Evaluate(Sample(T0.AddSeconds(27), cpu: 90)).State);
        Assert.Equal(WorkerState.Available, e.Evaluate(Sample(T0.AddSeconds(40), cpu: 90)).State);
        // Sustained: blocked, and running jobs are drained (not released).
        var blocked = e.Evaluate(Sample(T0.AddSeconds(58), cpu: 90, jobs: 1));
        Assert.Equal((WorkerState.Draining, ReclaimAction.None), (blocked.State, blocked.Action));
        // Load drops: still blocked until the hysteresis has elapsed again.
        Assert.Equal(WorkerState.Unavailable, e.Evaluate(Sample(T0.AddSeconds(60))).State);
        Assert.Equal(WorkerState.Unavailable, e.Evaluate(Sample(T0.AddSeconds(85))).State);
        e.Evaluate(Sample(T0.AddSeconds(91)));
        Assert.Equal(WorkerState.Available, e.Evaluate(Sample(T0.AddSeconds(115))).State);
    }

    [Fact]
    public void Schedule_windows_including_overnight()
    {
        var all = Enum.GetValues<DayOfWeek>();
        var night = new TimeWindow(all, new TimeOnly(22, 0), new TimeOnly(7, 0));
        Assert.True(night.Contains(new DateTime(2026, 10, 27, 23, 0, 0)));
        Assert.True(night.Contains(new DateTime(2026, 10, 28, 6, 59, 0)));
        Assert.False(night.Contains(new DateTime(2026, 10, 27, 14, 0, 0)));

        var weekdays = new TimeWindow([DayOfWeek.Saturday], new TimeOnly(9, 0), new TimeOnly(18, 0));
        Assert.Equal("hors plage horaire", Engine(s => s.Schedule = [weekdays]).Evaluate(Sample(T0)).Reason);
    }

    [Fact]
    public void Disabling_while_idle_is_immediate()
    {
        var e = Engine();
        e.Evaluate(Sample(T0));
        e.Evaluate(Sample(T0.AddSeconds(30)));
        Assert.Equal(WorkerState.Unavailable, e.Evaluate(Sample(T0.AddSeconds(31), enabled: false)).State);
    }
}

public class GpuLoadTests
{
    [Fact]
    public void Splits_own_and_foreign_usage_per_engine()
    {
        (string, double)[] counters =
        [
            ("pid_100_luid_0x00000000_0x0000C3E5_phys_0_eng_0_engtype_3D", 30),        // game
            ("pid_200_luid_0x00000000_0x0000C3E5_phys_0_eng_0_engtype_3D", 15),        // browser, same engine
            ("pid_900_luid_0x00000000_0x0000C3E5_phys_0_eng_5_engtype_VideoEncode", 60), // our ffmpeg (NVENC/QSV)
            ("pid_900_luid_0x00000000_0x0000C3E5_phys_0_eng_3_engtype_VideoDecode", 20),
        ];
        var usage = GpuEngineCounters.Aggregate(counters, new HashSet<int> { 900 });
        Assert.Equal(60, usage.Total);   // busiest engine: video encode
        Assert.Equal(60, usage.Own);
        Assert.Equal(45, usage.Foreign); // 3D engine shared by the other applications
    }

    [Fact]
    public void No_gpu_activity_or_software_encoding_reports_zero_gpu()
    {
        var usage = GpuEngineCounters.Aggregate([], new HashSet<int> { 900 });
        Assert.Equal(new GpuUsage(0, 0, 0), usage);
    }

    [Fact]
    public void Cpu_only_worker_is_judged_on_foreign_cpu_only()
    {
        // No GPU counters at all (GPU figures null) and a libx264 transcode using 80% CPU: still available.
        var engine = new AvailabilityEngine(new AvailabilitySettings { StableSeconds = 0, MaxForeignCpuPercent = 50 });
        var now = new DateTime(2026, 10, 27, 14, 0, 0);
        var sample = new AvailabilitySample(now, true, TimeSpan.FromHours(1), false, new WorkerLoad(95, null, 40, 80), 1, 2, true);
        Assert.Equal(WorkerState.Available, engine.Evaluate(sample).State);
    }

    [Fact]
    public void Foreign_gpu_load_blocks_after_hysteresis_but_own_does_not()
    {
        var engine = new AvailabilityEngine(new AvailabilitySettings { StableSeconds = 0, LoadHysteresisSeconds = 10, MaxForeignGpuPercent = 50 });
        var t = new DateTime(2026, 10, 27, 14, 0, 0);
        AvailabilitySample S(int sec, double own, double foreign) =>
            new(t.AddSeconds(sec), true, TimeSpan.FromHours(1), false, new WorkerLoad(20, Math.Max(own, foreign), 40, 5, own, foreign), 0, 2, true);
        Assert.Equal(WorkerState.Available, engine.Evaluate(S(0, own: 95, foreign: 10)).State);
        Assert.Equal(WorkerState.Available, engine.Evaluate(S(1, own: 0, foreign: 80)).State);
        Assert.Equal(WorkerState.Unavailable, engine.Evaluate(S(12, own: 0, foreign: 80)).State);
    }
}
