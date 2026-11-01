using System.Diagnostics;
using System.Runtime.InteropServices;
using RemoteTranscode.Common;

namespace RemoteTranscode.Worker;

/// <summary>
/// Machine load sampled between two calls, plus the share used by this worker's own ffmpeg processes so the
/// availability logic can tell "the PC is busy" from "our transcodes are busy".
/// Not thread-safe: deltas are kept between calls, so a single caller (the availability loop) samples it.
/// </summary>
public sealed partial class SystemLoad : IDisposable
{
    private readonly GpuLoad gpu = new();
    private long lastIdle, lastKernel, lastUser;
    private TimeSpan lastOwnCpu;
    private DateTime lastSample = DateTime.UtcNow;

    public WorkerLoad Sample(IEnumerable<Process> ownProcesses)
    {
        // Kernel time includes idle time, hence busy = (kernel + user) - idle. The first call averages since boot.
        GetSystemTimes(out long idle, out long kernel, out long user);
        long idleDelta = idle - lastIdle, totalDelta = (kernel - lastKernel) + (user - lastUser);
        double cpu = totalDelta > 0 ? 100.0 * (totalDelta - idleDelta) / totalDelta : 0;
        (lastIdle, lastKernel, lastUser) = (idle, kernel, user);

        var ownCpu = TimeSpan.Zero;
        var ownPids = new HashSet<int>();
        foreach (var p in ownProcesses)
        {
            try
            {
                ownCpu += p.TotalProcessorTime;
                ownPids.Add(p.Id);
            }
            catch (InvalidOperationException) { }   // process already gone
        }
        // A finished ffmpeg drops out of the sum; never report a negative share. (For that one sample the remaining jobs'
        // own share reads as 0, which load hysteresis absorbs.)
        if (ownCpu < lastOwnCpu)
            lastOwnCpu = ownCpu;
        var now = DateTime.UtcNow;
        double wall = (now - lastSample).TotalMilliseconds * Environment.ProcessorCount;
        double own = wall > 0 ? Math.Clamp(100.0 * (ownCpu - lastOwnCpu).TotalMilliseconds / wall, 0, 100) : 0;
        (lastOwnCpu, lastSample) = (ownCpu, now);

        var mem = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        GlobalMemoryStatusEx(ref mem);
        var g = gpu.Sample(ownPids);
        return new WorkerLoad(Math.Round(cpu, 1), g?.Total, mem.MemoryLoad, Math.Round(own, 1), g?.Own, g?.Foreign);
    }

    public bool HasGpuCounters => gpu.Available;

    public void Dispose() => gpu.Dispose();

    public static long TotalMemory()
    {
        var mem = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref mem) ? (long)mem.TotalPhys : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idle, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
