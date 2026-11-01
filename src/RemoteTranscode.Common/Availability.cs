namespace RemoteTranscode.Common;

public enum AvailabilityMode
{
    /// <summary>The user switches the worker on and off.</summary>
    Manual,
    /// <summary>The worker becomes available after a period without keyboard/mouse input.</summary>
    Automatic,
}

/// <summary>What happens to running jobs when the owner needs the PC back.</summary>
public enum ReclaimBehavior
{
    /// <summary>Refuse new jobs, stop background encodes, let playback sessions end.</summary>
    FinishPlayback,
    /// <summary>Stop everything now; playback is resumed elsewhere or locally and may stall.</summary>
    ReleaseImmediately,
}

public enum ReclaimAction { None, Drain, Release }

/// <summary>A weekly time window; End before Start means it runs past midnight.</summary>
public sealed record TimeWindow(DayOfWeek[] Days, TimeOnly Start, TimeOnly End)
{
    public bool Contains(DateTime local)
    {
        var t = TimeOnly.FromDateTime(local);
        if (Start <= End)
            return Days.Contains(local.DayOfWeek) && t >= Start && t < End;
        // Overnight window: the part after midnight belongs to the previous day's window.
        return (Days.Contains(local.DayOfWeek) && t >= Start)
            || (Days.Contains(local.AddDays(-1).DayOfWeek) && t < End);
    }
}

public sealed class AvailabilitySettings
{
    public AvailabilityMode Mode { get; set; } = AvailabilityMode.Manual;
    public int IdleMinutes { get; set; } = 10;
    public bool BlockOnBattery { get; set; }

    /// <summary>Empty means no restriction.</summary>
    public List<TimeWindow> Schedule { get; set; } = [];

    /// <summary>Thresholds apply to the load of other applications, not to this worker's own ffmpeg processes.</summary>
    public double MaxForeignCpuPercent { get; set; } = 50;
    public double MaxForeignGpuPercent { get; set; } = 50;
    public double MaxMemoryPercent { get; set; } = 90;

    /// <summary>A threshold must be crossed this long before it changes the state (both directions).</summary>
    public int LoadHysteresisSeconds { get; set; } = 30;

    /// <summary>All conditions must hold this long before the worker becomes available again.</summary>
    public int StableSeconds { get; set; } = 20;

    public ReclaimBehavior Reclaim { get; set; } = ReclaimBehavior.FinishPlayback;
}

public readonly record struct AvailabilitySample(
    DateTime LocalNow,
    bool Enabled,
    TimeSpan UserIdle,
    bool OnBattery,
    WorkerLoad Load,
    int ActiveJobs,
    int MaxJobs,
    bool Connected);

public sealed record AvailabilityDecision(WorkerState State, string? Reason, ReclaimAction Action);

/// <summary>
/// Decides the worker state from periodic samples. Owner-related conditions (disabled, PC in use, battery, schedule)
/// act immediately; load thresholds and the return to "available" are debounced so the state does not flap.
/// Reclaim actions are emitted once, on the transition that requires them.
/// Not thread-safe: the debounce timestamps are instance state, so one loop calls <see cref="Evaluate"/>. Times are
/// local wall-clock (schedule windows are local), so a DST change shifts the debounce windows by the offset.
/// </summary>
public sealed class AvailabilityEngine(AvailabilitySettings settings)
{
    private DateTime? loadExceededSince;
    private DateTime? loadClearSince;
    // Latched load verdict: changes only after the threshold has been crossed (or cleared) for LoadHysteresisSeconds.
    private bool loadBlocked;
    // Start of the current "all conditions hold" period, for StableSeconds.
    private DateTime? clearSince;
    // The reclaim action for the current owner-block episode has been emitted; reset once no job runs any more.
    private bool reclaimed;

    /// <summary>Replaced as a whole when the user saves settings (reference swap, read by the evaluating thread).</summary>
    public AvailabilitySettings Settings { get; set; } = settings;

