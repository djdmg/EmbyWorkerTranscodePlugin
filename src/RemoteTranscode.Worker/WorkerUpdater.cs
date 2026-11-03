using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using RemoteTranscode.Common;

namespace RemoteTranscode.Worker;

/// <summary>
/// Automatic update of the worker. The worker alone decides and fetches: it reads the latest release of the repository
/// compiled into it, over HTTPS with the system's normal certificate validation (not the coordinator's pinning), on its
/// own HTTP client that never carries the worker token. The URLs are rebuilt from constants (see
/// <see cref="WorkerUpdatePolicy"/>); the coordinator can at most make a check come sooner.
/// One background loop, tray mode only. Nothing here may disturb the work: every failure is logged and ignored, the
/// download stops as soon as a job starts, and the setup only runs on a worker with no job, that refuses new ones.
/// </summary>
public sealed class WorkerUpdater(WorkerHost host, Version current, Action<string> log)
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailedCheckDelay = TimeSpan.FromHours(1);
    private static readonly TimeSpan ReadStall = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(15);
    // Between "this worker refuses jobs" reaching the coordinator and the setup closing the worker.
    private static readonly TimeSpan AnnounceDelay = TimeSpan.FromSeconds(3);
    // The setup looks here for what to update; same key as Setup.UninstallKey.
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\EmbyRemoteWorker";

    // Only the setup in it, so nothing else can be side-loaded from the setup's folder. Emptied by the next worker: the
    // setup closes this process, which cannot delete it.
    private static string DownloadFolder => Path.Combine(WorkerConfig.Directory, "updates");
    private static string StatePath => Path.Combine(WorkerConfig.Directory, "update-state.json");

    private readonly CancellationTokenSource stop = new();
    // Own client: no pinning callback, no redirect followed without IsAllowedDownloadUri, no credentials of any kind.
    private readonly HttpClient http = CreateClient(current);
    // nextCheckUtc and lastHintUtc: the hint arrives on the receive loop. lastCheckedUtc: read by the window.
    private readonly object gate = new();
    private DateTime nextCheckUtc = DateTime.MaxValue;
    private DateTime? lastHintUtc;
    private DateTime? lastCheckedUtc;
    // What GitHub's rate limit last asked: neither a hint nor the switch being turned on again comes before it.
    private DateTime notBeforeUtc;
    private volatile string status = "en attente de la première vérification";

    // Touched by the loop only.
    private string? etag;
    private UpdateRelease? lastRelease;
    private UpdateCandidate? target;
    private string? verifiedSetup;
    private string? expectedHash;
    private bool wasDisabled;
    private (Version Version, DateTime AtUtc)? lastAttempt;

    /// <summary>For the window: a state, with no address, path or secret.</summary>
    public string Status
    {
        get
        {
            lock (gate)
                return lastCheckedUtc is { } at ? $"{status} (dernière vérification : {at.ToLocalTime():dd/MM HH:mm})" : status;
        }
    }

    public void Start() => _ = Task.Run(LoopAsync);

    public void Stop() => stop.Cancel();

    /// <summary>
    /// "Minimum version" sent by the coordinator (any thread). It can only bring the next check forward: what is installed
    /// is decided by that check alone, so a false hint changes nothing but one request to GitHub, at most once an hour.
    /// </summary>
    public void OnCoordinatorHint(string? minimumVersion)
    {
        if (!host.Config.AutoUpdate)
            return;
        lock (gate)
        {
            var now = DateTime.UtcNow;
            if (!WorkerUpdatePolicy.HintTriggersCheck(minimumVersion, current, lastHintUtc, now))
                return;
            lastHintUtc = now;
            if (nextCheckUtc != DateTime.MaxValue)
                nextCheckUtc = Earliest(now);
        }
        log("mises à jour : le serveur demande une version plus récente, vérification avancée");
    }

    private DateTime Earliest(DateTime now) => now > notBeforeUtc ? now : notBeforeUtc;

    private static HttpClient CreateClient(Version version)
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"EmbyRemoteWorker/{version.Major}.{version.Minor}.{version.Build}");
        return client;
    }

    private async Task LoopAsync()
    {
        try
        {
            if (Unsupported() is { } reason)
            {
                SetStatus(reason);
                return;
            }
            ReadAttemptAtStart();
            // Also leaves the setup that relaunched this worker time to end, so that its file can be deleted.
            await Task.Delay(WorkerUpdatePolicy.StartDelay(Random.Shared.NextDouble()), stop.Token);
            CleanDownloads();
            lock (gate)
                nextCheckUtc = DateTime.UtcNow;
            while (true)
            {
                try
                {
                    await TickAsync();
                }
                catch (Exception ex) when (!stop.IsCancellationRequested)
                {
                    // Nothing an update does may take the worker down.
                    SetStatus($"erreur : {ex.Message}");
                }
                await Task.Delay(Tick, stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log($"mises à jour arrêtées : {ex.Message}");
        }
    }

    /// <summary>
    /// The setup acts on the registered installation of the default user folder and closes the worker through its tray
    /// events: a copy elsewhere, or a test instance (EMBY_WORKER_HOME), must leave it alone.
    /// </summary>
    private static string? Unsupported()
    {
        if (Environment.GetEnvironmentVariable("EMBY_WORKER_HOME") is { Length: > 0 })
            return "inactives (instance de test)";
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return WorkerUpdatePolicy.IsInstalledCopy(Path.GetDirectoryName(Environment.ProcessPath), key?.GetValue("InstallLocation") as string)
            ? null
            : "inactives (cette copie n'est pas celle qui est installée)";
    }

    private async Task TickAsync()
    {
        var now = DateTime.UtcNow;
        if (!host.Config.AutoUpdate)
        {
            // Off means off: no contact with GitHub at all.
            SetStatus("désactivées dans les paramètres");
            wasDisabled = true;
            target = null;
            if (verifiedSetup != null)
                DropVerifiedSetup();
            return;
        }
        if (wasDisabled)
        {
            wasDisabled = false;
            lock (gate)
                nextCheckUtc = Earliest(now);
        }
        DateTime next;
        lock (gate)
            next = nextCheckUtc;
        if (now >= next)
            await CheckAsync();
        if (target is not { } update)
            return;
        if (lastAttempt is { } last && !WorkerUpdatePolicy.MayAttempt(update.Version, last.Version, last.AtUtc, now))
        {
            SetStatus($"la version {Text(update.Version)} a échoué : nouvelle tentative après le {(last.AtUtc + WorkerUpdatePolicy.RetryWindow).ToLocalTime():dd/MM HH:mm}");
            return;
        }
        if (!host.IsIdleForUpdate())
        {
            SetStatus(Waiting(update));
            return;
        }
        try
        {
            if (verifiedSetup is null && !await DownloadAsync(update))
                return;
            if (host.IsIdleForUpdate())
                await ApplyAsync(update);
            else
                SetStatus(Waiting(update));
        }
        catch (UpdateRejectedException ex)
        {
            // Bad content for this version: not asked for again before the retry window, or it would be downloaded in a loop.
            log($"mises à jour : version {Text(update.Version)} refusée : {ex.Message}");
            DropVerifiedSetup();
            RecordAttempt(update.Version);
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            // Network, disk: nothing is known against this version, it comes back with the next check (not at the next tick).
            SetStatus($"téléchargement de la version {Text(update.Version)} impossible : {ex.Message}");
            DropVerifiedSetup();
            target = null;
        }
    }

    private async Task CheckAsync()
    {
        var now = DateTime.UtcNow;
        var next = now + WorkerUpdatePolicy.NextCheckDelay(Random.Shared.NextDouble());
        try
        {
            if (verifiedSetup is null)
                CleanDownloads();
            using var request = new HttpRequestMessage(HttpMethod.Get, WorkerUpdatePolicy.LatestReleaseUrl);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            if (etag != null && lastRelease != null)
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            int code = (int)response.StatusCode;
            if (WorkerUpdatePolicy.RateLimitWait(code, response.Headers.RetryAfter?.Delta, HeaderNumber(response, "X-RateLimit-Remaining"),
                    HeaderNumber(response, "X-RateLimit-Reset"), now) is { } wait)
            {
                lock (gate)
                    notBeforeUtc = now + wait;
                if (now + wait > next)
                    next = now + wait;
            }
            UpdateRelease? release;
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                release = lastRelease;
            }
            else if (response.StatusCode == HttpStatusCode.OK)
            {
                byte[] body = await ReadCappedAsync(response.Content, WorkerUpdatePolicy.MaxApiResponseBytes, timeout.Token);
                release = WorkerUpdatePolicy.ParseRelease(Encoding.UTF8.GetString(body));
                lastRelease = release;
                etag = response.Headers.ETag?.ToString();
            }
            else
            {
                // A redirect too: a repository renamed or moved stops the updates, it is not followed.
                SetStatus($"vérification impossible (réponse {code} de GitHub)");
                return;
            }
            if (release is null)
                return;
            lock (gate)
                lastCheckedUtc = now;
            var candidate = WorkerUpdatePolicy.SelectUpdate(current, release, out string reason);
            if (candidate?.SetupName != target?.SetupName && verifiedSetup != null)
                DropVerifiedSetup();
            target = candidate;
            if (candidate is null)
                SetStatus(reason == WorkerUpdatePolicy.UpToDateReason ? $"à jour (version {Text(current)})" : $"aucune mise à jour applicable : {reason}");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or FormatException or OperationCanceledException && !stop.IsCancellationRequested)
        {
            // No network at logon, GitHub down...: tried again sooner than the normal interval, never in a loop.
            SetStatus($"vérification impossible : {ex.Message}");
            var soon = now + FailedCheckDelay;
            if (soon < next)
                next = soon;
        }
        finally
        {
            lock (gate)
                nextCheckUtc = next;
        }
    }

    /// <summary>
    /// Fetches the .sha256 then the setup into a file only renamed to its final name once its hash and size are right.
    /// False when a job started meanwhile: that job goes first, the download starts again at the next free moment.
    /// </summary>
    private async Task<bool> DownloadAsync(UpdateCandidate update)
    {
        SetStatus($"téléchargement de la version {Text(update.Version)}");
        CleanDownloads();
        Directory.CreateDirectory(DownloadFolder);

        string expected;
        using (var total = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            total.CancelAfter(TimeSpan.FromMinutes(2));
            using var hashResponse = await GetFollowingRedirectsAsync(update.HashUrl, total.Token);
            byte[] content = await ReadCappedAsync(hashResponse.Content, WorkerUpdatePolicy.MaxHashFileBytes, total.Token);
            expected = WorkerUpdatePolicy.ParseSha256File(Encoding.UTF8.GetString(content), update.SetupName)
                ?? throw new UpdateRejectedException("fichier d'empreinte invalide");
        }

        string part = Path.Combine(DownloadFolder, update.SetupName + ".part");
        using var response = await GetFollowingRedirectsAsync(update.SetupUrl, stop.Token);
        await using var source = await response.Content.ReadAsStreamAsync(stop.Token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written = 0;
        bool interrupted = false;
        await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            while (true)
            {
                using var read = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                read.CancelAfter(ReadStall);
                int n = await source.ReadAsync(buffer, read.Token);
                if (n == 0)
                    break;
                written += n;
                if (written > update.SetupSize)
                    throw new UpdateRejectedException("fichier plus gros qu'annoncé");
                hash.AppendData(buffer, 0, n);
                await file.WriteAsync(buffer.AsMemory(0, n), stop.Token);
                if (!host.IsIdleForUpdate())
                {
                    interrupted = true;
                    break;
                }
            }
        }
        if (interrupted)
        {
            SetStatus(Waiting(update));
            CleanDownloads();
            return false;
        }
        if (written != update.SetupSize)
            throw new IOException("téléchargement incomplet");
        if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != expected)
            throw new UpdateRejectedException("empreinte SHA-256 différente");
        string final = Path.Combine(DownloadFolder, update.SetupName);
        File.Move(part, final, overwrite: true);
        verifiedSetup = final;
        expectedHash = expected;
        log($"mises à jour : version {Text(update.Version)} téléchargée, empreinte vérifiée");
        return true;
    }

    private async Task ApplyAsync(UpdateCandidate update)
    {
        // Not WorkerService.ReleaseAsync: that stops jobs and persists "not available", which the worker would come back
        // with after the update. This only refuses new jobs, until this process ends.
        if (!await host.TryBeginUpdateAsync())
        {
            SetStatus(Waiting(update));
            return;
        }
        try
        {
            SetStatus($"installation de la version {Text(update.Version)}");
            await Task.Delay(AnnounceDelay, stop.Token);

            // Held from the hash to the start, sharing reads only: what was hashed is what runs.
            using var setup = new FileStream(verifiedSetup!, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Convert.ToHexString(await SHA256.HashDataAsync(setup, stop.Token)).ToLowerInvariant() != expectedHash)
                throw new UpdateRejectedException("le fichier a changé depuis sa vérification");

            // Saved before the start: the setup closes this process, so the next start reads the outcome from here (the
            // version it runs against this one). If it cannot be saved, an update loop could not be stopped: no update.
            if (!RecordAttempt(update.Version))
                return;

            // UseShellExecute false, absolute path, no window. Working directory: not the download folder (it would keep it
            // locked) nor the installation. Not attached to ChildProcessGuard, whose job would kill it with this process.
            var start = new ProcessStartInfo(verifiedSetup!) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory };
            start.ArgumentList.Add("--quiet");
            using var process = Process.Start(start) ?? throw new IOException("le programme d'installation n'a pas démarré");
            log($"mises à jour : programme d'installation de la version {Text(update.Version)} lancé, le worker va se fermer puis redémarrer");
            await setup.DisposeAsync();

            // Normally this process ends here, closed by the setup. Still alive afterwards: the setup gave up before that.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            timeout.CancelAfter(SetupTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                log($"mises à jour : la version {Text(update.Version)} n'a pas été installée (code {process.ExitCode}) ; voir EmbyRemoteWorker-Setup.log dans le dossier temporaire");
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                log($"mises à jour : la version {Text(update.Version)} ne s'est pas installée dans le délai ; le worker reprend");
            }
        }
        finally
        {
            host.EndUpdate();
            if (!stop.IsCancellationRequested)
                DropVerifiedSetup();
        }
    }

    private async Task<HttpResponseMessage> GetFollowingRedirectsAsync(string url, CancellationToken token)
    {
        var uri = new Uri(url);
        for (int hop = 0; hop < 5; hop++)
        {
            if (!WorkerUpdatePolicy.IsAllowedDownloadUri(uri))
                throw new UpdateRejectedException("redirection vers une adresse non autorisée");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                response.Dispose();
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode;
                response.Dispose();
                // The status only: the URLs of the storage carry signatures, they stay out of the logs.
                throw new IOException($"réponse {code} de GitHub");
            }
            return response;
        }
        throw new UpdateRejectedException("trop de redirections");
    }

    private static async Task<byte[]> ReadCappedAsync(HttpContent content, int max, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (await stream.ReadAsync(buffer, token) is var n && n > 0)
        {
            if (memory.Length + n > max)
                throw new FormatException("réponse trop longue");
            memory.Write(buffer, 0, n);
        }
        return memory.ToArray();
    }

    private static long? HeaderNumber(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && long.TryParse(values.FirstOrDefault(), out long v) ? v : null;

    private sealed record AttemptFile(string Version, DateTime AtUtc);

    /// <summary>
    /// What became of the attempt saved before the setup ran: this version at least as new, it worked; otherwise it failed
    /// and that version waits out the retry window, whatever closed this worker in between. An unreadable file counts as
    /// no attempt.
    /// </summary>
    private void ReadAttemptAtStart()
    {
        try
        {
            if (!File.Exists(StatePath))
                return;
            var saved = JsonSerializer.Deserialize<AttemptFile>(File.ReadAllText(StatePath));
            if (WorkerUpdatePolicy.ParseVersion(saved?.Version) is not { } version)
                return;
            if (WorkerUpdatePolicy.IsNewer(version, current))
            {
                lastAttempt = (version, DateTime.SpecifyKind(saved!.AtUtc, DateTimeKind.Utc));
                log($"mises à jour : la tentative vers la version {Text(version)} n'a pas abouti (voir EmbyRemoteWorker-Setup.log dans le dossier temporaire)");
            }
            else
            {
                log($"mises à jour : passage à la version {Text(version)} terminé");
                File.Delete(StatePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            log($"mises à jour : état illisible : {ex.Message}");
        }
    }

    /// <summary>The attempt counts at once in memory, even if it cannot be saved. False when it could not.</summary>
    private bool RecordAttempt(Version version)
    {
        var now = DateTime.UtcNow;
        lastAttempt = (version, now);
        try
        {
            Directory.CreateDirectory(WorkerConfig.Directory);
            string temp = StatePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new AttemptFile(Text(version), now)));
            File.Move(temp, StatePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"mises à jour : état non enregistré, mise à jour abandonnée : {ex.Message}");
            return false;
        }
    }

    private void DropVerifiedSetup()
    {
        verifiedSetup = null;
        expectedHash = null;
        CleanDownloads();
    }

    /// <summary>Best effort: a setup still ending keeps its file for a moment, the next check takes it.</summary>
    private static void CleanDownloads()
    {
        try
        {
            if (Directory.Exists(DownloadFolder))
                Directory.Delete(DownloadFolder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Only a change reaches the log: this is called every 30 s while waiting.</summary>
    private void SetStatus(string text)
    {
        if (text == status)
            return;
        status = text;
        log($"mises à jour : {text}");
    }

    private static string Waiting(UpdateCandidate update) => $"version {Text(update.Version)} disponible : en attente d'un moment sans tâche";

    private static string Text(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";

    /// <summary>The content is wrong for this version (as opposed to a network failure): covered by the retry window.</summary>
    private sealed class UpdateRejectedException(string message) : Exception(message);
}
