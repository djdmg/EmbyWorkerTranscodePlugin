namespace RemoteTranscode.Coordinator;

/// <summary>
/// Which machines a waiting slice is not offered to, so a slow machine does not hold the end of a conversion while a
/// faster one idles. A machine is passed over only when the machines clearly faster than it would finish everything
/// still waiting, this slice included, sooner than it alone would finish this one slice: its help cannot shorten the
/// conversion. There is no "last N slices" threshold: with a lot left to encode that test cannot hold, so slow machines
/// keep working through the body of a conversion (at 4x against 0.5x, 120 s slices: until about 8 slices are left).
/// One slice is never offered to two machines; a slow run is not duplicated either, two runs of one slice would write
/// the same numbered chunks.
/// </summary>
public static class SliceEndgame
{
    /// <summary>Below this a measured speed means nothing (as in the progress reports).</summary>
    public const double MinSpeed = 0.05;

    /// <summary>"Clearly" sooner: within 20 %, measured speeds are too close to matter and nobody is passed over.</summary>
    public const double ClearMargin = 0.8;

    /// <param name="Id">The machine.</param>
    /// <param name="Speed">Measured source seconds per second; at or below <see cref="MinSpeed"/>, unknown.</param>
    /// <param name="SecondsLeft">Source seconds left of the slice it is encoding now.</param>
    /// <param name="Free">Encodes nothing of this conversion.</param>
    /// <param name="CanTake">May be offered the slice now: free, or about to end its current slice.</param>
    public readonly record struct Machine(string Id, double Speed, double SecondsLeft, bool Free, bool CanTake)
    {
        public bool Known => Speed > MinSpeed;
    }

    /// <param name="Preferred">
    /// Not offered the slice first. They are offered it right after if nobody else took it: a faster machine that is
    /// free may still be unable to take it (paused, refusing, full, not compatible), and nobody waits for that.
    /// </param>
    /// <param name="Firm">
    /// Not offered the slice at all for now: a clearly faster machine that is busy on this conversion will take it
    /// sooner. Decided again at every pass; a machine that leaves is no longer counted, so nothing waits for it.
    /// </param>
    public sealed record Outcome(IReadOnlySet<string> Preferred, IReadOnlySet<string> Firm);

    /// <param name="machines">Everything that could encode for this conversion.</param>
    /// <param name="waiting">Source seconds to encode of every waiting slice, in order, this one included.</param>
    /// <param name="slice">Source seconds to encode of this slice.</param>
    /// <remarks>
    /// A machine with no measured speed cannot be compared: it is never kept waiting for a busy machine, and it may be
    /// the fastest of all. It only goes after the measured machines that are free when these can already take every
    /// waiting slice. So it is measured from its first slice in the body of a conversion, and the end of a conversion is
    /// not bet on it while a known machine is free. It still takes the slice if those do not.
    /// </remarks>
    public static Outcome Decide(IReadOnlyCollection<Machine> machines, IReadOnlyList<double> waiting, double slice)
    {
        var preferred = new HashSet<string>();
        var firm = new HashSet<string>();
        int freeKnown = machines.Count(m => m.Free && m.CanTake && m.Known);
        foreach (var m in machines.Where(m => m.CanTake))
        {
            if (!m.Known)
            {
                if (m.Free && freeKnown >= waiting.Count)
                    preferred.Add(m.Id);
                continue;
            }
            double alone = (m.SecondsLeft + slice) / m.Speed;
            var faster = machines.Where(o => o.Known && o.Speed > m.Speed && o.Id != m.Id).ToList();
            if (!Sooner(faster, waiting, alone))
                continue;
            preferred.Add(m.Id);
            if (Sooner(faster.Where(o => !o.Free).ToList(), waiting, alone))
                firm.Add(m.Id);
        }
        return new Outcome(preferred, firm);
    }

    private static bool Sooner(IReadOnlyList<Machine> machines, IReadOnlyList<double> waiting, double alone) =>
        machines.Count > 0 && Finish(machines, waiting) < ClearMargin * alone;

    /// <summary>When these machines would be done with the waiting slices, each slice going to the one finishing it first.</summary>
    private static double Finish(IReadOnlyList<Machine> machines, IReadOnlyList<double> waiting)
    {
        double[] at = machines.Select(m => m.SecondsLeft / m.Speed).ToArray();
        double finish = 0;
        foreach (double seconds in waiting)
        {
            int best = 0;
            for (int i = 1; i < at.Length; i++)
                if (at[i] + seconds / machines[i].Speed < at[best] + seconds / machines[best].Speed)
                    best = i;
            at[best] += seconds / machines[best].Speed;
            finish = Math.Max(finish, at[best]);
        }
        return finish;
    }
}
