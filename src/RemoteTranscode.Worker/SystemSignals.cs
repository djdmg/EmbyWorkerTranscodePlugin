using System.Runtime.InteropServices;

namespace RemoteTranscode.Worker;

/// <summary>Owner presence signals from Windows: time since last keyboard/mouse input and power source.</summary>
public static partial class SystemSignals
{
    /// <summary>
    /// Time since the last keyboard/mouse input in this interactive session (GetLastInputInfo is per session, which is
    /// why the worker runs as a tray app and not as a service). Zero on failure, i.e. "PC in use", the safe side.
    /// </summary>
    public static TimeSpan UserIdle()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
            return TimeSpan.Zero;
        // Both values are 32-bit millisecond tick counts; unsigned subtraction handles the 49-day wrap.
        uint idleMs = unchecked((uint)Environment.TickCount - info.Time);
        return TimeSpan.FromMilliseconds(idleMs);
    }

    public static bool OnBattery()
    {
        // ACLineStatus: 0 = offline (battery), 1 = online, 255 = unknown (desktop PCs report online).
        return GetSystemPowerStatus(out var status) && status.AcLineStatus == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);
}
