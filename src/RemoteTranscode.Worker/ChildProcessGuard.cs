using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RemoteTranscode.Worker;

/// <summary>
/// Windows job object with "kill on close": every ffmpeg started by the worker is assigned to it, so if the worker
/// crashes or is killed, Windows terminates its ffmpeg processes too instead of leaving orphans that keep using the
/// PC for a job the coordinator has already moved elsewhere.
/// </summary>
public static partial class ChildProcessGuard
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    // Kept open for the lifetime of the process; the OS closes it (and kills the children) when we exit.
    private static readonly IntPtr Job = Create();

    /// <summary>
    /// Best effort (the result is not checked): the process runs briefly before it is assigned, and on a failure it is
    /// simply not guarded. Nested job objects (Windows 8+) let this work even if the worker itself runs inside a job.
    /// </summary>
    public static void Attach(Process process)
    {
        if (Job != IntPtr.Zero)
            AssignProcessToJobObject(Job, process.Handle);
    }

    private static IntPtr Create()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            return IntPtr.Zero;
        var info = new JobObjectExtendedLimitInformationStruct
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        int size = Marshal.SizeOf<JobObjectExtendedLimitInformationStruct>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            return SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size) ? job : IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    // Managed mirrors of JOBOBJECT_BASIC_LIMIT_INFORMATION / JOBOBJECT_EXTENDED_LIMIT_INFORMATION; the field order and
    // pointer-sized members must match the native layout exactly (x64 build).
    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationStruct
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateJobObject(IntPtr attributes, string? name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
