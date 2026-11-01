using System.Text.Json;
using Microsoft.Win32;
using RemoteTranscode.Common;

namespace RemoteTranscode.Worker;

/// <summary>
/// Owns the worker's lifecycle for the tray UI: configuration, capability detection (cached), the coordinator
/// connection and Windows start-up registration.
/// Threading: Start/Stop/Restart are driven from the UI thread (or the console in "run" mode) and serialized by
/// <see cref="lifecycle"/>, so a double click or a save during start-up cannot create a second, unstoppable service;
/// the properties below are read by the UI timers every second.
/// </summary>
public sealed class WorkerHost
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "EmbyRemoteWorker";
    private CancellationTokenSource? cts;
    private Task? runTask;
    private readonly SemaphoreSlim lifecycle = new(1, 1);

    public WorkerHost(Logger logger)
    {
        Logger = logger;
        Config = WorkerConfig.Load(logger.Log);
    }

    public Logger Logger { get; }
    public WorkerConfig Config { get; }
    public WorkerCapabilities? Capabilities { get; private set; }
    public WorkerService? Service { get; private set; }
    public string? Error { get; private set; }
    public bool Starting { get; private set; }

    /// <summary>A start, stop or restart is in progress (the UI disables the buttons that would queue another one).</summary>
    public bool Busy => lifecycle.CurrentCount == 0;

    public bool IsPaired => Config.CoordinatorUrl != null && Config.ProtectedToken != null;

    /// <summary>State shown in the UI: an error or a missing pairing wins over the availability engine.</summary>
    public (WorkerState State, string? Reason) Status()
    {
        if (Error != null)
            return (WorkerState.Error, Error);
        if (Starting)
            return (WorkerState.Unavailable, "détection des capacités…");
        if (Service is null)
            return (WorkerState.Offline, IsPaired ? "arrêté" : "non associé à un serveur");
        var d = Service.Decision;
        return (d.State, d.Reason);
    }

    /// <summary>
    /// Detects capabilities (cached) and starts the connection loop in the background. Failures end up in
    /// <see cref="Error"/> for the UI rather than as exceptions.
    /// </summary>
    public async Task StartAsync()
    {
        await lifecycle.WaitAsync();
        try { await StartCoreAsync(); }
        finally { lifecycle.Release(); }
    }

    private async Task StartCoreAsync()
    {
        // Already running (e.g. a start queued behind another one): never a second service with the same worker id.
        if (Service != null)
            return;
        Error = null;
        if (!IsPaired)
            return;
        Starting = true;
        try
        {
            CleanStaleJobs();
            string ffmpeg = ResolveFfmpeg();
            Capabilities = await CapabilityCache.GetAsync(ffmpeg, Logger.Log);
            if (Capabilities.VideoEncoders.Length == 0)
            {
                Error = "aucun encodeur vidéo utilisable (voir le journal)";
                return;
            }
            Config.ResolvedFfmpegPath = ffmpeg;
            // GPU tone mapping switched off in the settings: not announced, so the coordinator sends this worker no
            // conversion tone mapped on the GPU, and playback is tone mapped on the CPU.
            var published = Config.GpuToneMapping ? Capabilities : Capabilities with { GpuToneMapping = null };
            Service = new WorkerService(Config, published, Logger.Log);
            cts = new CancellationTokenSource();
            var token = cts.Token;
            runTask = Task.Run(async () =>
            {
                try { await Service.RunForeverAsync(token); }
                catch (OperationCanceledException) { }
            });
        }
        catch (Exception ex)
        {
            Error = $"FFmpeg indisponible : {ex.Message}";
            Logger.Log(Error);
        }
        finally
        {
            Starting = false;
        }
    }

    /// <summary>Stops the jobs (waiting up to ~5 s), then the connection; bounded so the tray can always exit.</summary>
    public async Task StopAsync()
    {
        await lifecycle.WaitAsync();
        try { await StopCoreAsync(); }
        finally { lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        // "released:": the playback was not finished, the coordinator resumes it elsewhere (exit 0 would end it).
        if (Service != null)
            await Service.StopAllJobsAsync("released: arrêt du worker");
        cts?.Cancel();
        if (runTask != null)
            await runTask.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
        Service = null;
        runTask = null;
    }

    public async Task RestartAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            await StopCoreAsync();
            await StartCoreAsync();
        }
        finally
        {
            lifecycle.Release();
        }
    }

    /// <summary>"Libérer maintenant": every job stops and the coordinator resumes playback elsewhere or locally.</summary>
    public Task ReleaseNowAsync() => Service?.ReleaseAsync() ?? Task.CompletedTask;

    public void Pair(string code)
    {
        Config.ApplyPairingCode(code);
        // The PEM given to ffmpeg belongs to the previous coordinator: it is rewritten at the next pinned handshake.
        if (File.Exists(WorkerConfig.CaFilePath))
            File.Delete(WorkerConfig.CaFilePath);
        Config.Save();
        Logger.Log($"associé au serveur {Config.CoordinatorUrl} (id {Config.WorkerId})");
    }

    /// <summary>Bundled ffmpeg next to the executable first, then the configured path, then PATH.</summary>
    public string ResolveFfmpeg()
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (Config.FfmpegPath is "" or "ffmpeg" && File.Exists(bundled))
            return bundled;
        if (File.Exists(Config.FfmpegPath))
            return Path.GetFullPath(Config.FfmpegPath);
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
            if (dir.Length > 0 && File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException("ffmpeg.exe introuvable ; indiquez son chemin dans les paramètres");
    }

    /// <summary>
    /// Job folders left by a crash or a kill: their output was never delivered and the coordinator has resumed those
    /// jobs elsewhere, so they are only garbage.
    /// </summary>
    private void CleanStaleJobs()
    {
        if (!Directory.Exists(WorkerConfig.JobsDirectory))
            return;
        foreach (string stale in Directory.GetDirectories(WorkerConfig.JobsDirectory))
        {
            try
            {
                Directory.Delete(stale, true);
                Logger.Log($"dossier de tâche abandonné supprimé : {Path.GetFileName(stale)}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Per-user HKCU Run entry: no elevation needed, starts at logon in the user's session (idle detection needs it).</summary>
    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
                key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --tray");
            else
                key.DeleteValue(RunValue, false);
        }
    }
}

/// <summary>
/// Capability detection takes several seconds (one test encode per encoder), so results are reused until ffmpeg or
/// the GPU drivers change.
/// </summary>
public static class CapabilityCache
{
    private static string CachePath => Path.Combine(WorkerConfig.Directory, "capabilities.json");

    private sealed record Entry(string Key, WorkerCapabilities Capabilities);

    public static async Task<WorkerCapabilities> GetAsync(string ffmpeg, Action<string> log, bool force = false)
    {
        var info = new FileInfo(ffmpeg);
        // A different ffmpeg build or a driver update (part of the GPU names) invalidates the cache.
        // "v2": full filter list since phase 3. "v3": hardware decoders. "v4": GPU tone mapping (0.7.15). "v5": 0.7.16.
        string key = $"v5|{info.FullName}|{info.Length}|{info.LastWriteTimeUtc:O}|{string.Join(";", CapabilityProbe.Gpus())}";
        if (!force && File.Exists(CachePath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<Entry>(File.ReadAllText(CachePath));
                if (cached?.Key == key)
                {
                    log($"capacités reprises du cache : {string.Join(", ", cached.Capabilities.VideoEncoders)}");
                    var caps0 = cached.Capabilities;
                    // A refused GPU tone mapping is never kept: a BIOS setting (Resizable BAR) or the memory other
                    // programs hold can change it without any change to ffmpeg or the drivers. A few seconds per start.
                    if (caps0.GpuToneMapping is null && caps0.Filters.Contains("libplacebo"))
                    {
                        var probed = await CapabilityProbe.GpuToneMappingAsync(ffmpeg, caps0.Filters, caps0.VideoEncoders, log);
                        log($"tone mapping GPU validé : {(probed is null ? "aucun" : $"{probed.Method} ({probed.Device})")}");
                        if (probed != null)
                        {
                            caps0 = caps0 with { GpuToneMapping = probed };
                            File.WriteAllText(CachePath, JsonSerializer.Serialize(new Entry(key, caps0)));
                        }
                    }
                    return caps0;
                }
            }
            catch (JsonException) { }
        }
        log("détection des capacités de FFmpeg et du matériel…");
        var caps = await CapabilityProbe.RunAsync(ffmpeg, log);
        Directory.CreateDirectory(WorkerConfig.Directory);
        File.WriteAllText(CachePath, JsonSerializer.Serialize(new Entry(key, caps)));
        log($"encodeurs validés : {string.Join(", ", caps.VideoEncoders)}");
        log($"décodage matériel validé : {(caps.HwDecoders.Length == 0 ? "aucun" : string.Join(", ", caps.HwDecoders))}");
        log($"tone mapping GPU validé : {(caps.GpuToneMapping is { } tm ? $"{tm.Method} ({tm.Device})" : "aucun")}");
        return caps;
    }

    public static void Invalidate()
    {
        if (File.Exists(CachePath))
            File.Delete(CachePath);
    }
}
