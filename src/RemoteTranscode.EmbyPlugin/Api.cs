using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using ILogger = MediaBrowser.Model.Logging.ILogger;
using MediaBrowser.Model.Services;

namespace RemoteTranscode.EmbyPlugin;

// Request DTOs of Emby's ServiceStack-style API. Every route is [Authenticated(Roles = "Admin")]: Emby rejects
// non-administrators before the service runs. IReturnVoid only describes the route; the handlers return raw JSON.

[Route("/RemoteTranscode/Status", "GET", Summary = "Workers, jobs and encodes of the distributed transcoding")]
[Authenticated(Roles = "Admin")]
public sealed class GetStatus : IReturnVoid { }

[Route("/RemoteTranscode/Workers/Pair", "POST", Summary = "Creates a worker identity and returns its pairing code")]
[Authenticated(Roles = "Admin")]
public sealed class PairWorker : IReturnVoid
{
    public string Name { get; set; } = "";
}

[Route("/RemoteTranscode/Workers/{Id}/Revoke", "POST", Summary = "Revokes a worker; it is disconnected at once")]
[Authenticated(Roles = "Admin")]
public sealed class RevokeWorker : IReturnVoid
{
    public string Id { get; set; } = "";
}

[Route("/RemoteTranscode/Settings", "GET", Summary = "Plugin settings used by the administration page")]
[Authenticated(Roles = "Admin")]
public sealed class GetSettings : IReturnVoid { }

[Route("/RemoteTranscode/Settings", "POST", Summary = "Saves the worker endpoint settings and restarts the coordinator")]
[Authenticated(Roles = "Admin")]
public sealed class SaveSettings : IReturnVoid
{
    public string WorkerUrl { get; set; } = "";
    public string? PublicUrl { get; set; }
    public string? AdvertisedHost { get; set; }
}

[Route("/RemoteTranscode/Enabled", "POST", Summary = "Switches the interception of Emby's ffmpeg runs on or off")]
[Authenticated(Roles = "Admin")]
public sealed class SetEnabled : IReturnVoid
{
    public bool Enabled { get; set; }
}

[Route("/RemoteTranscode/AudioAhead", "POST", Summary = "Switches the audio converted ahead on or off (experimental, off by default)")]
[Authenticated(Roles = "Admin")]
public sealed class SetAudioAhead : IReturnVoid
{
    public bool Enabled { get; set; }
}

[Route("/RemoteTranscode/ReportFailures", "POST", Summary = "Switches the anonymous GitHub reports of failed conversions on or off")]
[Authenticated(Roles = "Admin")]
public sealed class SetReportFailures : IReturnVoid
{
    public bool Enabled { get; set; }
}

