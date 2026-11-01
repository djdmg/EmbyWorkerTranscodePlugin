using RemoteTranscode.Common;

namespace RemoteTranscode.Coordinator;

/// <summary>
/// HDR → SDR tone mapper of a conversion whose first slice has not started yet. Until then the choice is provisional
/// and taken again at every scheduling pass from the workers connected at that moment; it becomes final when the first
/// run starts (see EncodeQueue), so the slices of one file are never tone mapped two ways.
/// </summary>
public static class ToneMappingDecision
{
    /// <param name="Requirements">The conversion's requirements with the tone mapper chosen now.</param>
    /// <param name="Wait">
    /// True: start nothing yet (neither on a worker nor on the server), a GPU-capable worker may be reconnecting.
    /// </param>
    public readonly record struct Outcome(JobRequirements Requirements, bool Wait);

    /// <summary>
    /// libplacebo when a connected worker can run the conversion that way (<see cref="WorkerMatching.WithToneMapping"/>);
    /// otherwise zscale, but held back (<see cref="Outcome.Wait"/>) while the coordinator started less than
    /// <paramref name="grace"/> ago and some paired worker is not connected yet: right after an Emby restart, Emby
    /// relaunches its conversions within a second, before the workers have reconnected, and a conversion started then in
    /// zscale stayed in zscale (twice as slow) for the whole episode. Past the grace period, zscale starts at once, as
    /// when the GPU worker is simply off. Without HDR there is nothing to choose and nothing to wait for.
    /// </summary>
    /// <param name="sinceStart">Time since the coordinator started.</param>
    /// <param name="workersMissing">
    /// Whether some paired (not revoked) worker is not connected; only asked when it matters (it reads the worker store).
    /// </param>
    public static Outcome Decide(JobRequirements job, IEnumerable<WorkerCapabilities> connected, TimeSpan sinceStart, TimeSpan grace,
        Func<bool> workersMissing)
    {
        var chosen = WorkerMatching.WithToneMapping(job, connected);
        bool wait = EmbyFilters.NeedsToneMapping(job.Filters) && chosen.ToneMapping != ToneMapper.Libplacebo
            && sinceStart < grace && workersMissing();
        return new Outcome(chosen, wait);
    }
}
