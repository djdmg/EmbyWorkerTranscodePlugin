using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using RemoteTranscode.Coordinator;
using EmbyLogger = MediaBrowser.Model.Logging.ILogger;

namespace RemoteTranscode.EmbyPlugin;

/// <summary>
/// Everything the distributed transcoding needs on the Emby server, set up by the plugin alone so it works on any
/// Emby installation and again after every Emby update: the coordinator runs inside Emby's process (Emby ships
/// ASP.NET Core), the ffmpeg relay is extracted from the plugin for this platform, and every path comes from Emby
/// itself (its ffmpeg, transcoding folder, libraries, data folder). Re-done at each start.
/// </summary>
public sealed class Runtime : IDisposable
{
    private readonly IServerApplicationPaths paths;
    private readonly IFfmpegManager ffmpeg;
    private readonly ILibraryManager library;
    private readonly EmbyLogger logger;
    private readonly SyncQueue syncQueue;
    private readonly SemaphoreSlim gate = new(1, 1);
    private WebApplication? app;
    private CoordinatorOptions? options;
    private Timer? refresh;
    private volatile bool started;
    private readonly Version? embyVersion;

    public Runtime(IServerApplicationPaths paths, IFfmpegManager ffmpeg, ILibraryManager library, EmbyLogger logger, SyncQueue syncQueue,
        Version? embyVersion = null)
    {
        this.syncQueue = syncQueue;
        this.embyVersion = embyVersion;
        this.paths = paths;
        this.ffmpeg = ffmpeg;
        this.library = library;
        this.logger = logger;
        DataDir = Path.Combine(paths.ProgramDataPath, "remote-transcode");
        BinDir = Path.Combine(DataDir, "bin");
    }

    /// <summary>
    /// Set by <see cref="InterceptionEntryPoint"/>'s constructor; the API and <see cref="Plugin.OnUninstalling"/>
    /// read it and must cope with null (entry point not created yet).
    /// </summary>
    public static Runtime? Instance { get; set; }

    /// <summary>Coordinator data: certificate, paired workers, secrets, conversion spool. Kept across Emby updates.</summary>
    public string DataDir { get; }

    /// <summary>The relay and its configuration, rewritten at each start.</summary>
    public string BinDir { get; }

    /// <summary>What Emby runs as ffmpeg while the plugin is enabled.</summary>
    public string? DispatcherPath { get; private set; }

    /// <summary>Emby's own ffmpeg, which runs everything not handed to a worker.</summary>
    public string? RealFfmpeg { get; private set; }

    /// <summary>Why the service is not running (shown on the plugin page), null when it runs.</summary>
    public string? Error { get; private set; }

    // True only once the coordinator listens (set after its StartAsync succeeded, cleared when it stops).
    public bool Running => started;

    /// <summary>Coordinator's loopback endpoint: relay WebSocket and administration API (never exposed).</summary>
    public string AdminUrl => $"http://127.0.0.1:{Config.ShimPort}";

