namespace RemoteTranscode.Common;

/// <summary>
/// Playback has priority over background encodes, but an encode only leaves a worker when that worker cannot carry
/// everything at once: either its load is already too high when a playback arrives, or a playback running there can
/// no longer keep up with real time.
/// </summary>
public sealed class PlaybackPrioritySettings
{
    /// <summary>Total CPU (own jobs included) above which a new playback makes room by moving an encode away.</summary>
    public double MaxCpuPercent { get; set; } = 85;

    /// <summary>Busiest GPU engine (own jobs included) above which a new playback makes room.</summary>
    public double MaxGpuPercent { get; set; } = 85;

    /// <summary>A playback transcoding slower than this (× real time) is considered starved.</summary>
    public double MinSpeed { get; set; } = 1.1;

    /// <summary>Consecutive slow progress reports (about one per second) before acting.</summary>
    public int SlowReports { get; set; } = 5;

    /// <summary>Progress right after start (input opening, first frames) is not representative.</summary>
    public int GraceSeconds { get; set; } = 15;
}

public static class PlaybackPriority
{
    /// <summary>True when the worker's measured load leaves no headroom for one more playback.</summary>
    public static bool LacksHeadroom(WorkerLoad? load, PlaybackPrioritySettings settings) =>
        load != null && (load.CpuPercent > settings.MaxCpuPercent || (load.GpuPercent ?? 0) > settings.MaxGpuPercent);
}

/// <summary>
/// Detects a playback transcode that has fallen below real time for several consecutive reports. One instance per job,
/// fed from that worker's receive loop, so it needs no locking.
/// </summary>
public sealed class PlaybackSpeedMonitor(PlaybackPrioritySettings settings)
{
    private int slow;
    private bool reported;

    /// <summary>Returns true once, when the playback has been too slow for long enough.</summary>
    public bool Report(TimeSpan sinceStart, double speed)
    {
        if (sinceStart < TimeSpan.FromSeconds(settings.GraceSeconds) || speed <= 0)
            return false;
        slow = speed < settings.MinSpeed ? slow + 1 : 0;
        if (slow < settings.SlowReports || reported)
            return false;
        reported = true;
        return true;
    }

    /// <summary>After an encode has been moved away, watch again (another one may also need to go).</summary>
    public void Reset()
    {
        slow = 0;
        reported = false;
    }
}