    public AvailabilityDecision Evaluate(AvailabilitySample s)
    {
        if (!s.Connected)
            return new(WorkerState.Offline, "non connecté au coordinateur", ReclaimAction.None);

        string? ownerReason = OwnerBlock(s);
        string? loadReason = UpdateLoad(s);

        if (ownerReason != null)
        {
            clearSince = null;
            if (s.ActiveJobs == 0)
            {
                reclaimed = false;
                return new(WorkerState.Unavailable, ownerReason, ReclaimAction.None);
            }
            // The owner needs the PC while jobs run: apply the configured behaviour once.
            var action = ReclaimAction.None;
            if (!reclaimed)
            {
                reclaimed = true;
                action = Settings.Reclaim == ReclaimBehavior.ReleaseImmediately ? ReclaimAction.Release : ReclaimAction.Drain;
            }
            return Settings.Reclaim == ReclaimBehavior.ReleaseImmediately
                ? new(WorkerState.Unavailable, ownerReason, action)
                : new(WorkerState.Draining, ownerReason, action);
        }
        reclaimed = false;

        if (loadReason != null)
        {
            clearSince = null;
            // Load from other applications never kills running playback: stop taking new work only.
            return s.ActiveJobs > 0
                ? new(WorkerState.Draining, loadReason, ReclaimAction.None)
                : new(WorkerState.Unavailable, loadReason, ReclaimAction.None);
        }

        // Stabilisation only gates an idle worker; one that already runs jobs stays Available/Busy without waiting.
        clearSince ??= s.LocalNow;
        if (s.ActiveJobs == 0 && s.LocalNow - clearSince.Value < TimeSpan.FromSeconds(Settings.StableSeconds))
            return new(WorkerState.Unavailable, "stabilisation avant disponibilité", ReclaimAction.None);

        return s.ActiveJobs >= s.MaxJobs
            ? new(WorkerState.Busy, null, ReclaimAction.None)
            : new(WorkerState.Available, null, ReclaimAction.None);
    }

    private string? OwnerBlock(AvailabilitySample s)
    {
        if (!s.Enabled)
            return "désactivé par l'utilisateur";
        if (Settings.BlockOnBattery && s.OnBattery)
            return "sur batterie";
        if (Settings.Schedule.Count > 0 && !Settings.Schedule.Any(w => w.Contains(s.LocalNow)))
            return "hors plage horaire";
        if (Settings.Mode == AvailabilityMode.Automatic && s.UserIdle < TimeSpan.FromMinutes(Settings.IdleMinutes))
            return "PC utilisé";
        return null;
    }

    /// <summary>
    /// Always called (even while an owner condition blocks) so the hysteresis timers keep running. Returns the reason
    /// while the latched state is "blocked", including the cool-down after the load has dropped.
    /// </summary>
    private string? UpdateLoad(AvailabilitySample s)
    {
        double foreignCpu = Math.Max(0, s.Load.CpuPercent - s.Load.OwnCpuPercent);
        // Without per-process GPU counters (ForeignGpuPercent null) the whole-GPU figure is the best available proxy.
        string? exceeded =
            foreignCpu > Settings.MaxForeignCpuPercent ? $"CPU occupé par d'autres applications ({foreignCpu:0}%)" :
            (s.Load.ForeignGpuPercent ?? s.Load.GpuPercent) is { } gpu && gpu > Settings.MaxForeignGpuPercent ? $"GPU occupé par d'autres applications ({gpu:0}%)" :
            s.Load.MemoryPercent > Settings.MaxMemoryPercent ? $"mémoire saturée ({s.Load.MemoryPercent:0}%)" :
            null;

        var hysteresis = TimeSpan.FromSeconds(Settings.LoadHysteresisSeconds);
        if (exceeded != null)
        {
            loadClearSince = null;
            loadExceededSince ??= s.LocalNow;
            if (s.LocalNow - loadExceededSince.Value >= hysteresis)
                loadBlocked = true;
        }
        else
        {
            loadExceededSince = null;
            loadClearSince ??= s.LocalNow;
            if (s.LocalNow - loadClearSince.Value >= hysteresis)
                loadBlocked = false;
        }
        return loadBlocked ? exceeded ?? "charge élevée (retour en cours)" : null;
    }
}