    /// <summary>
    /// Bearer secret of the admin API, created by the coordinator at its first start. Read from disk at each call,
    /// whereas the coordinator keeps the value it loaded when it started.
    /// </summary>
    // The coordinator writes it owner-only (0600) as the Emby user, which is also the user this plugin runs as.
    // Never logged; unreadable is reported as absent.
    public string? PluginSecret
    {
        get
        {
            try
            {
                string path = Path.Combine(DataDir, "plugin.secret");
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>(Re)starts the coordinator and the relay, then applies the on/off switch to Emby's ffmpeg.</summary>
    public async Task StartAsync()
    {
        // Called by the entry point at Emby's start and by the settings endpoint: never two (re)starts at once.
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopHostAsync().ConfigureAwait(false);
            Error = null;
            try
            {
                Directory.CreateDirectory(BinDir);
                DetectFfmpeg();
                options = BuildOptions();
                WriteRelay();
                await SelfTestAsync().ConfigureAwait(false);
                app = CoordinatorHost.Build(options, logging => logging.AddProvider(new EmbyLoggerProvider(logger)));
                await app.StartAsync().ConfigureAwait(false);
                started = true;
                refresh = new Timer(_ => RefreshRoots(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
                logger.Info("Transcodage distribué : coordinateur démarré (workers {0}, relais {1}), ffmpeg d'Emby {2}", options.WorkerUrl, options.ShimUrl, RealFfmpeg);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                logger.ErrorException("Transcodage distribué : démarrage impossible, Emby garde son ffmpeg", ex);
                await StopHostAsync().ConfigureAwait(false);
            }
            // Always reached: on failure Running is false, so Emby is switched back to its own ffmpeg.
            ApplySwitch();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The page's on/off switch: applied to Emby, then saved only if Emby accepted it, so the stored state never
    /// claims what is not in effect. Under the start gate so it cannot interleave with a (re)start: an "on" can no
    /// longer land after a failed start switched Emby back to its own ffmpeg. Returns null or why it was refused.
    /// </summary>
    public async Task<string?> SetEnabledAsync(bool enabled)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
            return "plugin non initialisé";
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (enabled && !Running)
                return "coordinateur arrêté : " + (Error ?? "démarrage en cours");
            bool previous = plugin.Configuration.Enabled;
            plugin.Configuration.Enabled = enabled;
            string? problem = ApplySwitch();
            if (problem != null)
            {
                plugin.Configuration.Enabled = previous;
                return problem;
            }
            plugin.SaveConfiguration();
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Enabled and running: Emby runs the dispatcher. Otherwise Emby runs its own ffmpeg, as without the plugin
    /// (a dispatcher whose coordinator is down would still work, but each transcode would first wait for it).
    /// Callers hold the start gate.
    /// </summary>
    private string? ApplySwitch()
    {
        var interception = Interception.Instance;
        if (interception is null || DispatcherPath is null)
            return "plugin non initialisé";
        bool on = Config.Enabled && Running;
        return interception.Apply(on, DispatcherPath);
    }

    // ---- Emby's ffmpeg ---------------------------------------------------------------------------

    private void DetectFfmpeg()
    {
        string extension = OperatingSystem.IsWindows() ? ".exe" : "";
        string shim = Path.Combine(BinDir, "remote-ffmpeg" + extension);
        // Windows has no shell dispatcher: Emby runs the relay itself, which passes unsuitable runs to Emby's ffmpeg.
        // Set first so that ApplySwitch can still restore Emby's ffmpeg if the detection below fails.
        DispatcherPath = OperatingSystem.IsWindows() ? shim : Path.Combine(BinDir, "ffmpeg");

        // Emby's own ffmpeg, i.e. what Emby ran before the plugin: its effective configuration unless that is us
        // (the administrator's custom ffmpeg or Emby's default); while the plugin is enabled, the administrator's
        // custom ffmpeg saved when the plugin took over (Interception), else Emby's command line (packages pass it),
        // else what was found last time.
        var saved = LoadSaved();
        string? current = ffmpeg.FfmpegConfiguration?.EncoderPath;
        var previous = Interception.Instance?.Previous;
        string? adminCustom = previous is { UseCustomEncoderPath: true } && File.Exists(previous.CustomEncoderPath) ? previous.CustomEncoderPath : null;
        string? real = (current != null && !IsOurs(current) ? current : null)
            ?? adminCustom
            ?? CommandLineValue("-ffmpeg")
            ?? saved?.Ffmpeg;
        if (real is null || !File.Exists(real))
            throw new InvalidOperationException($"ffmpeg d'Emby introuvable ({real ?? "aucun chemin"})");
        RealFfmpeg = real;

        string? probe = CommandLineValue("-ffprobe") ?? ffmpeg.FfmpegConfiguration?.ProbePath;
        if (probe is null || IsOurs(probe) || !File.Exists(probe))
            probe = Path.Combine(Path.GetDirectoryName(real)!, "ffprobe" + extension);
        RealFfprobe = probe;

        // Variables Emby sets for its ffmpeg (its defaults plus the Linux overrides of its ffmpeg options): the relay
        // and the server-side runs must start Emby's ffmpeg with the same ones.
        var env = new Dictionary<string, string>();
        foreach (var v in ffmpeg.FfmpegConfiguration?.EnvironmentVariables ?? [])
            env[v.VariableName] = v.Value;
        if (env.Count == 0 && saved?.Environment != null)
            env = saved.Environment;
        FfmpegEnvironment = env;

        // Remembered for the next start, when Emby's configuration will name the dispatcher instead of its ffmpeg.
        File.WriteAllText(Path.Combine(DataDir, "emby-ffmpeg.json"), JsonSerializer.Serialize(new SavedFfmpeg { Ffmpeg = real, Ffprobe = probe, Environment = env }));
    }

    private string RealFfprobe { get; set; } = "";
    private Dictionary<string, string> FfmpegEnvironment { get; set; } = [];

    /// <summary>A path inside the plugin's data folder (dispatcher or relay), never Emby's real ffmpeg.</summary>
    private bool IsOurs(string path) => path.StartsWith(DataDir, StringComparison.OrdinalIgnoreCase);

    private SavedFfmpeg? LoadSaved()
    {
        try
        {
            string path = Path.Combine(DataDir, "emby-ffmpeg.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<SavedFfmpeg>(File.ReadAllText(path)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Value following <paramref name="name"/> on Emby's command line. Emby packages (Synology, Docker) start
    /// EmbyServer with -ffmpeg / -ffprobe / -ffdetect pointing at the tools they bundle.
    /// </summary>
    private static string? CommandLineValue(string name)
    {
        string[] args = System.Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Contents of emby-ffmpeg.json: Emby's own tools as detected at the last start.</summary>
    public sealed class SavedFfmpeg
    {
        public string? Ffmpeg { get; set; }
        public string? Ffprobe { get; set; }
        public Dictionary<string, string>? Environment { get; set; }
    }

    // ---- Coordinator ------------------------------------------------------------------------------

    private CoordinatorOptions BuildOptions()
    {
        var config = Config;
        // Emby starts its ffmpeg from its own working directory; server-side runs (fallbacks, assemblies) do the same.
        string workDir = System.Environment.CurrentDirectory;
        var o = new CoordinatorOptions
        {
            DataDir = DataDir,
            WorkerUrl = config.WorkerUrl,
            PublicUrl = string.IsNullOrWhiteSpace(config.PublicUrl) ? null : config.PublicUrl.Trim().TrimEnd('/'),
            ShimUrl = AdminUrl,
            EncodeSpoolDir = Path.Combine(DataDir, "encode"),
            ServerFfmpeg = RealFfmpeg!,
            ServerFfprobe = RealFfprobe,
            ServerToolsWorkingDirectory = workDir,
            ServerEnvironment = FfmpegEnvironment,
            CertificateNames = CertificateNames(),
            ConversionQueueRemaining = syncQueue.Remaining,
            IssueReporting = config.ReportFailures,
            IssueReportToken = IssueReportToken,
            ServerDescription = ServerDescription(),
        };
        (o.AllowedRoots, o.OutputRoots) = Roots();
        return o;
    }

    // ---- Automatic failure reports ------------------------------------------------------------------

    /// <summary>
    /// GitHub token embedded at build time (see FailureReporter): null in local builds and forks, which then never
    /// report. Never logged, shown or returned by the API; only whether there is one is.
    /// </summary>
    private static readonly string? IssueReportToken = FailureReporter.EmbeddedToken(typeof(Runtime).Assembly);

    /// <summary>Whether this build can report failures at all.</summary>
    public static bool HasIssueReportToken => IssueReportToken != null;

    /// <summary>The page's switch, applied to the running coordinator at once (no restart) and saved.</summary>
    public void SetReportFailures(bool enabled)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
            return;
        plugin.Configuration.ReportFailures = enabled;
        plugin.SaveConfiguration();
        if (options != null)
            options.IssueReporting = enabled;
    }

    /// <summary>The server as a failure report describes it: "Linux x64, Emby 4.10.2" (no host name, no kernel string).</summary>
    private string ServerDescription()
    {
        string os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "macOS"
            : OperatingSystem.IsFreeBSD() ? "FreeBSD" : "other OS";
        if (os == "Linux" && RuntimeId().Contains("musl", StringComparison.Ordinal))
            os = "Linux (musl)";
        string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var v = embyVersion ?? typeof(IFfmpegManager).Assembly.GetName().Version;
        string emby = v is null ? "?" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        return $"{os} {arch}, Emby {emby}";
    }

    /// <summary>
    /// Workers may read the media of Emby's libraries and the files Emby generates for a transcode (fonts,
    /// extracted subtitles, cache); results are written only to Emby's transcoding folder and conversion outputs.
    /// </summary>
    private (string[] Allowed, string[] Output) Roots()
    {
        var libraries = library.GetVirtualFolders().SelectMany(f => f.Locations ?? []).Where(l => !string.IsNullOrEmpty(l));
        string transcoding = paths.GetTranscodingTempPath();
        var generated = new[] { paths.CachePath, paths.SubtitleFontsPath, paths.InternalMetadataPath, paths.DataPath, transcoding };
        var allowed = libraries.Concat(generated).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToArray();
        // Emby writes conversions into the libraries or its own data folder ("sync").
        var output = libraries.Concat([transcoding, paths.DataPath, paths.CachePath]).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToArray();
        return (allowed, output);
    }

    /// <summary>
    /// Libraries added or moved in Emby are picked up without a restart. The arrays are swapped whole, so readers on
    /// other threads always see a complete list.
    /// </summary>
    private void RefreshRoots()
    {
        try
        {
            if (options != null)
                (options.AllowedRoots, options.OutputRoots) = Roots();
        }
        catch (Exception ex)
        {
            logger.Debug("Transcodage distribué : bibliothèques non relues ({0})", ex.Message);
        }
    }

    /// <summary>
    /// Names put in the self-signed certificate (only when it is first created) and, for the first one, the host of
    /// the pairing code in direct-TLS mode: the administrator's AdvertisedHost if set, else this server's LAN
    /// addresses, those of interfaces with a default gateway first. Container/VM bridges (docker0, veth, br-...) and
    /// tunnels are skipped: their addresses are unreachable from the workers' PCs.
    /// </summary>
    private static string[] CertificateNames()
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(Config.AdvertisedHost))
            names.Add(Config.AdvertisedHost.Trim());
        try
        {
            names.AddRange(NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && !IsVirtual(n))
                .Select(n => n.GetIPProperties())
                .OrderByDescending(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                .SelectMany(p => p.UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(a => a.ToString()));
        }
        catch (NetworkInformationException)
        {
            // Interfaces unreadable (restricted container): the host name and the administrator's setting remain.
        }
        return names.Append(Dns.GetHostName()).Append("localhost").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // "br-" only: Docker's user networks. Plain bridges (br0) and Synology's ovs_eth0 carry the LAN address.
    private static readonly string[] VirtualPrefixes =
        ["docker", "veth", "br-", "virbr", "vmnet", "vboxnet", "lxc", "lxd", "cni", "flannel", "cali", "podman", "tun", "tap", "vethernet"];

    private static bool IsVirtual(NetworkInterface n)
    {
        if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            return true;
        string name = n.Name.ToLowerInvariant();
        return VirtualPrefixes.Any(name.StartsWith);
    }

    /// <summary>Stops the coordinator; connected relays and workers are disconnected (relays then fall back to Emby's ffmpeg).</summary>
    private async Task StopHostAsync()
    {
        started = false;
        refresh?.Dispose();
        refresh = null;
        if (app is null)
            return;
        try
        {
            await app.StopAsync().ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.ErrorException("Transcodage distribué : arrêt du coordinateur", ex);
        }
        app = null;
    }

    // ---- Relay ------------------------------------------------------------------------------------

    /// <summary>Extracts the relay built for this platform (only when it changed) and writes its configuration.</summary>
    private void WriteRelay()
    {
        string rid = RuntimeId();
        string resource = $"RemoteTranscode.EmbyPlugin.Shim.{rid}.gz";
        using var packed = typeof(Runtime).Assembly.GetManifestResourceStream(resource)
            ?? throw new PlatformNotSupportedException($"plateforme {rid} non prise en charge par ce plugin");
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(packed, CompressionMode.Decompress))
            gz.CopyTo(ms);
        byte[] bytes = ms.ToArray();

        string extension = OperatingSystem.IsWindows() ? ".exe" : "";
        string shim = Path.Combine(BinDir, "remote-ffmpeg" + extension);
        if (!File.Exists(shim) || !SHA256.HashData(File.ReadAllBytes(shim)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
        {
            // A running transcode may still use the old file: replace it by renaming, never by rewriting it.
            string temp = shim + ".new";
            File.WriteAllBytes(temp, bytes);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, (UnixFileMode)0b111_101_101); // rwxr-xr-x
            if (File.Exists(shim))
            {
                // Windows lets a running exe be renamed but not deleted: the previous relay is parked as ".old" and
                // deleted at the next replacement, once no transcode uses it any more.
                string old = shim + ".old";
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                File.Move(shim, old, overwrite: true);
            }
            File.Move(temp, shim);
        }

        // shim.json next to the relay (see RemoteTranscode.Shim's ShimConfig): where the coordinator is, where its
        // secret is, and Emby's real tools with their environment for everything run on the server.
        var shimConfig = new
        {
            CoordinatorUrl = $"ws://127.0.0.1:{Config.ShimPort}/ws/shim",
            SecretPath = Path.Combine(DataDir, "shim.secret"),
            RealFfmpeg,
            RealFfprobe,
            ToolsDirectory = (string?)null,
            Environment = FfmpegEnvironment,
        };
        File.WriteAllText(Path.Combine(BinDir, "shim.json"), JsonSerializer.Serialize(shimConfig, new JsonSerializerOptions { WriteIndented = true }));

        if (!OperatingSystem.IsWindows())
        {
            // Started for every ffmpeg run of Emby: the shell sends only what a worker can take to the relay.
            // The relay is a self-contained .NET app (tens of MB): probes, thumbnails and subtitle extractions never pay
            // its start-up cost. Emby's ffmpeg path is the only interpolated value and is single-quoted (ShellQuote).
            // Note: if "exec" itself fails (relay not runnable, e.g. noexec mount or wrong libc), sh exits instead of
            // falling through to Emby's ffmpeg. Guarding that here would cost a run of the relay per call; instead
            // SelfTestAsync runs the dispatcher and the relay at each start, and Emby is never switched to a dispatcher
            // that failed it.
            string script = $$"""
                #!/bin/sh
                # Written by the Emby plugin "Transcodage distribué" at each start; Emby runs it as ffmpeg while the plugin is enabled.
                # Playback transcodes and video conversions go to the relay, everything else to Emby's own ffmpeg.
                # Kill switch: create a file named "disabled" in this folder.
                DIR=$(dirname "$0")
                case " $* " in
                  *" -f segment "*|*" -c:v:0 libx264 "*|*" -c:v:0 libx265 "*)
                    if [ ! -e "$DIR/disabled" ] && [ -x "$DIR/remote-ffmpeg" ]; then
                      exec "$DIR/remote-ffmpeg" "$@"
                    fi
                    ;;
                esac
                REAL={{ShellQuote(RealFfmpeg!)}}
                # Emby moved its ffmpeg (update) while the plugin could not rewrite this file: take it from Emby's command line.
                if [ ! -x "$REAL" ] && [ -r /proc/$PPID/cmdline ]; then
                  P=$(tr '\0' '\n' < /proc/$PPID/cmdline | sed -n '/^-ffmpeg$/{n;p;q;}')
                  [ -n "$P" ] && [ -x "$P" ] && REAL=$P
                fi
                exec "$REAL" "$@"

                """;
            string path = DispatcherPath!;
            // LF only (a CR would end up in every command), then an atomic rename: a shell already reading the
            // previous script keeps its own copy (inode).
            File.WriteAllText(path + ".new", script.Replace("\r\n", "\n"));
            File.SetUnixFileMode(path + ".new", (UnixFileMode)0b111_101_101);
            File.Move(path + ".new", path, overwrite: true);
        }
    }

    /// <summary>POSIX single quoting: nothing is special inside '...' except the quote itself, closed and escaped.</summary>
    private static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Runs the relay and the dispatcher with "-version" the way Emby will (its working directory and ffmpeg
    /// variables). "-version" is never offloaded: the relay passes it to Emby's ffmpeg and the dispatcher execs Emby's
    /// ffmpeg, which proves that both start here (exec allowed on this folder, right libc and architecture) and reach
    /// Emby's ffmpeg. Throws with the reason otherwise, so Emby keeps its own ffmpeg.
    /// </summary>
    private async Task SelfTestAsync()
    {
        string extension = OperatingSystem.IsWindows() ? ".exe" : "";
        string shim = Path.Combine(BinDir, "remote-ffmpeg" + extension);
        await RunVersionAsync(shim, "le relais").ConfigureAwait(false);
        if (DispatcherPath != shim)
            await RunVersionAsync(DispatcherPath!, "l'aiguilleur").ConfigureAwait(false);
    }

    private async Task RunVersionAsync(string path, string what)
    {
        string advice = $"Emby garde son ffmpeg (dossier {BinDir} monté en noexec, ou plateforme {RuntimeId()} inadaptée ?)";
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = System.Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add("-version");
        foreach (var (name, value) in FfmpegEnvironment)
            psi.Environment[name] = value;
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException($"{what} n'a pas démarré : {advice}");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"{what} ne peut pas s'exécuter ({ex.Message}) : {advice}", ex);
        }
        using (process)
        {
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            // Generous: the relay is a self-contained .NET app, slow to start cold on a small NAS.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new InvalidOperationException($"{what} ne répond pas à « -version » : {advice}");
            }
            string output = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0 || !output.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
            {
                string detail = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "aucune sortie";
                throw new InvalidOperationException($"{what} a échoué au test « -version » (code {process.ExitCode} : {detail}) : {advice}");
            }
        }
    }

    /// <summary>
    /// .NET runtime identifier of this server, matching the relay resources packed by tools/build-plugin.ps1
    /// (Shim.&lt;rid&gt;.gz). A platform without a packed relay makes the start fail, so Emby keeps its own ffmpeg.
    /// </summary>
    private static string RuntimeId()
    {
        // The RID of the .NET runtime running Emby is exact for the portable builds Emby ships, including the musl
        // ones ("linux-musl-x64"); distribution-specific RIDs (e.g. "ubuntu.22.04-x64") fall back to the detection below.
        string reported = RuntimeInformation.RuntimeIdentifier;
        if (typeof(Runtime).Assembly.GetManifestResourceInfo($"RemoteTranscode.EmbyPlugin.Shim.{reported}.gz") != null)
            return reported;
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "osx" : "unknown";
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            var a => a.ToString().ToLowerInvariant(),
        };
        // Alpine-based images (musl) need their own build.
        if (os == "linux" && (File.Exists("/etc/alpine-release") || reported.Contains("musl", StringComparison.Ordinal) || IsMuslOnly()))
            os = "linux-musl";
        return $"{os}-{arch}";
    }

    /// <summary>A musl dynamic loader and no glibc one: a musl system that is not Alpine.</summary>
    private static bool IsMuslOnly()
    {
        try
        {
            static bool Any(string dir, string pattern) => Directory.Exists(dir) && Directory.EnumerateFiles(dir, pattern).Any();
            return Any("/lib", "ld-musl-*") && !Any("/lib", "ld-linux*") && !Any("/lib64", "ld-linux*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Emby shutting down: the coordinator stops, but Emby's ffmpeg setting is left as is so the next start keeps
    /// the plugin's on/off state.
    /// </summary>
    public void Dispose()
    {
        StopHostAsync().GetAwaiter().GetResult();
    }
}

/// <summary>Coordinator logs go to Emby's log, under "RemoteTranscode".</summary>
internal sealed class EmbyLoggerProvider(EmbyLogger logger) : ILoggerProvider
{
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Adapter(logger);

    public void Dispose()
    {
    }

    private sealed class Adapter(EmbyLogger logger) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            // Emby's logger formats its message (string.Format): braces from the already formatted text are escaped.
            string message = formatter(state, exception).Replace("{", "{{").Replace("}", "}}");
            switch (logLevel)
            {
                case LogLevel.Information:
                    logger.Info(message);
                    break;
                case LogLevel.Warning:
                    logger.Warn(message);
                    break;
                default:
                    if (exception != null)
                        logger.ErrorException(message, exception);
                    else
                        logger.Error(message);
                    break;
            }
        }
    }
}
