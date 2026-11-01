using System.Runtime.InteropServices;
using RemoteTranscode.Common;

namespace RemoteTranscode.Coordinator;

/// <summary>
/// Server-side file checks: a path is usable only if, after resolving every symbolic link, it is still inside one of
/// the configured roots (whose own links are resolved too, e.g. /var/packages/EmbyServer/var).
/// </summary>
public static partial class ServerFiles
{
    /// <summary>
    /// Absolute path with every link resolved (realpath), or null when it does not exist. The check and the later open
    /// are separate steps: a link swapped in between is not caught, which only matters for someone able to write
    /// inside the allowed roots.
    /// </summary>
    public static string? Canonical(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Development and tests on Windows: "C:\x\y" becomes "/C:/x/y" so the Unix-style root checks still apply.
            try { return "/" + Path.GetFullPath(path).Replace('\\', '/'); }
            catch (Exception) { return null; }
        }
        IntPtr result = RealPath(path, IntPtr.Zero);
        if (result == IntPtr.Zero)
            return null;
        try
        {
            return Marshal.PtrToStringUTF8(result);
        }
        finally
        {
            Free(result);
        }
    }

    public static bool IsAllowed(string path, CoordinatorOptions options)
    {
        string? real = Canonical(path);
        if (real is null)
            return false;
        foreach (string root in options.AllowedRoots)
        {
            string? realRoot = Canonical(root);
            if (realRoot != null && EmbyJobPlanner.IsUnder(real, realRoot))
                return true;
        }
        return false;
    }

    /// <summary>Planner callback: file size, or null for files outside the roots (the planner then rejects the job).</summary>
    public static Func<string, long?> SizeIfAllowed(CoordinatorOptions options) => path =>
        IsAllowed(path, options) && File.Exists(path) ? new FileInfo(path).Length : null;

    /// <summary>Planner callback: files of an allowed folder (capped at 64), e.g. to find external subtitles.</summary>
    public static Func<string, IEnumerable<string>> ListIfAllowed(CoordinatorOptions options) => dir =>
        IsAllowed(dir, options) && Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).Where(f => IsAllowed(f, options)).Take(64).ToArray()
            : [];

    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr RealPath(string path, IntPtr resolved);

    [LibraryImport("libc", EntryPoint = "free")]
    private static partial void Free(IntPtr ptr);
}
