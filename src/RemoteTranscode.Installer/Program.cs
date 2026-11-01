using RemoteTranscode.Installer;

// EmbyRemoteWorker-Setup.exe [--quiet] [--dir <path>] [--no-startup] [--no-launch] [--no-kill] [--full]
// Over an existing installation it only updates it in place (same folder and options), unless --dir or --full.
// Uninstall.exe --uninstall [--quiet] [--remove-data] [--no-kill]
// Quiet mode never shows a window; failures go to %TEMP%\EmbyRemoteWorker-Setup.log and the exit code (1).

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool quiet = Has(args, "--quiet");
        bool kill = !Has(args, "--no-kill");
        Setup.Log = Log;
        ApplicationConfiguration.Initialize();
        try
        {
            if (Has(args, "--uninstall"))
                return Uninstall(quiet, kill, Has(args, "--remove-data"));

            // Already installed (and no other folder asked for): a plain update in place that keeps the folder and the
            // choices made at installation; "--full" shows the whole wizard anyway.
            if (Value(args, "--dir") is null && !Has(args, "--full") && Setup.FindExisting() is { } existing)
            {
                var update = new InstallOptions(existing.Folder, existing.StartWithWindows, existing.DesktopShortcut,
                    Launch: !Has(args, "--no-launch"), KillProcesses: kill);
                if (quiet)
                {
                    Log($"mise à jour {existing.Version} -> {Setup.Version} dans " + Setup.Install(update, new Progress<(int, string)>()));
                    return 0;
                }
                Application.Run(new UpdateForm(update, existing));
                return 0;
            }

            string folder = Value(args, "--dir") ?? Setup.InitialFolder();
            var options = new InstallOptions(folder, StartWithWindows: !Has(args, "--no-startup"), DesktopShortcut: false,
                Launch: !Has(args, "--no-launch"), KillProcesses: kill);
            if (quiet)
            {
                // A non-empty folder that is not ours gets an EmbyRemoteWorker subfolder (see ResolveInstallFolder).
                string installed = Setup.Install(options, new Progress<(int, string)>());
                Log("installation terminée dans " + installed);
                return 0;
            }
            Application.Run(new SetupForm(options));
            return 0;
        }
        catch (Exception e)
        {
            Log("ÉCHEC : " + e);
            if (!quiet)
                MessageBox.Show(e.Message, Setup.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    /// <summary>Uninstall mode of the setup exe (the Uninstall.exe of versions before 0.7.5); the worker now does it itself.</summary>
    private static int Uninstall(bool quiet, bool kill, bool removeData)
    {
        Log("désinstallation de " + Path.GetDirectoryName(Environment.ProcessPath));
        return Setup.RunUninstall(quiet, kill, removeData);
    }

    private static bool Has(string[] args, string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static string? Value(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "EmbyRemoteWorker-Setup.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
    }
}
