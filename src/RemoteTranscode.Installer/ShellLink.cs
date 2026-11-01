using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace RemoteTranscode.Installer;

/// <summary>
/// Creates .lnk files through the shell's own ShellLink COM object, so no external dependency (WSH, IWshRuntime
/// interop assembly) is needed.
/// </summary>
internal static class ShellLink
{
    public static void Create(string linkPath, string target, string workingDirectory, string description)
    {
        var link = (IShellLinkW)new ShellLinkCoClass();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(workingDirectory);
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            ((IPersistFile)link).Save(linkPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>Target of an existing shortcut, or null; used so uninstall only removes shortcuts it owns.</summary>
    public static string? GetTarget(string linkPath)
    {
        if (!File.Exists(linkPath))
            return null;
        var link = (IShellLinkW)new ShellLinkCoClass();
        try
        {
            ((IPersistFile)link).Load(linkPath, 0);
            var buffer = new StringBuilder(1024);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
            return buffer.ToString();
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass;

    // Method order must match the native vtable exactly.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
