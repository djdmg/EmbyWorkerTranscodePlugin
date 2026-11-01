using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using ILogger = MediaBrowser.Model.Logging.ILogger;

namespace RemoteTranscode.EmbyPlugin;

/// <summary>
/// Makes Emby hand every ffmpeg run (playback transcodes and conversions) to the dispatcher while the plugin is
/// enabled, and restores Emby's own ffmpeg when it is disabled or uninstalled.
///
/// Emby has no public API to replace its transcoder; it does have a "custom ffmpeg path" setting (the "ffmpeg" named
/// configuration, also editable in its own UI). Its type is internal, so the two properties are set by reflection,
/// then the live configuration of the ffmpeg manager is replaced the same way Emby's own options page does it.
/// A missing member (other Emby version) is reported and leaves Emby untouched.
/// </summary>
public sealed class Interception
{
    private const string Key = "ffmpeg";
    private readonly IServerConfigurationManager configuration;
    private readonly IFfmpegManager ffmpeg;
    private readonly ILogger logger;
    private readonly string previousPath;

    public Interception(IServerConfigurationManager configuration, IFfmpegManager ffmpeg, ILogger logger, string dataDir)
    {
        this.configuration = configuration;
        this.ffmpeg = ffmpeg;
        this.logger = logger;
        previousPath = Path.Combine(dataDir, "emby-ffmpeg-setting.json");
    }

    /// <summary>
    /// Emby's "custom ffmpeg path" setting as it was before the plugin first switched it to the dispatcher (an
    /// administrator may have chosen their own ffmpeg). Restored exactly on disable/uninstall. Null when unknown.
    /// </summary>
    public sealed class PreviousSetting
    {
        public bool UseCustomEncoderPath { get; set; }
        public string? CustomEncoderPath { get; set; }
    }

    public PreviousSetting? Previous
    {
        get
        {
            try
            {
                return File.Exists(previousPath) ? JsonSerializer.Deserialize<PreviousSetting>(File.ReadAllText(previousPath)) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public static Interception? Instance { get; private set; }

    public static void Register(Interception instance) => Instance = instance;

    /// <summary>ffmpeg Emby currently runs.</summary>
    public string? CurrentEncoderPath => ffmpeg.FfmpegConfiguration?.EncoderPath;

    public bool IsActive(string dispatcher) => string.Equals(CurrentEncoderPath, dispatcher, StringComparison.Ordinal);

    /// <summary>Returns null on success, otherwise why the change was not applied.</summary>
    public string? Apply(bool enable, string dispatcher)
    {
        try
        {
            // Emby.Server.MediaEncoding's FfmpegConfiguration (internal type), the cached instance that the ffmpeg
            // manager also received at start-up (FfmpegManager.SetInitialConfiguration).
            object config = configuration.GetConfiguration(Key);
            var type = config.GetType();
            var useCustom = type.GetProperty("UseCustomEncoderPath");
            var customPath = type.GetProperty("CustomEncoderPath");
            if (useCustom is null || customPath is null)
                return "réglage ffmpeg d'Emby introuvable (version d'Emby non prise en charge)";

            bool currentlyCustom = (bool)useCustom.GetValue(config)!;
            string? currentPath = (string?)customPath.GetValue(config);
            if (enable)
            {
                if (!File.Exists(dispatcher))
                    return $"aiguilleur introuvable : {dispatcher}";
                if (currentlyCustom && currentPath == dispatcher)
                    return null;
                // Remembered before anything changes (a failed write leaves Emby untouched), unless the setting already
                // names one of our files (previous plugin version): then the saved one is still the administrator's.
                bool ours = currentlyCustom && currentPath != null && currentPath.StartsWith(Path.GetDirectoryName(previousPath)!, StringComparison.OrdinalIgnoreCase);
                if (!ours)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(previousPath)!);
                    File.WriteAllText(previousPath, JsonSerializer.Serialize(new PreviousSetting { UseCustomEncoderPath = currentlyCustom, CustomEncoderPath = currentPath }));
                }
                useCustom.SetValue(config, true);
                customPath.SetValue(config, dispatcher);
            }
            else
            {
                // Only undo our own setting: a custom ffmpeg chosen by the administrator stays as it is.
                if (!currentlyCustom || currentPath != dispatcher)
                    return null;
                // Back to exactly what the administrator had. Emby validates the effective path on save, so a custom
                // ffmpeg that no longer exists falls back to Emby's own (its path stays in the field, unused).
                var previous = Previous;
                bool restoreCustom = previous is { UseCustomEncoderPath: true } && File.Exists(previous.CustomEncoderPath);
                useCustom.SetValue(config, restoreCustom);
                if (previous != null)
                    customPath.SetValue(config, previous.CustomEncoderPath);
                // Otherwise CustomEncoderPath keeps the dispatcher: harmless while UseCustomEncoderPath is false.
            }

            // Persisted by Emby (library.db) and validated first by FfmpegConfigurationStore.Validate, which throws
            // when the ffmpeg/ffprobe paths do not exist.
            configuration.SaveConfiguration(Key, config);
            // Emby reads this configuration once at start-up; its options page swaps the live instance like this
            // (FfmpegOptionsService: FfmpegManager.FfmpegConfiguration has an internal setter).
            var live = ffmpeg.GetType().GetProperty("FfmpegConfiguration", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            live?.GetSetMethod(nonPublic: true)?.Invoke(ffmpeg, [config]);
            logger.Info("Transcodage distribué {0} : ffmpeg utilisé par Emby = {1}", enable ? "activé" : "désactivé", CurrentEncoderPath);
            // Restored: the next enable records the administrator's setting afresh (they may change it meanwhile).
            if (!enable)
                try { File.Delete(previousPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return null;
        }
        catch (Exception ex)
        {
            logger.ErrorException("Impossible de modifier le ffmpeg utilisé par Emby", ex);
            return ex.Message;
        }
    }
}

/// <summary>
/// At each Emby start: sets up the coordinator and the relay from what this Emby installation provides, then applies
/// the plugin's on/off state. Runs in the background so Emby's start-up is never delayed.
/// </summary>
public sealed class InterceptionEntryPoint : IServerEntryPoint
{
    private readonly Runtime runtime;

    // Emby builds entry points through its container at start-up, which is where the services come from; the static
    // instances are for the plugin class and the API, which Emby builds separately.
    public InterceptionEntryPoint(IServerConfigurationManager configuration, IFfmpegManager ffmpeg, IServerApplicationPaths paths, ILibraryManager library, ILogManager logManager,
        IServerApplicationHost host)
    {
        var logger = logManager.GetLogger("RemoteTranscode");
        runtime = new Runtime(paths, ffmpeg, library, logger, new SyncQueue(host, logger), host.ApplicationVersion);
        Interception.Register(new Interception(configuration, ffmpeg, logger, runtime.DataDir));
        Runtime.Instance = runtime;
    }

    // Until StartAsync has rewritten the relay, Emby keeps the setting persisted at the previous run (possibly the
    // dispatcher of the previous plugin version), which still works: the relay falls back to Emby's ffmpeg.
    public void Run() => _ = Task.Run(runtime.StartAsync);

    public void Dispose() => runtime.Dispose();
}
