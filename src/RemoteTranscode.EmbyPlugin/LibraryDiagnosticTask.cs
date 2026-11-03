using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Tasks;
using RemoteTranscode.Coordinator;
using ILogger = MediaBrowser.Model.Logging.ILogger;

namespace RemoteTranscode.EmbyPlugin;

[Route("/RemoteTranscode/LibraryDiagnostic", "GET", Summary = "Video files of the libraries at risk for a slice-based conversion, with per-category counts")]
[Authenticated(Roles = "Admin")]
public sealed class GetLibraryDiagnostic : IReturnVoid
{
    /// <summary>False: counts and state only, for the page to follow a running analysis without reloading the list.</summary>
    public bool? IncludeFiles { get; set; }
}

/// <summary>Results of the analysis, shared by the task and the API, which Emby builds separately.</summary>
internal static class LibraryDiagnosticResults
{
    public static readonly LibraryDiagnosticState State = new();
    private static LibraryDiagnosticStore? store;
    private static string? storeFolder;

    public static LibraryDiagnosticStore? Store(Runtime? runtime)
    {
        if (runtime is null)
            return null;
        lock (State)
        {
            if (store is null || storeFolder != runtime.DataDir)
            {
                Directory.CreateDirectory(runtime.DataDir);
                store = new LibraryDiagnosticStore(Path.Combine(runtime.DataDir, "library-diagnostic.json"));
                storeFolder = runtime.DataDir;
            }
            return store;
        }
    }
}

/// <summary>
/// Emby scheduled task, with no trigger by default: run by hand from the dashboard (Scheduled tasks, "Transcodage
/// distribué" category). Stopping it from there keeps what was analysed; the next run resumes after it.
/// </summary>
public sealed class LibraryDiagnosticTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly ILibraryManager library;
    private readonly ISessionManager sessions;
    private readonly ILogger logger;

    public LibraryDiagnosticTask(ILibraryManager library, ISessionManager sessions, ILogManager logManager)
    {
        this.library = library;
        this.sessions = sessions;
        logger = logManager.GetLogger("RemoteTranscode");
    }

    public string Name => "Diagnostic de la bibliothèque";

    public string Key => "RemoteTranscodeLibraryDiagnostic";

    public string Description => "Repère à l'avance les vidéos à risque pour une conversion par tranches. Un fichier à la fois, en priorité minimale, en pause pendant une lecture ou une conversion. Résultats sur la page « Diagnostic de la bibliothèque ».";

    public string Category => "Transcodage distribué";

    public bool IsHidden => false;

    public bool IsEnabled => true;

    public bool IsLogged => true;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        var runtime = Runtime.Instance;
        // Emby's own ffprobe with its environment, at the lowest priority (Idle on Windows, nice 19 on Linux).
        var media = runtime?.CreateServerMedia();
        var store = LibraryDiagnosticResults.Store(runtime);
        if (runtime is null || media is null || store is null)
            throw new InvalidOperationException("Transcodage distribué : ffprobe d'Emby non détecté, voir la page du plugin");
        var probe = new LibraryDiagnosticProbe(media.ProbeQuietlyAsync);
        // Counts only in the log: paths stay in the results, which only an administrator can read.
        var scanner = new LibraryDiagnosticScanner(store, LibraryDiagnosticResults.State, probe.ReadAsync,
            () => BusyReason(runtime), message => logger.Info("Diagnostic de la bibliothèque : {0}", message));
        await scanner.RunAsync(VideoPaths(), progress, cancellationToken).ConfigureAwait(false);
    }

    private List<string> VideoPaths() =>
        library.GetItemList(new InternalItemsQuery { Recursive = true, MediaTypes = [MediaType.Video], IsVirtualItem = false, HasPath = true })
            .Select(item => item.Path)
            .Where(path => !string.IsNullOrEmpty(path) && Path.IsPathRooted(path))
            .ToList();

    /// <summary>Why the analysis must wait now, null when the server is idle.</summary>
    private string? BusyReason(Runtime runtime)
    {
        if (sessions.Sessions.Any(s => s.NowPlayingItem != null))
            return "lecture en cours";
        return runtime.ActivityReason();
    }
}

/// <summary>
/// Admin-only: the results hold the files' paths. Emby rejects non-administrators before the service runs, as for the
/// routes of <see cref="RemoteTranscodeApi"/>.
/// </summary>
public sealed class LibraryDiagnosticApi : IService, IRequiresRequest
{
    private readonly IHttpResultFactory results;

    public LibraryDiagnosticApi(IHttpResultFactory results)
    {
        this.results = results;
    }

    // Set by Emby (IRequiresRequest) before each call.
    public IRequest Request { get; set; } = null!;

    public object Get(GetLibraryDiagnostic request)
    {
        var entries = LibraryDiagnosticResults.Store(Runtime.Instance)?.Snapshot() ?? [];
        return results.GetResult(Request, JsonSerializer.Serialize(
            LibraryDiagnosticReport.Build(entries, LibraryDiagnosticResults.State, request.IncludeFiles != false)), "application/json");
    }
}
