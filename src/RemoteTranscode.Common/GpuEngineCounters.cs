namespace RemoteTranscode.Common;

/// <summary>GPU usage in percent of the busiest engine: whole machine, this worker's processes, everything else.</summary>
public readonly record struct GpuUsage(double Total, double Own, double Foreign);

/// <summary>Aggregates Windows "GPU Engine" counter instances (one per process and engine).</summary>
public static class GpuEngineCounters
{
    /// <summary>
    /// Instance names look like "pid_1234_luid_0x0_0xC3E5_phys_0_eng_0_engtype_3D". Values are summed per physical
    /// engine (luid + phys + eng) for each population; the result is the busiest engine of each population.
    /// </summary>
    public static GpuUsage Aggregate(IEnumerable<(string Name, double Value)> entries, IReadOnlySet<int> ownProcessIds)
    {
        var total = new Dictionary<string, double>();
        var own = new Dictionary<string, double>();
        var foreign = new Dictionary<string, double>();
        // Several processes share one engine: summing per engine then taking the max mirrors Task Manager's GPU column.
        foreach (var (name, value) in entries)
        {
            int luid = name.IndexOf("_luid_", StringComparison.Ordinal);
            if (!name.StartsWith("pid_", StringComparison.Ordinal) || luid < 0)
                continue;
            if (!int.TryParse(name.AsSpan(4, luid - 4), out int pid))
                continue;
            int engType = name.IndexOf("_engtype_", StringComparison.Ordinal);
            string engine = engType > luid ? name[(luid + 1)..engType] : name[(luid + 1)..];
            Add(total, engine, value);
            Add(ownProcessIds.Contains(pid) ? own : foreign, engine, value);
        }
        return new GpuUsage(Busiest(total), Busiest(own), Busiest(foreign));

        static void Add(Dictionary<string, double> d, string key, double v) => d[key] = d.GetValueOrDefault(key) + v;
        static double Busiest(Dictionary<string, double> d) => d.Count == 0 ? 0 : Math.Round(Math.Min(100, d.Values.Max()), 1);
    }
}
