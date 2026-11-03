using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using RemoteTranscode.Common;
using Message = RemoteTranscode.Common.Message;

namespace RemoteTranscode.Worker;

/// <summary>
/// Keeps an outbound, pinned-TLS WebSocket to the coordinator (no inbound port on the PC), reports state and load,
/// and accepts or refuses job offers.
/// </summary>
public sealed partial class WorkerService(WorkerConfig config, WorkerCapabilities capabilities, Action<string> log)
{
    // Touched by the receive loop (offers), each job's own task (removal), the availability loop and the UI timer.
    private readonly ConcurrentDictionary<string, JobRunner> jobs = new();
    // Only used by the availability loop (PDH queries are not shared between threads).
    private readonly SystemLoad load = new();
    // ClientWebSocket allows a single outstanding send: heartbeats, progress and answers come from several threads.
    private readonly SemaphoreSlim sendLock = new(1, 1);
    // Only evaluated by the availability loop; its Settings are swapped by ApplySettings from the UI thread.
    private readonly AvailabilityEngine engine = new(config.Availability);
    // Replaced on every reconnection; readers take a local copy (see SendAsync).
    private ClientWebSocket? socket;
    // Recreated per connection so the pinning callback and pooled connections follow the current session. Jobs keep
    // the instance they were started with.
    private HttpClient? http;

    // One cache for the process (services are recreated on reconnection and settings changes, the cache stays).
    private static InputCache? sharedCache;
    private static readonly object CacheGate = new();

