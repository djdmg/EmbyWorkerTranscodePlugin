using System.Runtime.InteropServices;
using RemoteTranscode.Common;

namespace RemoteTranscode.Worker;

/// <summary>
/// GPU usage from the Windows "GPU Engine" performance counters (WDDM 2.x, any vendor: NVIDIA, Intel, AMD).
/// Each counter instance is one process on one engine (3D, VideoEncode, VideoDecode, Copy...), so usage can be split
/// between this worker's ffmpeg processes and everything else. Like Task Manager, the GPU figure is the busiest
/// engine. On a PC without a GPU (or without these counters) <see cref="Sample"/> returns null and only CPU
/// thresholds apply; software encoding is then accounted for by the CPU own/foreign split.
/// </summary>
public sealed partial class GpuLoad : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhCStatusValidData = 0x00000000;
    private const uint PdhCStatusNewData = 0x00000001;

    private IntPtr query;
    private IntPtr counter;

    public bool Available { get; }

    public GpuLoad()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0)
            return;
        if (PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out counter) != 0)
        {
            PdhCloseQuery(query);
            query = IntPtr.Zero;
            return;
        }
        // Rate counters need two collections; the first one only primes the query.
        PdhCollectQueryData(query);
        Available = true;
    }

    /// <summary>
    /// Utilisation since the previous call, split by process. Null when the counters are unavailable or the instance
    /// set changed between the two PDH calls (a process started); the next sample is normally fine.
    /// </summary>
    public GpuUsage? Sample(IReadOnlySet<int> ownProcessIds)
    {
        if (!Available || PdhCollectQueryData(query) != 0)
            return null;

        // Usual PDH two-call pattern: the first call only reports the buffer size (PDH_MORE_DATA).
        uint size = 0;
        uint status = PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref size, out uint count, IntPtr.Zero);
        if (status != PdhMoreData || size == 0)
            return count == 0 ? new GpuUsage(0, 0, 0) : null;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref size, out count, buffer) != 0)
                return null;
            var entries = new List<(string Name, double Value)>((int)count);
            int itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(buffer + i * itemSize);
                if (item.CStatus is PdhCStatusValidData or PdhCStatusNewData)
                    entries.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value));
            }
            return GpuEngineCounters.Aggregate(entries, ownProcessIds);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (query != IntPtr.Zero)
            PdhCloseQuery(query);
        query = IntPtr.Zero;
    }

    // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer, then PDH_FMT_COUNTERVALUE (CStatus + 8-byte-aligned union), so Value
    // sits at offset 16 on x64 and the item is 24 bytes; sequential layout reproduces that padding.
    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public IntPtr Name;
        public uint CStatus;
        public double Value;   // union of PDH_FMT_COUNTERVALUE, read as double (PDH_FMT_DOUBLE)
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(IntPtr query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static partial uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(IntPtr query);
}