/// <summary>
/// Admin-only relay to the coordinator's loopback API. Emby performs the authentication (administrators only);
/// the coordinator only accepts loopback calls carrying the plugin secret.
/// </summary>
public sealed class RemoteTranscodeApi : IService, IRequiresRequest
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly IHttpResultFactory results;
    private readonly IServerConfigurationManager serverConfiguration;
    private readonly ILogger logger;

    public RemoteTranscodeApi(IHttpResultFactory results, IServerConfigurationManager serverConfiguration, ILogManager logManager)
    {
        this.results = results;
        this.serverConfiguration = serverConfiguration;
        logger = logManager.GetLogger("RemoteTranscode");
    }

    // Set by Emby (IRequiresRequest) before each call; needed to build results and set status codes.
    public IRequest Request { get; set; } = null!;

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public Task<object> Get(GetStatus request) => RelayAsync(HttpMethod.Get, "/admin/status", null);

    public object Get(GetSettings request) => SettingsResult();

    public async Task<object> Post(SetEnabled request)
    {
        var plugin = Plugin.Instance;
        var runtime = Runtime.Instance;
        if (plugin is null || runtime is null)
            return Error(HttpStatusCode.ServiceUnavailable, "plugin non initialisé");
        // Applied, then saved only if Emby accepted it, under the runtime's start gate (see SetEnabledAsync).
        string? problem = await runtime.SetEnabledAsync(request.Enabled).ConfigureAwait(false);
        return problem != null ? Error(HttpStatusCode.Conflict, problem) : SettingsResult();
    }

    /// <summary>Applied to the conversions captured from now on, no restart (see Runtime.SetAudioAhead).</summary>
    public object Post(SetAudioAhead request)
    {
        var runtime = Runtime.Instance;
        if (Plugin.Instance is null || runtime is null)
            return Error(HttpStatusCode.ServiceUnavailable, "plugin non initialisé");
        runtime.SetAudioAhead(request.Enabled);
        return SettingsResult();
    }

    /// <summary>Applied to the running coordinator at once, no restart (see Runtime.SetReportFailures).</summary>
    public object Post(SetReportFailures request)
    {
        var runtime = Runtime.Instance;
        if (Plugin.Instance is null || runtime is null)
            return Error(HttpStatusCode.ServiceUnavailable, "plugin non initialisé");
        runtime.SetReportFailures(request.Enabled);
        return SettingsResult();
    }

    public async Task<object> Post(SaveSettings request)
    {
        var plugin = Plugin.Instance;
        var runtime = Runtime.Instance;
        if (plugin is null || runtime is null)
            return Error(HttpStatusCode.ServiceUnavailable, "plugin non initialisé");
        string workerUrl = (request.WorkerUrl ?? "").Trim();
        if (!Uri.TryCreate(workerUrl, UriKind.Absolute, out var worker) || worker.Scheme is not ("http" or "https") || worker.Port <= 0
            || worker.AbsolutePath != "/" || !string.IsNullOrEmpty(worker.Query))
            return Error(HttpStatusCode.BadRequest, "adresse d'écoute des workers invalide (ex. https://0.0.0.0:8097)");
        // What the coordinator's Kestrel listener accepts (CoordinatorHost: IPAddress.Parse, "localhost" as loopback).
        if (worker.Host != "localhost" && !IPAddress.TryParse(worker.Host, out _))
            return Error(HttpStatusCode.BadRequest, "l'adresse d'écoute doit être une adresse IP (0.0.0.0 pour toutes, 127.0.0.1 derrière un reverse proxy), pas un nom d'hôte");
        var emby = serverConfiguration.Configuration;
        if (worker.Port == Config.ShimPort || worker.Port == emby.HttpServerPortNumber || worker.Port == emby.HttpsPortNumber)
            return Error(HttpStatusCode.BadRequest, $"le port {worker.Port} est déjà utilisé par Emby ou par le relais : choisissez-en un autre (ex. 8097)");
        string? publicUrl = string.IsNullOrWhiteSpace(request.PublicUrl) ? null : request.PublicUrl.Trim();
        if (publicUrl != null && (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var pub) || pub.Scheme != "https"))
            return Error(HttpStatusCode.BadRequest, "adresse publique invalide (https://...)");
        if (worker.Scheme == "http" && publicUrl is null)
            return Error(HttpStatusCode.BadRequest, "sans TLS (http), une adresse publique https (reverse proxy) est nécessaire");
        string? advertised = string.IsNullOrWhiteSpace(request.AdvertisedHost) ? null : request.AdvertisedHost.Trim();
        if (advertised != null && Uri.CheckHostName(advertised) is UriHostNameType.Unknown or UriHostNameType.Basic)
            return Error(HttpStatusCode.BadRequest, "adresse annoncée invalide (nom d'hôte ou adresse IP, sans http:// ni port)");

        var config = plugin.Configuration;
        // Unchanged: no restart, which would cut the running jobs off their workers for nothing.
        if (config.WorkerUrl == workerUrl && config.PublicUrl == publicUrl && config.AdvertisedHost == advertised)
            return SettingsResult();
        (string WorkerUrl, string? PublicUrl, string? AdvertisedHost) previous = (config.WorkerUrl, config.PublicUrl, config.AdvertisedHost);
        config.WorkerUrl = workerUrl;
        config.PublicUrl = publicUrl;
        config.AdvertisedHost = advertised;
        // Restarts the coordinator with the new endpoint: running jobs lose their worker and fall back to Emby's
        // ffmpeg; if the new endpoint cannot be served, Emby is switched back to its own ffmpeg (see StartAsync).
        await runtime.StartAsync().ConfigureAwait(false);
        if (!runtime.Running)
        {
            // Not kept: back to the previous settings (which ran, or at least were the administrator's last choice).
            string reason = runtime.Error ?? "démarrage impossible";
            (config.WorkerUrl, config.PublicUrl, config.AdvertisedHost) = previous;
            await runtime.StartAsync().ConfigureAwait(false);
            return Error(HttpStatusCode.Conflict, $"réglages refusés, les précédents sont rétablis : {reason}");
        }
        plugin.SaveConfiguration();
        return SettingsResult();
    }

    /// <summary>
    /// State shown by the page. Booleans are sent as the strings "true"/"false" (the writer below only knows strings);
    /// the page compares them as such.
    /// </summary>
    private object SettingsResult()
    {
        var interception = Interception.Instance;
        var runtime = Runtime.Instance;
        string? dispatcher = runtime?.DispatcherPath;
        return results.GetResult(Request, Json(new (string, string?)[]
        {
            ("dispatcherPath", dispatcher),
            ("embyFfmpeg", runtime?.RealFfmpeg),
            ("dataDir", runtime?.DataDir),
            ("running", runtime?.Running == true ? "true" : "false"),
            ("error", runtime?.Error),
            ("workerUrl", Config.WorkerUrl),
            ("publicUrl", Config.PublicUrl),
            ("advertisedHost", Config.AdvertisedHost),
            ("enabled", Config.Enabled ? "true" : "false"),
            ("active", dispatcher != null && interception?.IsActive(dispatcher) == true ? "true" : "false"),
            ("encoderPath", interception?.CurrentEncoderPath),
            // Whether this build has a report token, never the token itself.
            ("reportFailures", Config.ReportFailures ? "true" : "false"),
            ("reportFailuresState", !Runtime.HasIssueReportToken ? "notoken" : Config.ReportFailures ? "active" : "disabled"),
        }), "application/json");
    }

    public Task<object> Post(PairWorker request) => RelayAsync(HttpMethod.Post, "/admin/workers/pair", Json(new (string, string?)[] { ("name", request.Name) }));

    public Task<object> Post(RevokeWorker request) => RelayAsync(HttpMethod.Post, $"/admin/workers/{Uri.EscapeDataString(request.Id)}/revoke", null);

    /// <summary>
    /// Forwards to the coordinator's loopback admin API with the plugin secret and returns its JSON unchanged.
    /// Errors are returned with the coordinator's status code and its body as the "error" text (401/403 become 502).
    /// </summary>
    private async Task<object> RelayAsync(HttpMethod method, string path, string? jsonBody)
    {
        var runtime = Runtime.Instance;
        if (runtime is null || !runtime.Running)
            return Error(HttpStatusCode.ServiceUnavailable, "coordinateur arrêté : " + (runtime?.Error ?? "démarrage en cours"));
        string? secret = runtime.PluginSecret;
        if (secret is null)
            return Error(HttpStatusCode.ServiceUnavailable, "secret du coordinateur absent");
        using var message = new HttpRequestMessage(method, runtime.AdminUrl + path);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        if (jsonBody != null)
            message.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        try
        {
            using var response = await Http.SendAsync(message).ConfigureAwait(false);
            byte[] body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            // The coordinator refusing the plugin's own secret is a server-side fault: passed through as 401/403, Emby's
            // web app would take it for the administrator's session expiring and log them out.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Error(HttpStatusCode.BadGateway, $"le coordinateur refuse le secret du plugin (code {(int)response.StatusCode}) : redémarrez Emby");
            if (!response.IsSuccessStatusCode)
                return Error(response.StatusCode, Encoding.UTF8.GetString(body));
            // Never an empty body: the page's ApiClient parses the answer as JSON and would report a success as an error.
            if (body.Length == 0)
                body = "{}"u8.ToArray();
            return results.GetResult(Request, new ReadOnlyMemory<byte>(body), "application/json");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Error(HttpStatusCode.ServiceUnavailable, $"coordinateur injoignable : {ex.Message}");
        }
    }

    private object Error(HttpStatusCode status, string message)
    {
        Request.Response.StatusCode = (int)status;
        return results.GetResult(Request, Json(new[] { ("error", (string?)message) }), "application/json");
    }

    /// <summary>Minimal JSON object writer (string values only), avoiding a serializer dependency.</summary>
    private static string Json((string Key, string? Value)[] fields)
    {
        var sb = new StringBuilder("{");
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append('"').Append(fields[i].Key).Append("\":");
            if (fields[i].Value is null)
                sb.Append("null");
            else
                AppendString(sb, fields[i].Value!);
        }
        return sb.Append('}').ToString();
    }

    /// <summary>JSON string literal: quotes, backslashes and control characters escaped (RFC 8259 minimum).</summary>
    private static void AppendString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
