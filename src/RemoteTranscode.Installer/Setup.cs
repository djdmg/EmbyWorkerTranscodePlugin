using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace RemoteTranscode.Installer;

internal sealed record InstallOptions(string Folder, bool StartWithWindows, bool DesktopShortcut, bool Launch, bool KillProcesses);

/// <summary>
/// Install / uninstall logic, shared by the wizard and the quiet command line. Everything is per-user
/// (LOCALAPPDATA, APPDATA, HKCU) so no administrator rights are ever needed.
/// </summary>
internal static class Setup
{
    public const string AppName = "Emby Remote Worker";
    public const string AppExe = "EmbyRemoteWorker.exe";
    public const string UninstallExe = "Uninstall.exe";
    /// <summary>Relative paths of every file the installer wrote; uninstall deletes those and nothing else.</summary>
    public const string ManifestFile = "install-manifest.txt";
    private const string DedicatedFolderName = "EmbyRemoteWorker";
    private const string ProcessName = "EmbyRemoteWorker";
    // Same key/value/format as WorkerHost.StartWithWindows in the app, so the app's own checkbox stays in sync.
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "EmbyRemoteWorker";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\EmbyRemoteWorker";

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "EmbyRemoteWorker");

    /// <summary>
    /// Name of the running worker's kernel objects (same rule as the worker's Program.InstanceName: one instance per
    /// configuration folder); "-exit" asks it to quit cleanly.
    /// </summary>
    public static string InstanceName =>
        @"Local\EmbyRemoteWorker-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(UserDataFolder.ToLowerInvariant())))[..12];

    public static string UserDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmbyRemoteWorker");

    private static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

    private static string DesktopShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    public static string Version
    {
        get
        {
            string? v = typeof(Setup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return v?.Split('+')[0] ?? "0.0.0";
        }
    }

    /// <summary>An installation of this user found on the PC, with the choices it was made with.</summary>
    public sealed record ExistingInstall(string Folder, string? Version, bool StartWithWindows, bool DesktopShortcut, bool Running);

    /// <summary>
    /// The current installation (registered folder that still holds the app), so an upgrade can keep its folder and
    /// options without asking; null for a first installation.
    /// </summary>
    public static ExistingInstall? FindExisting()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        if (key?.GetValue("InstallLocation") is not string folder || folder.Length == 0 || !File.Exists(Path.Combine(folder, AppExe)))
            return null;
        bool startup;
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey))
            startup = run != null && RunValuePointsInside(run, folder);
        bool desktop = ShellLink.GetTarget(DesktopShortcut) is { } target && IsInside(target, folder);
        var running = RunningFrom(folder);
        running.ForEach(p => p.Dispose());
        return new ExistingInstall(folder, key.GetValue("DisplayVersion") as string, startup, desktop, running.Count > 0);
    }

    /// <summary>Folder of a previous installation (upgrade keeps it), otherwise the default one.</summary>
    public static string InitialFolder()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key?.GetValue("InstallLocation") is string s && s.Length > 0 ? s : DefaultFolder;
    }

    /// <summary>Zip of the published worker app, embedded at build time (-p:PayloadZip); null in a plain solution build.</summary>
    public static Stream? OpenPayload() => typeof(Setup).Assembly.GetManifestResourceStream("payload.zip");

    public static string? ReadFfmpegLicense()
    {
        using var payload = OpenPayload();
        if (payload is null)
            return null;
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals("ffmpeg/LICENSE.txt", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return null;
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Folder the files really go to: the chosen one when it is ours (named EmbyRemoteWorker, holds a previous
    /// installation) or empty, otherwise an EmbyRemoteWorker subfolder, so the files never mix with someone else's
    /// (e.g. --dir D:\Apps installs into D:\Apps\EmbyRemoteWorker).
    /// </summary>
    public static string ResolveInstallFolder(string chosen)
    {
        // TrimEndingDirectorySeparator keeps a drive root ("D:\") valid, unlike TrimEnd.
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(chosen));
        bool ours = string.Equals(Path.GetFileName(folder), DedicatedFolderName, StringComparison.OrdinalIgnoreCase)
            || File.Exists(Path.Combine(folder, ManifestFile))
            || File.Exists(Path.Combine(folder, AppExe));
        if (ours || !Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any())
            return folder;
        return Path.Combine(folder, DedicatedFolderName);
    }

    /// <summary>Installs or upgrades; returns the folder actually used (see <see cref="ResolveInstallFolder"/>).</summary>
    public static string Install(InstallOptions o, IProgress<(int Percent, string Text)> progress)
    {
        string folder = ResolveInstallFolder(o.Folder);
        using var payload = OpenPayload() ?? throw new InvalidOperationException("Ce programme d'installation ne contient pas l'application (payload absent).");

        // Upgrade over a running worker: its exe and DLLs are locked, so it is closed first. Without that (--no-kill),
        // extraction fails part-way on the first locked file, leaving files of both versions.
        if (o.KillProcesses)
        {
            progress.Report((2, "Fermeture d'Emby Remote Worker…"));
            CloseRunningApp(folder);
        }

        progress.Report((5, "Copie des fichiers…"));
        // The worker itself uninstalls (EmbyRemoteWorker.exe --uninstall): the setup never copies itself anywhere.
        // An Uninstall.exe left by an older version (a copy of its setup) goes away.
        string oldUninstaller = Path.Combine(folder, UninstallExe);
        using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
        {
            var files = new List<(ZipArchiveEntry Entry, string Target)>();
            foreach (var entry in zip.Entries)
            {
                string target = Path.GetFullPath(Path.Combine(folder, entry.FullName));
                // Zip-slip guard: the payload is ours, but never write outside the chosen folder.
                if (!IsInside(target, folder))
                    throw new InvalidDataException($"Entrée d'archive invalide : {entry.FullName}");
                if (!(entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')))
                    files.Add((entry, target));
            }

            // Upgrade: a file still locked (worker started from elsewhere, --no-kill, antivirus) would make extraction
            // fail half-way and leave files of both versions. Check everything first, before changing anything.
            var targets = files.Select(f => f.Target).ToList();
            EnsureWritable(targets);

            Directory.CreateDirectory(folder);
            // Written before the copy so even an interrupted installation can be uninstalled cleanly. Entries of a
            // previous version are kept: their files stay there until the uninstall.
            WriteManifest(folder, targets.Select(t => Path.GetRelativePath(folder, t)));

            long total = Math.Max(1, files.Sum(f => f.Entry.Length));
            long done = 0;
            foreach (var (entry, target) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                progress.Report((5 + (int)(80 * done / total), "Copie de " + entry.FullName.Replace('/', '\\') + "…"));
                entry.ExtractToFile(target, overwrite: true);
                done += entry.Length;
            }
        }

        try { File.Delete(oldUninstaller); } catch (IOException) { } catch (UnauthorizedAccessException) { }

        progress.Report((90, "Raccourcis…"));
        string exe = Path.Combine(folder, AppExe);
        ShellLink.Create(StartMenuShortcut, exe, folder, AppName);
        if (o.DesktopShortcut)
            ShellLink.Create(DesktopShortcut, exe, folder, AppName);
        else
            DeleteShortcutIfOwned(DesktopShortcut, folder);

        progress.Report((94, "Démarrage avec Windows…"));
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (o.StartWithWindows)
                run.SetValue(RunValue, $"\"{exe}\" --tray");
            else if (RunValuePointsInside(run, folder))
                // Only drop our own entry: a value pointing at another copy of the app is not ours to remove.
                run.DeleteValue(RunValue, false);
        }

        // HKCU uninstall entry: listed in "Apps & features" for this user only, no elevation needed.
        progress.Report((97, "Enregistrement dans Applications et fonctionnalités…"));
        long sizeKb = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024;
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", "Emby Remote Transcode");
            key.SetValue("InstallLocation", folder);
            key.SetValue("DisplayIcon", exe);
            key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --quiet");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        }

        if (o.Launch)
        {
            progress.Report((99, "Lancement d'Emby Remote Worker…"));
            Launch(exe, folder, o.StartWithWindows);
        }
        progress.Report((100, "Installation terminée."));
        return folder;
    }

    /// <summary>Messages worth keeping for a quiet run (the setup writes them to its log file).</summary>
    public static Action<string> Log { get; set; } = _ => { };

    /// <summary>
    /// Starts the worker after an installation. From session 0 (setup run over OpenSSH, a service...) a plain start
    /// would give a worker without desktop nor tray icon: it is started in this user's interactive session through a
    /// one-shot scheduled task (interactive token, no password nor administrator rights needed), or not at all when
    /// the user has no open session.
    /// </summary>
    private static void Launch(string exe, string folder, bool startsWithWindows)
    {
        using (var self = Process.GetCurrentProcess())
        {
            if (self.SessionId != 0)
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = folder });
                return;
            }
        }
        string later = startsWithWindows ? "il démarrera à la prochaine ouverture de session" : "lancez-le depuis le menu Démarrer";
        if (!HasInteractiveSession())
        {
            Log($"installation hors session interactive et aucune session ouverte : worker non lancé ({later})");
            return;
        }
        try
        {
            if (LaunchInInteractiveSession(exe, folder))
                Log("worker lancé dans la session interactive de l'utilisateur");
            else
                Log($"le worker n'a pas pu être lancé dans la session interactive ({later})");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log($"lancement dans la session interactive en échec : {e.Message} ({later})");
        }
    }

    /// <summary>True when this user has a desktop session open (its shell runs in a session other than 0).</summary>
    private static bool HasInteractiveSession()
    {
        string? me = CurrentUserSid();
        if (me is null)
            return false;
        foreach (var p in Process.GetProcessesByName("explorer").Concat(Process.GetProcessesByName("sihost")))
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != 0 && QueryProcess(p.Id).Owner is { } owner && string.Equals(owner, me, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Registers a task for this user with an interactive token (runs in its desktop session), runs it, waits for the
    /// worker to show up, then deletes the task. The XML form sets what schtasks' switches cannot: normal priority
    /// (tasks default to below normal, inherited by ffmpeg), no 72 h time limit, allowed on battery.
    /// </summary>
    private static bool LaunchInInteractiveSession(string exe, string folder)
    {
        string sid = CurrentUserSid() ?? throw new InvalidOperationException("utilisateur courant inconnu");
        string task = "EmbyRemoteWorker-Launch-" + Guid.NewGuid().ToString("N")[..8];
        string xmlPath = Path.Combine(Path.GetTempPath(), task + ".xml");
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Lancement unique de {AppName} après sa mise à jour (supprimée aussitôt).</Description></RegistrationInfo>
              <Principals>
                <Principal id="Author">
                  <UserId>{sid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{System.Security.SecurityElement.Escape(exe)}</Command>
                  <WorkingDirectory>{System.Security.SecurityElement.Escape(folder)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
        File.WriteAllText(xmlPath, xml, System.Text.Encoding.Unicode);
        try
        {
            if (!SchTasks($"/create /tn \"{task}\" /xml \"{xmlPath}\" /f"))
                return false;
            try
            {
                if (!SchTasks($"/run /tn \"{task}\""))
                    return false;
                // The task is only deleted once the worker runs (deleting it does not stop its process).
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                while (DateTime.UtcNow < deadline)
                {
                    var running = RunningFrom(folder);
                    running.ForEach(p => p.Dispose());
                    if (running.Count > 0)
                        return true;
                    Thread.Sleep(250);
                }
                return false;
            }
            finally
            {
                SchTasks($"/delete /tn \"{task}\" /f");
            }
        }
        finally
        {
            try { File.Delete(xmlPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static bool SchTasks(string arguments)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("schtasks introuvable");
        var stdout = p.StandardOutput.ReadToEndAsync();
        string stderr = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(30_000))
        {
            try { p.Kill(); } catch (InvalidOperationException) { }
            Log($"schtasks {arguments.Split(' ')[0]} : délai dépassé");
            return false;
        }
        if (p.ExitCode != 0)
            Log($"schtasks {arguments.Split(' ')[0]} : code {p.ExitCode} {stderr.Trim()} {stdout.Result.Trim()}");
        return p.ExitCode == 0;
    }

    /// <summary>Throws, with nothing modified, if any existing target file cannot be overwritten.</summary>
    private static void EnsureWritable(IEnumerable<string> targets)
    {
        foreach (string target in targets)
        {
            if (!File.Exists(target))
                continue;
            try
            {
                // Exclusive write access is exactly what the overwrite needs (fails on a running exe or a loaded DLL).
                using var _ = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Le fichier {target} est utilisé par un autre programme ou protégé en écriture. " +
                                      "Fermez Emby Remote Worker puis relancez l'installation. Aucun fichier n'a été modifié.", e);
            }
        }
    }

    private static void WriteManifest(string folder, IEnumerable<string> relativePaths)
    {
        var entries = new SortedSet<string>(ReadManifest(folder), StringComparer.OrdinalIgnoreCase);
        entries.UnionWith(relativePaths);
        entries.Add(ManifestFile);
        File.WriteAllLines(Path.Combine(folder, ManifestFile), entries);
    }

    private static IEnumerable<string> ReadManifest(string folder)
    {
        string path = Path.Combine(folder, ManifestFile);
        return File.Exists(path) ? File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToList() : [];
    }

    /// <summary>
    /// The interactive uninstall of the installation this executable runs from (EmbyRemoteWorker.exe --uninstall, or
    /// the Uninstall.exe of older versions): confirmation, optional removal of the user data, then the files.
    /// </summary>
    public static int RunUninstall(bool quiet, bool kill, bool removeData)
    {
        // The executable always lives in the installation folder it removes.
        string folder = Path.GetDirectoryName(Environment.ProcessPath)!;
        if (!quiet)
        {
            if (MessageBox.Show($"Désinstaller {AppName} de\n{folder} ?", AppName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return 0;
            // Data (pairing, configuration, logs) is kept by default so a reinstall does not need a new pairing.
            removeData = Directory.Exists(UserDataFolder) && MessageBox.Show(
                $"Supprimer aussi les données utilisateur (configuration, association, journaux) ?\n{UserDataFolder}",
                AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }
        bool wholeFolder = Uninstall(folder, kill, removeData);
        if (!quiet)
            MessageBox.Show($"{AppName} a été désinstallé.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        ScheduleFolderDeletion(folder, wholeFolder);
        return 0;
    }

    /// <summary>
    /// Removes the installation that contains the running executable (the worker, or an older Uninstall.exe): only the
    /// files listed in its manifest, never other content of the folder. The running executable itself and the
    /// then-empty folder are removed by a detached cmd (<see cref="ScheduleFolderDeletion"/>), since a running exe
    /// cannot delete its own file. Returns true for an installation made before manifests existed in a dedicated
    /// EmbyRemoteWorker folder, which is then removed whole as before.
    /// </summary>
    public static bool Uninstall(string folder, bool killProcesses, bool removeUserData)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        // Our current directory would otherwise keep the folder locked for rmdir.
        Environment.CurrentDirectory = Path.GetTempPath();

        if (killProcesses)
            CloseRunningApp(folder);

        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
        {
            if (run is not null && RunValuePointsInside(run, folder))
                run.DeleteValue(RunValue, false);
        }
        DeleteShortcutIfOwned(StartMenuShortcut, folder);
        DeleteShortcutIfOwned(DesktopShortcut, folder);

        bool registeredHere;
        using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
            registeredHere = key?.GetValue("InstallLocation") is string loc && SamePath(loc, folder);
        if (registeredHere)
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);

        if (removeUserData && Directory.Exists(UserDataFolder))
        {
            try { Directory.Delete(UserDataFolder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        if (!File.Exists(Path.Combine(folder, ManifestFile)))
            return string.Equals(Path.GetFileName(folder), DedicatedFolderName, StringComparison.OrdinalIgnoreCase);
        RemoveInstalledFiles(folder);
        return false;
    }

    private static void RemoveInstalledFiles(string folder)
    {
        string self = Path.GetFullPath(Environment.ProcessPath!);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relative in ReadManifest(folder))
        {
            string path;
            try { path = Path.GetFullPath(Path.Combine(folder, relative)); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            // The manifest is plain text in a user-writable folder: never follow it outside the installation.
            if (!IsInside(path, folder) || string.Equals(path, self, StringComparison.OrdinalIgnoreCase))
                continue;
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            for (string? dir = Path.GetDirectoryName(path); dir != null && IsInside(dir, folder); dir = Path.GetDirectoryName(dir))
                directories.Add(dir);
        }
        // Deepest first; a directory still holding other files stays.
        foreach (string dir in directories.OrderByDescending(d => d.Length))
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Last step of an uninstall, called just before exiting (after any final message box): the detached cmd waits
    /// ~2 s for this process to end, then deletes Uninstall.exe and the folder if nothing else is left in it.
    /// <paramref name="wholeFolder"/> (legacy dedicated folder without manifest, see <see cref="Uninstall"/>) removes
    /// it with its content.
    /// </summary>
    public static void ScheduleFolderDeletion(string folder, bool wholeFolder)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (Directory.Exists(folder))
        {
            // "ping -n 3" is cmd's portable 2-second wait. Plain "rmdir" only removes an empty folder.
            string self = Path.GetFullPath(Environment.ProcessPath!);
            string delete = wholeFolder
                ? $"rmdir /s /q \"{folder}\""
                : $"del /f /q \"{self}\" 2>nul & rmdir \"{folder}\" 2>nul";
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 >nul & {delete}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath(),
            });
        }
    }

    /// <summary>
    /// Closes EmbyRemoteWorker instances of this user started from <paramref name="folder"/> so their files can be
    /// replaced, then the bundled ffmpeg/ffprobe still running from that folder (a run outliving its worker would
    /// keep ffmpeg.exe locked and the update would stop); programs running from any other folder are left alone.
    /// </summary>
    private static void CloseRunningApp(string folder)
    {
        CloseWorkers(folder);
        foreach (string name in BundledTools)
            Kill(RunningFrom(folder, name));
    }

    // The bundled FFmpeg programs (ffmpeg\ folder of the installation).
    private static readonly string[] BundledTools = ["ffmpeg", "ffprobe"];

    /// <summary>
    /// Session-independent twin of the "-exit" event (Global\ namespace, no privilege needed for an event): lets a setup
    /// run from another session of the same user (OpenSSH runs in session 0) ask the tray worker of session 1 to quit.
    /// </summary>
    public static string GlobalExitEventName => @"Global\" + InstanceName[@"Local\".Length..] + "-exit";

    private static void CloseWorkers(string folder)
    {
        // First the way the tray's "Quitter" does it (the running worker hands its jobs back), then, for an older
        // version without that signal or one that does not answer, a forced stop. The session-local event reaches a
        // worker of this session; otherwise the Global\ one reaches a worker of another session of this user (only one
        // is set: a leftover signaled event could make the relaunched worker quit at once).
        if (SignalExit(InstanceName + "-exit") || SignalExit(GlobalExitEventName))
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                var still = RunningFrom(folder);
                still.ForEach(p => p.Dispose());
                if (still.Count == 0)
                    return;
                Thread.Sleep(250);
            }
        }
        Kill(RunningFrom(folder));
    }

    private static bool SignalExit(string name)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(name, out var exit))
                return false;
            using (exit)
                exit.Set();
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    private static void Kill(List<Process> processes)
    {
        foreach (var p in processes)
        {
            using (p)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(10_000);
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                }
            }
        }
    }

    /// <summary>
    /// Processes named <paramref name="name"/> (the worker by default) of this user, in any of its sessions (a setup
    /// run over OpenSSH is in session 0, the tray worker in the interactive one), started from <paramref name="folder"/>;
    /// the caller disposes them. Processes of other users, or whose owner/path cannot be read, are never returned.
    /// </summary>
    private static List<Process> RunningFrom(string folder, string name = ProcessName)
    {
        using var current = Process.GetCurrentProcess();
        string? me = CurrentUserSid();
        var found = new List<Process>();
        foreach (var p in Process.GetProcessesByName(name))
        {
            // The worker uninstalling itself is not one to close.
            if (me != null && p.Id != current.Id && QueryProcess(p.Id) is { Owner: { } owner, Path: { } path }
                && string.Equals(owner, me, StringComparison.OrdinalIgnoreCase) && IsInside(path, folder))
                found.Add(p);
            else
                p.Dispose();
        }
        return found;
    }

    private static string? CurrentUserSid()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return identity.User?.Value;
    }

    /// <summary>
    /// Executable path and owner SID of a process, read with the least access right (query-limited), which works across
    /// sessions for this user's processes; null fields when access is denied or the process is gone.
    /// </summary>
    private static (string? Path, string? Owner) QueryProcess(int pid)
    {
        const int ProcessQueryLimitedInformation = 0x1000;
        const int TokenQuery = 0x0008;
        using var process = Native.OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid)
            return (null, null);
        string? path = null, owner = null;
        var buffer = new char[32768];
        int size = buffer.Length;
        if (Native.QueryFullProcessImageName(process, 0, buffer, ref size))
            path = new string(buffer, 0, size);
        if (Native.OpenProcessToken(process, TokenQuery, out var token))
        {
            using (token)
            {
                try
                {
                    using var identity = new System.Security.Principal.WindowsIdentity(token.DangerousGetHandle());
                    owner = identity.User?.Value;
                }
                catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
                {
                }
            }
        }
        return (path, owner);
    }

    private static class Native
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(int access, bool inherit, int processId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            EntryPoint = "QueryFullProcessImageNameW")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageName(Microsoft.Win32.SafeHandles.SafeProcessHandle process, int flags,
            [System.Runtime.InteropServices.Out] char[] buffer, ref int size);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(Microsoft.Win32.SafeHandles.SafeProcessHandle process, int access,
            out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token);
    }

    private static bool RunValuePointsInside(RegistryKey run, string folder) =>
        run.GetValue(RunValue) is string cmd && ExtractExe(cmd) is { } exe && IsInside(exe, folder);

    private static string? ExtractExe(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        int space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }

    private static void DeleteShortcutIfOwned(string link, string folder)
    {
        string? target = ShellLink.GetTarget(link);
        if (target is not null && IsInside(target, folder))
            File.Delete(link);
    }

    /// <summary>True when <paramref name="path"/> is strictly below <paramref name="folder"/> (trailing separator compared, so "X2" is not inside "X").</summary>
    private static bool IsInside(string path, string folder)
    {
        try
        {
            return Path.GetFullPath(path).StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
