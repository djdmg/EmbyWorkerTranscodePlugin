using System.Runtime.InteropServices;
using RemoteTranscode.Worker;
using RemoteTranscode.Worker.Ui;

// EmbyRemoteWorker.exe                     notification-area application (default, also "--tray")
// EmbyRemoteWorker.exe pair <code>         store a pairing code (shown on the plugin page, "Associer un worker")
// EmbyRemoteWorker.exe probe               measure ffmpeg/hardware capabilities and exit
// EmbyRemoteWorker.exe load [--encode X]   print machine/own load every second (diagnostic)
// EmbyRemoteWorker.exe run                 headless worker in a console
// EmbyRemoteWorker.exe release             "Libérer le PC" in the running instance (no confirmation dialog)
// Common options: --ffmpeg <path> --max-jobs N

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Registered in "Apps & features" as "EmbyRemoteWorker.exe --uninstall [--quiet]".
        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            ApplicationConfiguration.Initialize();
            return RemoteTranscode.Installer.Setup.RunUninstall(args.Contains("--quiet", StringComparer.OrdinalIgnoreCase), kill: true,
                removeData: args.Contains("--remove-data", StringComparer.OrdinalIgnoreCase));
        }
        string command = args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal) ? "tray" : args[0];
        if (command == "tray")
            return RunTray(args);
        if (command == "release")
        {
            // Same action as the tray's "Libérer le PC" button, for scripts and tests.
            bool running = EventWaitHandle.TryOpenExisting(InstanceName() + "-release", out var release);
            release?.Set();
            return running ? 0 : 1;
        }
        // GUI-subsystem executable: reuse the parent console for command-line use.
        AttachConsole(-1);
        return RunCommandAsync(command, args).GetAwaiter().GetResult();
    }

    private static int RunTray(string[] args)
    {
        string instance = InstanceName();
        // Named kernel objects in the session-local namespace: the mutex enforces a single tray instance, the auto-reset
        // events let a second launch (or "release") signal the running one.
        using var single = new Mutex(true, instance, out bool first);
        using var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, instance + "-show");
        using var releaseRequest = new EventWaitHandle(false, EventResetMode.AutoReset, instance + "-release");
        // Set by the setup before an update or by the uninstall: quit like the tray's "Quitter" (jobs handed back).
        using var exitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, instance + "-exit");
        // Same request from a setup run in another session of this user (e.g. over OpenSSH, session 0), which cannot
        // see the session-local one. Its default security only admits this user; a failure only loses that path.
        using var globalExitRequest = TryCreateEvent(@"Global\" + instance[@"Local\".Length..] + "-exit");
        if (!first)
        {
            // Already running: bring its window up instead of starting a second worker.
            showRequest.Set();
            return 0;
        }
        ApplicationConfiguration.Initialize();
        var logger = new Logger();
        logger.Log("démarrage de l'interface");
        var host = new WorkerHost(logger);
        var updater = new WorkerUpdater(host, typeof(WorkerHost).Assembly.GetName().Version ?? new Version(0, 0, 0), logger.Log);
        host.Updater = updater;
        // "--show [tab]" opens the window at start (0 status, 1 settings, 2 logs).
        int? tab = Array.IndexOf(args, "--show") is int i and >= 0 ? int.TryParse(Arg(args, "--show"), out int t) ? t : 0 : null;
        var tray = new TrayApp(host, tab);
        var listener = new Thread(() =>
        {
            while (showRequest.WaitOne())
                tray.ShowFromAnotherInstance();
        }) { IsBackground = true, Name = "show-request" };
        listener.Start();
        // Dedicated background thread blocked on the event; the release itself runs here, off the UI thread, so an
        // exception thrown by it would terminate the process: it is caught and logged.
        var releaser = new Thread(() =>
        {
            while (releaseRequest.WaitOne())
            {
                logger.Log("libération demandée en ligne de commande");
                try
                {
                    host.ReleaseNowAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    logger.Log($"libération en échec : {ex.Message}");
                }
            }
        }) { IsBackground = true, Name = "release-request" };
        releaser.Start();
        var exiter = new Thread(() =>
        {
            WaitHandle[] exitRequests = globalExitRequest is null ? [exitRequest] : [exitRequest, globalExitRequest];
            if (WaitHandle.WaitAny(exitRequests) >= 0)
            {
                logger.Log("fermeture demandée (mise à jour ou désinstallation)");
                tray.ExitFromAnotherInstance();
            }
        }) { IsBackground = true, Name = "exit-request" };
        exiter.Start();
        updater.Start();
        Application.Run(tray);
        updater.Stop();
        logger.Log("arrêt");
        return 0;
    }

    private static EventWaitHandle? TryCreateEvent(string name)
    {
        try
        {
            return new EventWaitHandle(false, EventResetMode.AutoReset, name);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return null;
        }
    }

    /// <summary>One instance per configuration folder (several workers can run on one PC for tests).</summary>
    private static string InstanceName() =>
        @"Local\EmbyRemoteWorker-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(WorkerConfig.Directory.ToLowerInvariant())))[..12];

    private static async Task<int> RunCommandAsync(string command, string[] args)
    {
        var logger = new Logger();
        var host = new WorkerHost(logger);
        var config = host.Config;
        if (Arg(args, "--ffmpeg") is { } ffmpeg)
            config.FfmpegPath = ffmpeg;
        if (Arg(args, "--max-jobs") is { } max)
            config.MaxJobs = int.Parse(max);

        switch (command)
        {
            case "pair":
                host.Pair(args.SkipWhile(a => a != "pair").Skip(1).FirstOrDefault() ?? throw new ArgumentException("pair <code>"));
                return 0;
            case "probe":
            {
                var caps = await CapabilityCache.GetAsync(host.ResolveFfmpeg(), logger.Log, force: true);
                logger.Log($"ffmpeg: {caps.FfmpegVersion}");
                logger.Log($"filters: {string.Join(", ", caps.Filters)}; GPUs: {string.Join("; ", caps.Gpus)}");
                return 0;
            }
            case "load":
            {
                string? encoder = Arg(args, "--encode");
                using var sampler = new SystemLoad();
                logger.Log($"GPU counters: {(sampler.HasGpuCounters ? "available" : "unavailable (CPU-only accounting)")}");
                System.Diagnostics.Process? test = null;
                if (encoder != null)
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(host.ResolveFfmpeg(),
                        $"-hide_banner -loglevel error -re -f lavfi -i testsrc2=size=1920x1080:rate=60 -t 20 -c:v {encoder} -f null -")
                    { UseShellExecute = false, CreateNoWindow = true };
                    test = System.Diagnostics.Process.Start(psi);
                }
                sampler.Sample([]);
                for (int i = 0; i < 12; i++)
                {
                    await Task.Delay(1000);
                    var l = sampler.Sample(test is { HasExited: false } ? [test] : []);
                    logger.Log($"CPU {l.CpuPercent,5:0.0}% (own {l.OwnCpuPercent,5:0.0}%)  GPU {Fmt(l.GpuPercent)} (own {Fmt(l.OwnGpuPercent)}, others {Fmt(l.ForeignGpuPercent)})  RAM {l.MemoryPercent:0}%");
                }
                try { test?.Kill(); } catch (InvalidOperationException) { }
                return 0;
            }
            case "run":
            {
                if (!host.IsPaired)
                {
                    logger.Log("non associé : lancez d'abord « EmbyRemoteWorker.exe pair <code> »");
                    return 1;
                }
                await host.StartAsync();
                if (host.Error != null)
                    return 1;
                var done = new TaskCompletionSource();
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    done.TrySetResult();
                };
                await done.Task;
                await host.StopAsync();
                return 0;
            }
            default:
                Console.WriteLine("usage: EmbyRemoteWorker [--tray] | pair <code> | probe | load [--encode X] | run  [--ffmpeg path] [--max-jobs N]");
                return 2;
        }

        static string Fmt(double? v) => v is null ? "  n/a" : $"{v,5:0.0}%";
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
