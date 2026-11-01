namespace RemoteTranscode.Common;

/// <summary>
/// What a job needs from a worker: the output video encoder Emby asked for and the filters of its graphs.
/// <paramref name="ToneMapping"/>: how Emby's HDR→SDR filter is translated, chosen once per conversion by the
/// coordinator (<see cref="ToneMapper.Libplacebo"/> only for workers with that capability, never the server); playback
/// keeps <see cref="ToneMapper.Zscale"/> here, each worker choosing for itself.
/// </summary>
public sealed record JobRequirements(string VideoEncoder, string[] Filters, ToneMapper ToneMapping = ToneMapper.Zscale);

/// <summary>Decides whether a worker can run a job and how well suited it is.</summary>
public static class WorkerMatching
{
    /// <summary>
    /// Returns why the worker cannot run the job, or null when it can. Remux jobs ("copy") never reach this: the
    /// planners keep them on the server.
    /// </summary>
    public static string? Incompatibility(JobRequirements job, WorkerCapabilities worker)
    {
        if (!CanEncode(job.VideoEncoder, worker.VideoEncoders))
            return $"encodeur {job.VideoEncoder} indisponible";
        // A conversion tone mapped on the GPU goes to workers that validated it only (the server never has it).
        if (job.ToneMapping == ToneMapper.Libplacebo && EmbyFilters.NeedsToneMapping(job.Filters) && worker.GpuToneMapping?.IsLibplacebo != true)
            return "tone mapping GPU (libplacebo) indisponible";
        // Emby-only filters (supertonemap...) are translated on the worker into standard ones.
        var missing = EmbyFilters.ExpandRequirements(job.Filters, job.ToneMapping).Where(f => !worker.Filters.Contains(f)).ToArray();
        return missing.Length > 0 ? $"filtres manquants : {string.Join(", ", missing)}" : null;
    }

    /// <summary>
    /// The tone mapper a new conversion is frozen with: libplacebo when Emby asks for HDR→SDR and at least one of the
    /// given (connected) workers can run the conversion that way, zscale otherwise. Every slice then uses the same one.
    /// </summary>
    public static JobRequirements WithToneMapping(JobRequirements job, IEnumerable<WorkerCapabilities> connected)
    {
        if (!EmbyFilters.NeedsToneMapping(job.Filters))
            return job with { ToneMapping = ToneMapper.Zscale };
        var gpu = job with { ToneMapping = ToneMapper.Libplacebo };
        return connected.Any(c => Incompatibility(gpu, c) is null) ? gpu : job with { ToneMapping = ToneMapper.Zscale };
    }

    /// <summary>The software encoder itself, or one of its validated hardware equivalents.</summary>
    public static bool CanEncode(string requested, IReadOnlyCollection<string> encoders) =>
        encoders.Contains(requested) || WorkerCommandBuilder.HardwareEquivalentsOf(requested).Any(encoders.Contains);

    /// <summary>
    /// Higher is better. Keeping a playback session on its previous worker matters most (seeks restart ffmpeg and the
    /// first version never splits a session), then a hardware encoder for the requested codec, then free capacity,
    /// then a quiet machine.
    /// </summary>
    public static double Score(JobRequirements job, WorkerCapabilities caps, WorkerLoad? load, int reserved, int maxJobs, bool sameSession)
    {
        double score = 0;
        if (sameSession)
            score += 1000;
        if (HasHardwareEncoder(job.VideoEncoder, caps.VideoEncoders))
            score += 100;
        score += 50.0 * (1 - (double)reserved / Math.Max(1, maxJobs));
        if (load != null)
        {
            double foreignCpu = Math.Max(0, load.CpuPercent - load.OwnCpuPercent);
            score -= 0.2 * foreignCpu + 0.2 * (load.ForeignGpuPercent ?? 0);
        }
        return score;
    }

    public static bool HasHardwareEncoder(string requested, IReadOnlyCollection<string> encoders) =>
        WorkerCommandBuilder.HardwareEquivalentsOf(requested).Any(encoders.Contains);
}