    /// <summary>
    /// Versions 0.7.3 to 0.7.6 kept input blocks directly in cache\(size-hash) folders: removed once, they
    /// would otherwise take up to the cache's capacity for good.
    /// </summary>
    private static void RemoveOldInputBlocks(string cacheFolder)
    {
        if (!Directory.Exists(cacheFolder))
            return;
        foreach (string dir in Directory.EnumerateDirectories(cacheFolder))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(dir), "^[0-9]+-[0-9a-f]{16}$"))
                continue;
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The input cache (conversions and playback), created on first use; null if it cannot start (inputs are then read directly).</summary>
    private InputCache? Cache
    {
        get
        {
            lock (CacheGate)
            {
                if (sharedCache != null)
                    return sharedCache;
                try
                {
                    // Its own folder: cacheonts (filter fonts kept between jobs) must survive the clean-up at start.
                    string folder = Path.Combine(WorkerConfig.Directory, "cache", "inputs");
                    RemoveOldInputBlocks(Path.Combine(WorkerConfig.Directory, "cache"));
                    // At most 4 GB, and never more than a quarter of the free space.
                    long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
                    sharedCache = new InputCache(folder, Math.Min(4L << 30, free / 4), log);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
                {
                    log($"cache local indisponible ({ex.Message}) : lecture directe des sources");
                }
                return sharedCache;
            }
        }
    }
    // When the current WebSocket session was established; read by RunForeverAsync for the backoff reset.
    private DateTime? connectedAt;
    private volatile AvailabilityDecision decision = new(WorkerState.Offline, "démarrage", ReclaimAction.None);
    private WorkerLoad lastLoad = new(0, null, 0, 0);
    // An offer is checked and registered as one step, and an update starts only with no job registered: never both.
    private readonly object offerGate = new();
    private volatile bool updateDraining;
    private long lastJobActivityTicks = DateTime.UtcNow.Ticks;
    private const string UpdateReason = "mise à jour en cours";

    public IReadOnlyCollection<JobRunner> Jobs => jobs.Values.ToArray();
    public bool Connected => socket?.State == WebSocketState.Open;
    public WorkerLoad LastLoad => lastLoad;

    /// <summary>Items left in Emby's conversion queue and when the server said so (with conversion progress); null until then.</summary>
    public (int Remaining, DateTime At)? ConversionQueue { get; private set; }
    public AvailabilityDecision Decision => decision;

    /// <summary>Applies edited settings without reconnecting (availability rules, max jobs).</summary>
    public void ApplySettings() => engine.Settings = config.Availability;

    /// <summary>
    /// "Libérer le PC": the worker stops being available first (and says so right away), so the coordinator does not hand
    /// the released playback straight back to it, then every job stops; playback resumes on another worker or the server.
    /// </summary>
    public async Task ReleaseAsync()
    {
        Enabled = false;
        decision = new AvailabilityDecision(WorkerState.Unavailable, "libéré par l'utilisateur", ReclaimAction.None);
        await SendAsync(Heartbeat());
        await StopAllJobsAsync("released: libération demandée par l'utilisateur");
    }

    /// <summary>"Minimum version" hint of the coordinator; set by the host, handled by the updater.</summary>
    public Action<string>? UpdateHintReceived { get; set; }

    /// <summary>No job at all, and none for a while (a gap between two slices of a conversion is not a free moment).</summary>
    public bool IsIdleForUpdate() =>
        WorkerUpdatePolicy.IsIdle(jobs.Count, new DateTime(Interlocked.Read(ref lastJobActivityTicks), DateTimeKind.Utc), DateTime.UtcNow);

    /// <summary>
    /// Refuses new jobs from now on if there is none running, else false: an update never stops a job. Unlike a release,
    /// nothing is persisted (Enabled stays as it is), so the worker comes back available after the update.
    /// </summary>
    public bool TryBeginUpdateDrain()
    {
        lock (offerGate)
        {
            if (!jobs.IsEmpty)
                return false;
            updateDraining = true;
            decision = new AvailabilityDecision(WorkerState.Unavailable, UpdateReason, ReclaimAction.None);
            return true;
        }
    }

    /// <summary>Tells the coordinator now rather than at the next heartbeat, so it does not offer a job in vain.</summary>
    public Task AnnounceStateAsync() => SendAsync(Heartbeat());

    /// <summary>The update did not happen: the availability loop gives the real state back within a second.</summary>
    public void EndUpdateDrain() => updateDraining = false;

    private void MarkJobActivity() => Interlocked.Exchange(ref lastJobActivityTicks, DateTime.UtcNow.Ticks);

    /// <summary>Stops every running job (application exit or explicit release) and waits briefly for them.</summary>
    public async Task StopAllJobsAsync(string reason)
    {
        foreach (var job in jobs.Values)
            job.Stop(reason);
        for (int i = 0; i < 50 && !jobs.IsEmpty; i++)
            await Task.Delay(100);
    }

    /// <summary>Current decision of the availability engine; read for every offer and heartbeat.</summary>
    public WorkerState State => decision.State;
    public string? StateReason => decision.Reason;
    public event Action<AvailabilityDecision>? StateChanged;

    /// <summary>User switch (manual mode, or global on/off in automatic mode).</summary>
    public bool Enabled
    {
        get => config.Available;
        set
        {
            config.Available = value;
            // The switch applies now even if it cannot be persisted (it is then lost at the next restart).
            try { config.Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log($"configuration non enregistrée : {ex.Message}"); }
        }
    }

    [GeneratedRegex("^[0-9a-f]{16}$")]
    private static partial Regex JobIdFormat();

    /// <summary>
    /// Connection loop with exponential backoff (1 s doubling up to 30 s). The availability loop runs alongside for the
    /// whole lifetime, connected or not, so the tray always shows a current state.
    /// </summary>
    public async Task RunForeverAsync(CancellationToken token)
    {
        _ = Task.Run(() => AvailabilityLoopAsync(token), token);
        // Created at start rather than at the first job: the clean-up of older versions' blocks happens right away, and
        // a prefetch arriving before any job finds the cache ready.
        _ = Task.Run(() => Cache, token);
        var backoff = TimeSpan.FromSeconds(1);
        while (!token.IsCancellationRequested)
        {
            connectedAt = null;
            try
            {
                await ConnectAndServeAsync(token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                log($"connection to coordinator lost: {ex.Message}");
            }
            // Only a connection that held for a while proves the coordinator is healthy; one that is accepted then dropped
            // right away (coordinator restarting, rejecting this worker) keeps backing off.
            if (connectedAt is { } since && DateTime.UtcNow - since > TimeSpan.FromSeconds(60))
                backoff = TimeSpan.FromSeconds(1);
            // Jobs cannot outlive the connection: the coordinator has already handed them over elsewhere.
            foreach (var job in jobs.Values)
                job.Stop("released: connexion au coordinateur perdue");
            await Task.Delay(backoff, token);
            backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
        }
    }

    /// <summary>One session: connect, say hello, then handle messages until the coordinator closes or the socket fails.</summary>
    private async Task ConnectAndServeAsync(CancellationToken token)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = ValidatePinned },
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        // Jobs of the previous session are still stopping and uploading their last segments with the old client: it is
        // disposed only once they have all ended.
        if (http is { } previous)
        {
            var users = jobs.Values.Select(j => j.Completion).ToArray();
            _ = Task.WhenAll(users).ContinueWith(_ => previous.Dispose(), TaskScheduler.Default);
        }
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };

        var ws = new ClientWebSocket();
        socket = ws;
        try
        {
            ws.Options.RemoteCertificateValidationCallback = ValidatePinned;
            ws.Options.SetRequestHeader("Authorization", "Bearer " + config.GetToken());
            // WebSocket pings keep NAT/proxy idle timers from silently dropping the connection between heartbeats.
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
            var wsUri = new Uri(config.CoordinatorUrl!.Replace("https://", "wss://") + "/ws/worker");
            await ws.ConnectAsync(wsUri, token);
            connectedAt = DateTime.UtcNow;
            log($"connected to {config.CoordinatorUrl}");

            await SendAsync(new WorkerHello(config.WorkerId!, config.Name, typeof(WorkerService).Assembly.GetName().Version?.ToString() ?? "0", capabilities, config.MaxJobs, CurrentState()));
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var heartbeat = HeartbeatLoopAsync(heartbeatCts.Token);
            try
            {
                while (await ReceiveAsync(ws, token) is { } message)
                    await HandleAsync(message);
            }
            finally
            {
                heartbeatCts.Cancel();
                await heartbeat.ContinueWith(_ => { });
            }
        }
        finally
        {
            // Senders on other threads hold a copy: they see a non-open state (or ObjectDisposedException, caught).
            ws.Dispose();
        }
    }

    /// <summary>
    /// Runs on the receive loop: must not block. Jobs run on their own task; other message types are ignored.
    /// </summary>
    private async Task HandleAsync(Message message)
    {
        switch (message)
        {
            case JobOffer offer:
                var inputCache = Cache;
                string? refusal;
                JobRunner? runner = null;
                lock (offerGate)
                {
                    refusal = CheckOffer(offer);
                    if (refusal is null)
                    {
                        runner = new JobRunner(offer, config, capabilities, http!, SendAsync, log, inputCache);
                        // Registered before answering so the slot is counted by the next heartbeat and offer check.
                        jobs[offer.JobId] = runner;
                        MarkJobActivity();
                    }
                }
                if (runner is null)
                {
                    log($"refused job {offer.JobId}: {refusal}");
                    await SendAsync(new JobAnswer(offer.JobId, false, refusal));
                    return;
                }
                await SendAsync(new JobAnswer(offer.JobId, true, null));
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await runner.RunAsync();
                    }
                    catch (Exception ex)
                    {
                        log($"job {offer.JobId}: {ex.Message}");
                    }
                    finally
                    {
                        jobs.TryRemove(offer.JobId, out _);
                        MarkJobActivity();
                    }
                });
                break;
            case ConversionProgress progress:
                // Also sent without a job (empty JobId) every 30 s, for the queue length alone.
                if (progress.QueueRemaining is { } queue)
                    ConversionQueue = (queue, DateTime.Now);
                if (jobs.TryGetValue(progress.JobId, out var running))
                {
                    running.TotalPercent = progress.Percent;
                    running.TotalRemainingSeconds = progress.RemainingSeconds;
                }
                break;
            case CancelJob cancel when jobs.TryGetValue(cancel.JobId, out var job):
                log($"job {cancel.JobId}: stop requested ({cancel.Reason})");
                // Asked by the coordinator: the only stop that ends with exit 0 (see JobRunner.RunAsync).
                job.Cancel(cancel.Reason);
                break;
            case PrefetchHint hint when Cache is { } cache && http is { } client && config.CoordinatorUrl is { } url:
                // A playback this worker is expected to take over: its source is fetched ahead into the cache.
                MarkJobActivity();
                string key = InputCache.KeyOf(hint.File.Name, hint.File.Size);
                cache.Attach(key, hint.File.Size, $"{url.TrimEnd('/')}/prefetch/{hint.GrantId}", hint.Token, client);
                cache.Prefetch(key, hint.Ranges.Select(r => (r.Offset, r.Length)));
                log($"préchargement de {hint.File.Name} pour une lecture à reprendre ({hint.Ranges.Sum(r => r.Length) >> 20} Mo)");
                break;
            case WorkerUpdateHint updateHint:
                UpdateHintReceived?.Invoke(updateHint.MinimumVersion);
                break;
        }
    }

    /// <summary>Every condition is re-checked here even though the coordinator only offers to available workers.</summary>
    private string? CheckOffer(JobOffer offer)
    {
        // The job id becomes a directory name under JobsDirectory: only accept the coordinator's 16-hex format.
        if (!JobIdFormat().IsMatch(offer.JobId))
            return "malformed job id";
        if (updateDraining)
            return UpdateReason;
        // "Busy" only means "all slots used" when last evaluated (once a second); the live counts decide below.
        var state = CurrentState();
        if (state is not (WorkerState.Available or WorkerState.Busy))
            return $"worker is {state}" + (StateReason is null ? "" : $" ({StateReason})");
        // Background encodes never block a playback (the coordinator moves them away if the PC cannot carry both);
        // a background encode only takes a slot nobody uses.
        if (offer.Kind == JobKind.Playback ? ActivePlaybacks >= config.MaxJobs : ActiveJobs >= config.MaxJobs)
            return "no free slot";
        if (!ArgTemplateValidator.Validate(offer.Args, out string? error))
            return "invalid job: " + error;
        // A conversion tone mapped on the GPU by the coordinator cannot fall back to the CPU here (its slices must all
        // look alike): refused when this worker's GPU tone mapping is off or suspended after failures.
        if (EmbyFilters.UsesLibplacebo(offer.Args) && (capabilities.GpuToneMapping?.IsLibplacebo != true || JobRunner.GpuToneMappingSuspended))
            return "tone mapping GPU indisponible sur ce worker" + (JobRunner.GpuToneMappingSuspended ? " (suspendu après des échecs)" : "");
        return null;
    }

    private WorkerState CurrentState() => decision.State;

    /// <summary>
    /// Re-evaluates availability every second. When the owner needs the PC back the configured behaviour is applied:
    /// "release" stops every job (the coordinator resumes playback elsewhere or locally), "drain" refuses new jobs and
    /// lets running playback finish.
    /// </summary>
    private async Task AvailabilityLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Fire-and-forget loop: one bad iteration must neither end it silently nor freeze the state.
                try
                {
                    await EvaluateAvailabilityAsync();
                }
                catch (Exception ex)
                {
                    log($"évaluation de la disponibilité en échec : {ex.Message}");
                }
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        finally
        {
            load.Dispose();
        }
    }

    private async Task EvaluateAvailabilityAsync()
    {
        lastLoad = load.Sample(jobs.Values.Select(j => j.Process).OfType<System.Diagnostics.Process>());
        var sample = new AvailabilitySample(DateTime.Now, config.Available, SystemSignals.UserIdle(), SystemSignals.OnBattery(),
            lastLoad, ActiveJobs, config.MaxJobs, socket?.State == WebSocketState.Open);
        var next = engine.Evaluate(sample);
        if (updateDraining)
            next = new AvailabilityDecision(WorkerState.Unavailable, UpdateReason, ReclaimAction.None);
        var previous = decision;
        decision = next;

        if (next.Action == ReclaimAction.Release)
        {
            log($"PC repris ({next.Reason}) : libération immédiate de {jobs.Count} tâche(s)");
            foreach (var job in jobs.Values)
                job.Stop($"released: {next.Reason}");
        }
        else if (next.Action == ReclaimAction.Drain)
        {
            // Playback finishes here; background encodes stop now and resume elsewhere/later from their last chunk.
            var background = jobs.Values.Where(j => j.Kind == JobKind.Background).ToList();
            log($"PC repris ({next.Reason}) : drainage, les lectures en cours se terminent, {background.Count} encodage(s) suspendu(s)");
            foreach (var job in background)
                job.Stop($"released: drainage ({next.Reason})");
        }

        // Reasons carry live figures ("CPU ... (77%)"): only a change of state or of motive is an event.
        if (next.State != previous.State || Motive(next.Reason) != Motive(previous.Reason))
        {
            log($"état : {next.State}{(next.Reason is null ? "" : $" ({next.Reason})")}");
            StateChanged?.Invoke(next);
            if (next.State != WorkerState.Offline)
                await SendAsync(Heartbeat());
        }
    }

    private static string? Motive(string? reason) => reason is null ? null : reason.Split(" (")[0];

    private WorkerHeartbeat Heartbeat() => new(CurrentState(), ActiveJobs, config.MaxJobs, lastLoad, StateReason);

    /// <summary>Jobs still working; a job being stopped (preempted, released) no longer holds its slot.</summary>
    private int ActiveJobs => jobs.Values.Count(j => j.StopReason is null);

    private int ActivePlaybacks => jobs.Values.Count(j => j.StopReason is null && j.Kind == JobKind.Playback);

    /// <summary>Every 5 s while connected; the coordinator stops offering jobs to a worker whose heartbeats are too old.</summary>
    private async Task HeartbeatLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await SendAsync(Heartbeat());
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
    }

    /// <summary>
    /// TLS validation for both the WebSocket and the HTTP client. With a pinned fingerprint, chain and name errors are
    /// deliberately ignored (the coordinator typically uses a self-signed certificate): only the exact certificate is
    /// accepted. Without one, normal Windows validation applies (public certificate behind a reverse proxy).
    /// Runs on a thread-pool thread during each handshake.
    /// </summary>
    private bool ValidatePinned(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null)
            return false;
        if (config.CertificateFingerprint is null)
            return errors == SslPolicyErrors.None;
        string fingerprint = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
        if (!string.Equals(fingerprint, config.CertificateFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            log("coordinator certificate does not match the paired fingerprint; refusing connection");
            return false;
        }
        // ffmpeg verifies the same certificate for media inputs through -ca_file.
        if (!File.Exists(WorkerConfig.CaFilePath))
            File.WriteAllText(WorkerConfig.CaFilePath, new X509Certificate2(certificate).ExportCertificatePem());
        return true;
    }

    /// <summary>
    /// Fire-and-forget delivery: while disconnected, messages are dropped (the coordinator treats this worker's jobs as
    /// lost anyway). Safe to call from any thread.
    /// </summary>
    private async Task SendAsync(Message message)
    {
        var s = socket;
        if (s is null || s.State != WebSocketState.Open)
            return;
        byte[] bytes = Encoding.UTF8.GetBytes(MessageJson.Serialize(message));
        await sendLock.WaitAsync();
        try
        {
            await s.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            log($"send failed: {ex.Message}");
        }
        finally
        {
            sendLock.Release();
        }
    }

    /// <summary>
    /// Reassembles one text message from its frames; null when the coordinator closes the connection. A message this
    /// version cannot read (new type from a newer coordinator) is logged and skipped rather than dropping the connection.
    /// </summary>
    private async Task<Message?> ReceiveAsync(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            ms.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
                continue;
            string json = Encoding.UTF8.GetString(ms.ToArray());
            ms.SetLength(0);
            try
            {
                if (MessageJson.Deserialize(json) is { } message)
                    return message;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
            {
                log($"message du coordinateur ignoré : {ex.Message}");
            }
        }
    }
}
