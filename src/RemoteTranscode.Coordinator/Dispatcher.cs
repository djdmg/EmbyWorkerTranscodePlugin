using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RemoteTranscode.Common;

namespace RemoteTranscode.Coordinator;

/// <summary>
/// Lifecycle of a <see cref="Job"/>: Offered (slot reserved, waiting for the worker's answer) → Running → Stopping
/// (owner asked to stop, waiting for the worker's JobEnded) → Finished or Failed. Offered, Running and Stopping are "active".
/// </summary>
public enum JobStatus { Offered, Running, Stopping, Finished, Failed }

/// <summary>
/// Who receives a job's output: the ffmpeg shim of a playback session, or the background encode queue.
/// </summary>
public interface IJobOwner
{
    /// <summary>Returns false when the output is no longer wanted (the worker then stops the job).</summary>
    Task<bool> OnSegmentAsync(Job job, int index, double start, double end, ReadOnlyMemory<byte> data);
    Task OnProgressAsync(Job job, JobProgress progress);
    /// <summary>Normal end, reported by the worker.</summary>
    Task OnEndedAsync(Job job, JobEnded ended);
    /// <summary>The job is gone without a normal end: worker lost, released for its owner, or preempted.</summary>
    Task OnLostAsync(Job job, string reason);
}

/// <summary>
/// One run of a plan on one worker. A playback or encode that resumes elsewhere gets a new Job (new id, new token);
/// the old one stays in the table, inactive, for the status page.
/// </summary>
public sealed class Job
{
    private int released;

    public required string Id { get; init; }
    /// <summary>Per-job secret sent only to the chosen worker; required on /jobs/{id}/files and /jobs/{id}/segments.</summary>
    public required string Token { get; init; }
    public required JobKind Kind { get; init; }
    public required JobPlan Plan { get; init; }
    public required IJobOwner Owner { get; init; }
    public required WorkerConnection Worker { get; init; }
    // Changed from several tasks (worker receive loop, shim loop, heartbeat loop, encode scheduler): every transition
    // is a compare-and-swap, so exactly one path ends a job (and notifies its owner) and an ended job never comes back.
    private int status = (int)JobStatus.Offered;
    public JobStatus Status => (JobStatus)Volatile.Read(ref status);

    /// <summary>Atomically moves from <paramref name="from"/> to <paramref name="to"/>; false if the job was in another state.</summary>
    public bool TryTransition(JobStatus from, JobStatus to) =>
        Interlocked.CompareExchange(ref status, (int)to, (int)from) == (int)from;

    /// <summary>Atomically ends an active job (Finished or Failed); false if it had already ended.</summary>
    public bool TryEnd(JobStatus to)
    {
        while (true)
        {
            var current = Status;
            if (current is not (JobStatus.Offered or JobStatus.Running or JobStatus.Stopping))
                return false;
            if (TryTransition(current, to))
                return true;
        }
    }

    /// <summary>Atomically marks an offered or running job as stopping; false if it is already stopping or ended.</summary>
    public bool TryStop()
    {
        while (true)
        {
            var current = Status;
            if (current is not (JobStatus.Offered or JobStatus.Running))
                return false;
            if (TryTransition(current, JobStatus.Stopping))
                return true;
        }
    }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    /// <summary>Segment indexes already handed to the owner: a worker retrying an upload is acknowledged without a second delivery.</summary>
    public ConcurrentDictionary<int, bool> DeliveredSegments { get; } = new();
    public JobProgress? LastProgress { get; set; }
    public string? Error { get; set; }
    public string? Description { get; init; }
    /// <summary>
    /// Completed by the worker's receive loop; asynchronous continuations keep that loop from running the dispatch
    /// code inline (it would then process the job's next messages only after the offer completes).
    /// </summary>
    public TaskCompletionSource<JobAnswer> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Playback only: detects when it can no longer keep up with real time.</summary>
    public PlaybackSpeedMonitor? SpeedMonitor { get; init; }

    /// <summary>Releases the worker slot exactly once, whatever path ends the job.</summary>
    public void ReleaseSlot()
    {
        if (Interlocked.Exchange(ref released, 1) == 0)
            Worker.Release(Kind);
    }

    public bool IsActive => Status is JobStatus.Offered or JobStatus.Running or JobStatus.Stopping;
}

/// <summary>Tracks workers and jobs, chooses workers and routes job traffic to each job's owner.</summary>
public sealed class Dispatcher(CoordinatorOptions options, ILogger<Dispatcher> logger)
{
    // Keyed by paired identity: at most one live connection per worker (a reconnect replaces the previous one).
    private readonly ConcurrentDictionary<string, WorkerConnection> workers = new();
    // Active jobs plus recently ended ones (kept 30 minutes for the status page, see CheckHeartbeatsAsync).
    private readonly ConcurrentDictionary<string, Job> jobs = new();
    // Serializes only "pick a worker + reserve its slot"; the offer round-trip happens outside it so a slow worker
    // does not hold up other dispatches. The reservation itself is what prevents overbooking.
    private readonly SemaphoreSlim dispatchLock = new(1, 1);

    // Playback session (Emby output folder) -> worker that ran it last, so seeks stay on the same machine.
    private readonly ConcurrentDictionary<string, (string WorkerId, DateTimeOffset At)> sessionAffinity = new();
    // Workers whose ffmpeg failed on a playback session (session, worker id → when): the same command would fail
    // again there, so that session goes elsewhere or to the server for a while instead of retrying in a loop.
    private readonly ConcurrentDictionary<(string Session, string WorkerId), DateTimeOffset> sessionFailures = new();
    private static readonly TimeSpan SessionFailureMemory = TimeSpan.FromMinutes(10);

    private List<string> FailedWorkers(string session) =>
        sessionFailures.Where(f => f.Key.Session == session && DateTimeOffset.UtcNow - f.Value < SessionFailureMemory).Select(f => f.Key.WorkerId).ToList();
    private readonly Regex? offloadFilter = string.IsNullOrWhiteSpace(options.OffloadInputPattern) ? null : new Regex(options.OffloadInputPattern);

    public IEnumerable<WorkerConnection> Workers => workers.Values;
    public IEnumerable<Job> Jobs => jobs.Values;

    /// <summary>
    /// Raised when capacity may have been freed (job ended, worker connected), so queued work can start. Invoked
    /// synchronously on the caller's thread (often a worker receive loop): handlers must be quick and must not throw.
    /// </summary>
    public event Action? CapacityChanged;

    public Job? FindJob(string id) => jobs.TryGetValue(id, out var job) ? job : null;

    /// <summary>Registers an authenticated worker connection, dropping any older connection of the same identity.</summary>
    public void AddWorker(WorkerConnection worker)
    {
        // Fire-and-forget: the old connection's jobs are lost (owners recover) without delaying the new handshake.
        if (workers.TryRemove(worker.Identity.Id, out var previous))
            _ = DropWorkerAsync(previous, "replaced by a new connection");
        workers[worker.Identity.Id] = worker;
        logger.LogInformation("Worker {Name} ({Id}) connected from {Address}", worker.Identity.Name, worker.Identity.Id, worker.RemoteAddress);
    }

    /// <summary>Called when a worker's socket closes or its heartbeat expires: every job it held is lost.</summary>
    public async Task DropWorkerAsync(WorkerConnection worker, string reason)
    {
        // Removes this exact connection only: a newer connection of the same worker stays registered.
        // May run more than once for the same connection (heartbeat timeout, then socket close); LoseJobAsync skips
        // jobs that are no longer active.
        workers.TryRemove(new KeyValuePair<string, WorkerConnection>(worker.Identity.Id, worker));
        logger.LogWarning("Worker {Name} dropped: {Reason}", worker.Identity.Name, reason);
        foreach (var job in jobs.Values.Where(j => j.Worker == worker && j.IsActive))
            await LoseJobAsync(job, $"worker {worker.Identity.Name} lost: {reason}", notifyWorker: false);
        // Not awaited: a half-open peer must never hold up the heartbeat loop or the revoke request. CloseAsync is
        // bounded, never throws, and aborts the socket if the peer does not complete the close.
        if (worker.IsOpen)
            _ = worker.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation, reason);
    }

    // ---- Playback -------------------------------------------------------------------------------

    /// <summary>
    /// Plans Emby's HLS command and starts it on a worker. Any rejection makes the shim run Emby's ffmpeg locally, so
    /// a refusal here never breaks playback.
    /// </summary>
    public async Task<Message> SubmitPlaybackAsync(ShimConnection shim, ShimSubmit submit, CancellationToken token)
    {
        var plan = EmbyJobPlanner.Plan(submit.Args, new PlannerOptions
        {
            AllowedRoots = options.AllowedRoots,
            OutputRoots = options.OutputRoots,
            GetFileSize = ServerFiles.SizeIfAllowed(options),
            ListDirectory = ServerFiles.ListIfAllowed(options),
        });
        if (!plan.Remote)
            return new ShimRejected(plan.Reason);
        var input = plan.Files.First(f => f.Kind == JobFileKind.Input);
        if (offloadFilter != null && !offloadFilter.IsMatch(input.ServerPath))
            return new ShimRejected("input not selected for offloading");

        var (job, why) = await StartJobAsync(plan, JobKind.Playback, new ShimOwner(shim, this), input.ServerPath, token,
            avoid: FailedWorkers(plan.SessionKey));
        if (job is null)
            return new ShimRejected(why ?? "no available worker");
        sessionAffinity[plan.SessionKey] = (job.Worker.Identity.Id, DateTimeOffset.UtcNow);
        return new ShimAssigned(job.Id, job.Worker.Identity.Name);
    }

    /// <summary>Could a worker take this playback right now? Used to move a playback back from the server.</summary>
    public ShimCapacity PlaybackCapacity(ShimCapacityQuery query)
    {
        var plan = EmbyJobPlanner.Plan(query.Args, new PlannerOptions
        {
            AllowedRoots = options.AllowedRoots,
            OutputRoots = options.OutputRoots,
            GetFileSize = ServerFiles.SizeIfAllowed(options),
            ListDirectory = ServerFiles.ListIfAllowed(options),
        });
        if (!plan.Remote)
            return new ShimCapacity(false, plan.Reason);
        if (offloadFilter != null && !offloadFilter.IsMatch(plan.Files.First(f => f.Kind == JobFileKind.Input).ServerPath))
            return new ShimCapacity(false, "input not selected for offloading");
        return HasCapacityFor(plan.Requirements!, JobKind.Playback, FailedWorkers(plan.SessionKey))
            ? new ShimCapacity(true, null)
            : new ShimCapacity(false, "no available worker");
    }

    // ---- Any job ----------------------------------------------------------------------------------

    /// <summary>
    /// Picks a worker, reserves its slot and offers the job, trying the next best worker on refusal. Playback may
    /// preempt a background encode when no slot is free; background work only uses free slots.
    /// </summary>
    /// <param name="onCreated">
    /// Called with each job before its offer leaves, so the owner can link it and recognise callbacks (JobEnded, lost)
    /// that the worker's receive loop may process before this method returns. Must be quick and must not throw.
    /// </param>
    /// <returns>
    /// The running job, or null with a reason when no worker took it or when it ended (lost, stopped) before it could
    /// be marked running; in that last case the owner has already been notified or asked to stop it.
    /// </returns>
    /// <param name="avoid">Worker ids not to use (e.g. those already encoding another slice of the same conversion).</param>
    public async Task<(Job? Job, string? Reason)> StartJobAsync(JobPlan plan, JobKind kind, IJobOwner owner, string description, CancellationToken token, Action<Job>? onCreated = null,
        IEnumerable<string>? avoid = null)
    {
        var tried = new HashSet<string>(avoid ?? []);
        while (true)
        {
            WorkerConnection? worker;
            string? why;
            await dispatchLock.WaitAsync(token);
            try
            {
                (worker, why) = PickWorker(plan, kind, tried);
            }
            finally
            {
                dispatchLock.Release();
            }
            if (worker is null)
                return (null, why);
            tried.Add(worker.Identity.Id);

            var job = new Job
            {
                Id = Guid.NewGuid().ToString("N")[..16],
                Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
                Kind = kind,
                Plan = plan,
                Owner = owner,
                Worker = worker,
                Description = description,
                SpeedMonitor = kind == JobKind.Playback ? new PlaybackSpeedMonitor(options.PlaybackPriority) : null,
            };
            // Registered before the offer leaves so the worker's answer (and anything after it) can find the job.
            jobs[job.Id] = job;
            onCreated?.Invoke(job);
            var offer = new JobOffer(job.Id, kind, plan.Template,
                plan.Files.Select(f => new JobFile(f.Id, f.Kind, f.Name, f.Size)).ToArray(),
                plan.SegmentSeconds, plan.StartIndex, job.Token, plan.Prefetch, plan.Slice, plan.ServerFileStart);
            // Set once the job's fate no longer belongs to this method (running, or accepted then ended elsewhere).
            bool handedOff = false;
            // Set when the job was lost or stopped by another path while the offer was pending.
            bool endedElsewhere = false;
            try
            {
                await worker.SendAsync(offer, token);
                var answer = await job.Answer.Task.WaitAsync(TimeSpan.FromSeconds(options.OfferTimeoutSeconds), token);
                if (answer.Accepted)
                {
                    handedOff = true;
                    // The worker starts right after answering: its JobEnded (or the worker's loss, or a stop by the
                    // owner) may already have been processed by the time this continuation runs. Only an offered job
                    // may become running; an ended one must not be resurrected, and the caller must not wait for it.
                    // Its slot is (or will be) released by the path that ended or stopped it.
                    if (!job.TryTransition(JobStatus.Offered, JobStatus.Running))
                    {
                        logger.LogInformation("Job {Job} ended before it could start on {Worker}: {Status} {Error}", job.Id, worker.Identity.Name, job.Status, job.Error);
                        return (null, $"{worker.Identity.Name} : tâche terminée avant son démarrage ({job.Error ?? job.Status.ToString()})");
                    }
                    logger.LogInformation("{Kind} job {Job} running on {Worker} (start segment {Start}, {Input})", kind, job.Id, worker.Identity.Name, plan.StartIndex, description);
                    // Encodes only leave when the worker cannot carry everything: its load is already too high.
                    if (kind == JobKind.Playback && PlaybackPriority.LacksHeadroom(worker.LastHeartbeat?.Load, options.PlaybackPriority))
                        await MoveBackgroundAwayAsync(worker, "charge du worker trop élevée pour la lecture");
                    return (job, null);
                }
                why = $"{worker.Identity.Name} a refusé : {answer.Reason}";
                logger.LogInformation("Worker {Worker} refused job {Job}: {Reason}", worker.Identity.Name, job.Id, answer.Reason);
            }
            catch (Exception ex) when (ex is TimeoutException or System.Net.WebSockets.WebSocketException)
            {
                why = $"{worker.Identity.Name} : pas de réponse ({ex.Message})";
                logger.LogWarning("Offer {Job} to {Worker} failed: {Error}", job.Id, worker.Identity.Name, ex.Message);
                _ = worker.SendAsync(new CancelJob(job.Id, "offer timed out"), CancellationToken.None).ContinueWith(_ => { });
            }
            catch (OperationCanceledException)
            {
                // The caller went away (shim disconnected, host stopping) with the offer pending: the worker may have
                // received it, so cancel it there too; the finally block frees the slot.
                _ = worker.SendAsync(new CancelJob(job.Id, "offer cancelled"), CancellationToken.None).ContinueWith(_ => { });
                throw;
            }
            finally
            {
                if (!handedOff)
                {
                    // Refused, unanswered or cancelled: free the reservation and forget the job (a late answer then
                    // finds nothing). If another path already ended or stopped it, the owner knows: do not try the
                    // next worker on its behalf.
                    endedElsewhere = !job.TryTransition(JobStatus.Offered, JobStatus.Failed);
                    job.TryEnd(JobStatus.Failed);
                    job.ReleaseSlot();
                    jobs.TryRemove(job.Id, out _);
                }
            }
            if (endedElsewhere)
                return (null, job.Error ?? why);
        }
    }

    /// <summary>
    /// A worker qualifies only if it reported itself available, its heartbeat is recent, it has a free slot and it can
    /// run this job (encoder, filters). The best scored one wins (<see cref="WorkerMatching.Score"/>); its slot is
    /// reserved before the offer leaves. When nobody qualifies, the reason is returned for the logs.
    /// </summary>
    private (WorkerConnection? Worker, string? Reason) PickWorker(JobPlan plan, JobKind kind, HashSet<string> excluded, bool reserve = true)
    {
        var now = DateTimeOffset.UtcNow;
        var requirements = plan.Requirements ?? new JobRequirements("copy", []);
        string? previous = kind == JobKind.Playback && sessionAffinity.TryGetValue(plan.SessionKey, out var a) ? a.WorkerId : null;
        var reasons = new List<string>();
        var candidates = new List<(WorkerConnection W, double Score)>();
        foreach (var w in workers.Values)
        {
            string name = w.Identity.Name;
            if (excluded.Contains(w.Identity.Id) || !w.IsOpen || w.Hello is null)
                continue;
            // "Busy" means all slots are used; for a playback, slots held by background encodes do not count.
            bool usable = w.State == WorkerState.Available || (kind == JobKind.Playback && w.State == WorkerState.Busy);
            if (!usable)
                reasons.Add($"{name}: {w.State}{(w.LastHeartbeat?.Reason is { } r ? $" ({r})" : "")}");
            else if (now - w.LastSeen >= TimeSpan.FromSeconds(options.HeartbeatTimeoutSeconds))
                reasons.Add($"{name}: signal de présence trop ancien");
            else if (!w.HasRoomFor(kind))
                reasons.Add($"{name}: {(w.BackgroundPaused && kind == JobKind.Background ? "réservé aux lectures en cours" : "complet")}");
            else if (WorkerMatching.Incompatibility(requirements, w.Hello.Capabilities) is { } incompatible)
                reasons.Add($"{name}: {incompatible}");
            else
                candidates.Add((w, WorkerMatching.Score(requirements, w.Hello.Capabilities, w.LastHeartbeat?.Load, w.Reserved, w.MaxJobs, w.Identity.Id == previous)));
        }
        // Without reservation (fetch-ahead): only which worker would be picked now.
        if (!reserve)
            return (candidates.OrderByDescending(c => c.Score).Select(c => c.W).FirstOrDefault(), null);
        foreach (var (w, score) in candidates.OrderByDescending(c => c.Score))
        {
            if (w.TryReserve(kind))
            {
                logger.LogDebug("Picked {Worker} (score {Score:0.0}) among {Count}", w.Identity.Name, score, candidates.Count);
                return (w, null);
            }
        }
        return (null, reasons.Count == 0 ? "no worker connected" : "no available worker: " + string.Join("; ", reasons));
    }

    /// <summary>True when some worker could take a job with these requirements right now (nothing is reserved).</summary>
    public bool HasCapacityFor(JobRequirements requirements, JobKind kind = JobKind.Background, IReadOnlyCollection<string>? excluded = null)
    {
        var now = DateTimeOffset.UtcNow;
        return workers.Values.Any(w => w.IsOpen && w.Hello != null && excluded?.Contains(w.Identity.Id) != true
            && (w.State == WorkerState.Available || (kind == JobKind.Playback && w.State == WorkerState.Busy))
            && now - w.LastSeen < TimeSpan.FromSeconds(options.HeartbeatTimeoutSeconds)
            && w.HasRoomFor(kind)
            && WorkerMatching.Incompatibility(requirements, w.Hello.Capabilities) is null);
    }

    /// <summary>
    /// A connected worker able to run these requirements, whatever its state now (busy with other applications,
    /// outside its hours, draining): a conversion can wait for it, the server covering meanwhile slice by slice.
    /// </summary>
    public bool HasWorkerFor(JobRequirements requirements) =>
        ConnectedCapabilities().Any(c => WorkerMatching.Incompatibility(requirements, c) is null);

    /// <summary>Capabilities of the workers connected now (whatever their state), as <see cref="HasWorkerFor"/> counts them.</summary>
    public IReadOnlyList<WorkerCapabilities> ConnectedCapabilities()
    {
        var now = DateTimeOffset.UtcNow;
        return workers.Values.Where(w => w.IsOpen && w.Hello != null
            && now - w.LastSeen < TimeSpan.FromSeconds(options.HeartbeatTimeoutSeconds)).Select(w => w.Hello!.Capabilities).ToList();
    }

    /// <summary>
    /// Playback has priority, but an encode leaves a worker only when that worker cannot carry everything: one
    /// background job is stopped (it resumes elsewhere or on the server from its last chunk) and no background work
    /// returns to this worker until its playbacks are over.
    /// </summary>
    private async Task MoveBackgroundAwayAsync(WorkerConnection worker, string reason)
    {
        var victim = jobs.Values.FirstOrDefault(j => j.Worker == worker && j.Kind == JobKind.Background && j.Status == JobStatus.Running);
        if (victim is null)
            return;
        worker.BackgroundPaused = true;
        logger.LogInformation("Moving background job {Job} away from {Worker}: {Reason}", victim.Id, worker.Identity.Name, reason);
        await LoseJobAsync(victim, $"preempted: {reason}", notifyWorker: true);
    }

    /// <summary>
    /// Handles one message from a worker. Called sequentially by that worker's receive loop, so a slow owner callback
    /// delays the worker's later messages (heartbeats included). Job messages are honoured only from the job's worker.
    /// </summary>
    /// <summary>Where worker names are stored; set by the host (null in unit setups: names are then left as paired).</summary>
    public WorkerStore? Store { get; set; }

    /// <summary>Server-side probing, for fetch-ahead ranges; set by the host (null: no fetch-ahead).</summary>
    public ServerMedia? Media { get; set; }

    // Encoding speed the server reached when it started a playback of this source (path -> speed, when), and the
    // probed height of sources: together they tell whether the server can start a playback itself.
    private readonly ConcurrentDictionary<string, (double Speed, DateTimeOffset At)> serverSpeeds = new();
    private readonly ConcurrentDictionary<string, int> sourceHeights = new();
    // Probes of playback sources, shared by the start advice and the fetch-ahead (one ffprobe per file and version).
    private readonly ConcurrentDictionary<string, Task<SourceInfo>> probes = new();
    /// <summary>How long a measured server speed decides a start: the server's load changes, so not forever.</summary>
    private static readonly TimeSpan ServerSpeedMemory = TimeSpan.FromDays(7);
    /// <summary>The advice must reach the shim well within its 2 s wait: a probe still running then is not waited for.</summary>
    private static readonly TimeSpan AdviceProbeWait = TimeSpan.FromMilliseconds(1200);

    private Task<SourceInfo> ProbeCachedAsync(string path)
    {
        var file = new FileInfo(path);
        string key = $"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        var probe = probes.GetOrAdd(key, _ => Media!.ProbeAsync(path, CancellationToken.None));
        // A failed probe is not kept: the next playback tries again.
        _ = probe.ContinueWith(_ => probes.TryRemove(key, out var _), TaskContinuationOptions.OnlyOnFaulted);
        if (probes.Count > 500)
            probes.Clear();
        return probe;
    }

    /// <summary>Remembers how fast the server encoded this source at the start of a playback.</summary>
    public void RecordServerSpeed(ShimServerSpeed report)
    {
        if (EmbyJobPlanner.InputPath(report.Args) is { } input)
        {
            serverSpeeds[input] = (report.Speed, DateTimeOffset.UtcNow);
            logger.LogInformation("Server speed at playback start: {Speed:0.00}x for {Input}", report.Speed, Path.GetFileName(input));
        }
    }

    /// <summary>
    /// Fast start or straight to a worker. Straight to a worker when the server is known to be too slow for this
    /// source (measured at a previous start) or expected to be (more than 1080 lines), and a worker can take it now.
    /// </summary>
    public async Task<ShimStartAdvice> StartAdviceAsync(ShimStartQuery query)
    {
        if (EmbyJobPlanner.InputPath(query.Args) is not { } input)
            return new ShimStartAdvice(true, "source inconnue");
        if (serverSpeeds.TryGetValue(input, out var known) && DateTimeOffset.UtcNow - known.At < ServerSpeedMemory && known.Speed < query.MinSpeed)
            return Direct($"serveur trop lent pour ce film ({known.Speed:0.00}x)");
        if (Media != null && ServerFiles.IsAllowed(input, options))
        {
            if (!sourceHeights.TryGetValue(input, out int height))
            {
                // A slow probe (busy server, sleeping disk) must not delay the answer past the shim's wait: the
                // playback then starts on the server, and the probe, which goes on, serves the next decisions.
                var probe = ProbeCachedAsync(input);
                _ = probe.ContinueWith(t => sourceHeights[input] = t.Result.Height, TaskContinuationOptions.OnlyOnRanToCompletion);
                if (await Task.WhenAny(probe, Task.Delay(AdviceProbeWait)) == probe && probe.IsCompletedSuccessfully)
                    height = probe.Result.Height;
            }
            if (height > 1088)
                return Direct($"source en {height} lignes");
        }
        return new ShimStartAdvice(true, "démarrage sur le serveur");

        // Only when a worker could actually take it: otherwise the server is the only option anyway.
        ShimStartAdvice Direct(string reason)
        {
            var plan = EmbyJobPlanner.Plan(query.Args, new PlannerOptions
            {
                AllowedRoots = options.AllowedRoots,
                OutputRoots = options.OutputRoots,
                GetFileSize = ServerFiles.SizeIfAllowed(options),
                ListDirectory = ServerFiles.ListIfAllowed(options),
            });
            return plan.Remote && HasCapacityFor(plan.Requirements!, JobKind.Playback, FailedWorkers(plan.SessionKey))
                ? new ShimStartAdvice(false, reason)
                : new ShimStartAdvice(true, reason + ", mais aucun worker libre");
        }
    }

    // Short-lived read grants for fetching a source ahead (PrefetchHint): grant id -> file and token.
    private readonly ConcurrentDictionary<string, (string Path, string Token, DateTimeOffset Expires)> grants = new();
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromMinutes(3);

    public bool TryGetGrant(string id, out string path, out string token)
    {
        if (grants.TryGetValue(id, out var g) && g.Expires > DateTimeOffset.UtcNow)
        {
            (path, token) = (g.Path, g.Token);
            return true;
        }
        (path, token) = ("", "");
        return false;
    }

    /// <summary>
    /// A playback starts on the server and will move to a worker (fast start): the worker that would be picked now
    /// fetches ahead the file's head and tail (container header and index, which ffmpeg reads on every open) and the
    /// data from the start of the playback to past the expected hand-over, so that its run starts without waiting for
    /// the network wherever it takes over.
    /// </summary>
    public async Task PreparePlaybackAsync(ShimPrepare prepare)
    {
        var plan = EmbyJobPlanner.Plan(prepare.Args, new PlannerOptions
        {
            AllowedRoots = options.AllowedRoots,
            OutputRoots = options.OutputRoots,
            GetFileSize = ServerFiles.SizeIfAllowed(options),
            ListDirectory = ServerFiles.ListIfAllowed(options),
        });
        if (!plan.Remote || Media is null)
            return;
        var input = plan.Files.First(f => f.Kind == JobFileKind.Input);
        if (offloadFilter != null && !offloadFilter.IsMatch(input.ServerPath))
            return;
        WorkerConnection? worker;
        await dispatchLock.WaitAsync();
        try
        {
            (worker, _) = PickWorker(plan, JobKind.Playback, [.. FailedWorkers(plan.SessionKey)], reserve: false);
        }
        finally
        {
            dispatchLock.Release();
        }
        if (worker is null)
            return;

        const long Edge = 8 << 20;
        long size = input.Size;
        var ranges = new List<ByteRange> { new(0, Math.Min(Edge, size)) };
        if (size > Edge)
            ranges.Add(new ByteRange(size - Edge, Edge));
        var info = await ProbeCachedAsync(input.ServerPath);
        // From where the playback starts (the server may turn out too slow from its very first segment, and the worker
        // then takes over right there) to 40 s past the expected hand-over: one range at the file's average rate,
        // from the keyframe a run starting there decodes from. Capped. From the start of the file, no keyframe lookup.
        double from = EmbyJobPlanner.ParseDuration(EmbyJobPlanner.ValueOf(prepare.Args, "-ss")) ?? 0;
        var boundary = from > 0 ? (await Media.BoundariesAsync(input.ServerPath, [from], CancellationToken.None))[0] : null;
        if (info.DurationSeconds > 0)
        {
            long position = from > 0 ? boundary?.KeyframePosition ?? (long)(size / info.DurationSeconds * from) : 0;
            long start = Math.Max(0, position - (2 << 20));
            double seconds = Math.Max(0, prepare.AtSeconds - from) + 40;
            long length = (long)Math.Min(512L << 20, size / info.DurationSeconds * seconds);
            ranges.Add(new ByteRange(start, Math.Min(length, size - start)));
        }
        // Too late: the playback already moved to a worker, whose own reads would compete with these.
        if (jobs.Values.Any(j => j.IsActive && j.Kind == JobKind.Playback && j.Plan.SessionKey == plan.SessionKey))
        {
            logger.LogInformation("Playback {Input}: no fetch-ahead, already on a worker", Path.GetFileName(input.ServerPath));
            return;
        }
        string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        grants[id] = (input.ServerPath, token, DateTimeOffset.UtcNow + GrantLifetime);
        await worker.SendAsync(new PrefetchHint(id, token, new JobFile(0, JobFileKind.Input, input.Name, input.Size), [.. ranges]), CancellationToken.None);
        logger.LogInformation("Playback {Input}: {Worker} fetches ahead around {At:0}s", Path.GetFileName(input.ServerPath), worker.Identity.Name, prepare.AtSeconds);
    }

    /// <summary>A name announced by a worker, made safe to show and store: trimmed, no control characters, 64 at most.</summary>
    private static string? CleanName(string? name)
    {
        string clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length == 0 ? null : clean.Length > 64 ? clean[..64] : clean;
    }

    public async Task OnWorkerMessageAsync(WorkerConnection worker, Message message)
    {
        worker.LastSeen = DateTimeOffset.UtcNow;
        switch (message)
        {
            case WorkerHello hello:
                worker.Hello = hello;
                // The name shown everywhere follows the one set in the worker's own settings, not the pairing label.
                if (Store is { } store && CleanName(hello.Name) is { } name && name != worker.Identity.Name
                    && store.Rename(worker.Identity.Id, name) is { } renamed)
                {
                    logger.LogInformation("Worker {Id} renamed {Old} -> {New}", worker.Identity.Id, worker.Identity.Name, name);
                    worker.Identity = renamed;
                }
                logger.LogInformation("Worker {Name}: ffmpeg {Version}, encoders [{Encoders}], max {Max} jobs, {State}",
                    worker.Identity.Name, hello.Capabilities.FfmpegVersion, string.Join(",", hello.Capabilities.VideoEncoders), hello.MaxJobs, hello.State);
                CapacityChanged?.Invoke();
                break;
            case WorkerHeartbeat hb:
                bool becameAvailable = hb.State == WorkerState.Available && worker.State != WorkerState.Available;
                worker.LastHeartbeat = hb;
                if (becameAvailable)
                    CapacityChanged?.Invoke();
                break;
            case JobAnswer answer when FindJob(answer.JobId) is { } job && job.Worker == worker:
                job.Answer.TrySetResult(answer);
                break;
            case JobProgress progress when FindJob(progress.JobId) is { } job && job.Worker == worker:
                job.LastProgress = progress;
                // A playback that falls below real time while encodes share its worker: move one encode away.
                if (job.SpeedMonitor?.Report(DateTimeOffset.UtcNow - job.CreatedAt, progress.Speed) == true)
                {
                    await MoveBackgroundAwayAsync(worker, $"lecture trop lente ({progress.Speed:0.00}x)");
                    job.SpeedMonitor.Reset();
                }
                await Safe(() => job.Owner.OnProgressAsync(job, progress));
                break;
            case JobEnded ended when FindJob(ended.JobId) is { } job && job.Worker == worker:
                await OnJobEndedAsync(job, ended);
                break;
        }
    }

    private async Task OnJobEndedAsync(Job job, JobEnded ended)
    {
        // The worker's process is really gone now: free the slot even for a job already stopped or lost.
        job.ReleaseSlot();
        CapacityChanged?.Invoke();
        if (!job.IsActive)
            return;
        // A worker that releases a job because its owner needs the PC must not look like a normal end:
        // playback resumes elsewhere from the next segment, a background encode goes back to the queue.
        if (ended.Error?.StartsWith("released:", StringComparison.Ordinal) == true)
        {
            await LoseJobAsync(job, ended.Error, notifyWorker: false);
            return;
        }
        // Atomic: a concurrent loss (heartbeat timeout, preemption) cannot also notify the owner.
        if (!job.TryEnd(ended.ExitCode == 0 ? JobStatus.Finished : JobStatus.Failed))
            return;
        job.Error = ended.Error;
        logger.LogInformation("Job {Job} ended with {Code} {Error}", job.Id, ended.ExitCode, ended.Error);
        if (job.Kind == JobKind.Playback && ended.ExitCode != 0)
        {
            sessionFailures[(job.Plan.SessionKey, job.Worker.Identity.Id)] = DateTimeOffset.UtcNow;
            logger.LogWarning("Playback session {Session} avoids {Worker} for {Minutes} min after an ffmpeg failure", job.Plan.SessionKey, job.Worker.Identity.Name, SessionFailureMemory.TotalMinutes);
        }
        await Safe(() => job.Owner.OnEndedAsync(job, ended));
    }

    /// <summary>Ends a job abnormally (worker lost, released, preempted) and tells its owner to recover.</summary>
    public async Task LoseJobAsync(Job job, string reason, bool notifyWorker)
    {
        // Atomic: only one of the concurrent end paths (JobEnded, worker drop, preemption) notifies the owner.
        if (!job.TryEnd(JobStatus.Failed))
            return;
        job.Error = reason;
        // Released before the worker confirms: for a preemption the worker may run a few more seconds beyond MaxJobs.
        job.ReleaseSlot();
        // A job lost while its offer is pending: StartJobAsync stops waiting for an answer that will not come.
        job.Answer.TrySetResult(new JobAnswer(job.Id, false, reason));
        logger.LogWarning("Job {Job} lost: {Reason}", job.Id, reason);
        if (notifyWorker && job.Worker.IsOpen)
            await TrySend(job.Worker, new CancelJob(job.Id, reason));
        await Safe(() => job.Owner.OnLostAsync(job, reason));
        CapacityChanged?.Invoke();
    }

    /// <summary>The owner asked to stop (Emby sent "q", shim gone, encode cancelled).</summary>
    public async Task StopJobAsync(Job job, string reason)
    {
        if (!job.TryStop())
            return;
        // With a live worker the slot is freed by its JobEnded (OnJobEndedAsync); a worker that ignores the cancel
        // keeps the slot until it disconnects.
        if (job.Worker.IsOpen)
            await TrySend(job.Worker, new CancelJob(job.Id, reason));
        else
            job.ReleaseSlot();
    }

    /// <summary>Emby's ffmpeg process is gone: stop its playback jobs; nobody is left to receive their segments.</summary>
    public async Task OnShimClosedAsync(ShimConnection shim)
    {
        foreach (var job in jobs.Values.Where(j => j.Owner is ShimOwner o && o.Shim == shim && j.IsActive))
        {
            await StopJobAsync(job, "shim disconnected");
            // Ended for the owner (gone); a JobEnded that already finished it is not overwritten.
            job.TryEnd(JobStatus.Failed);
        }
    }

    /// <summary>
    /// Hands an uploaded segment to the job's owner; duplicates (upload retries) are acknowledged only once. True:
    /// delivered. False: the job is no longer wanted. Null: the server could not take it now (disk, file lock): the
    /// worker must retry, never take it for a stop.
    /// </summary>
    public async Task<bool?> DeliverSegmentAsync(Job job, int index, double start, double end, ReadOnlyMemory<byte> data)
    {
        // Claimed before delivery so concurrent uploads of the same index deliver once; un-claimed on failure so a
        // retry can deliver it. Different indexes may be delivered concurrently and out of order.
        if (!job.DeliveredSegments.TryAdd(index, true))
            return true;
        bool? result;
        try
        {
            result = await job.Owner.OnSegmentAsync(job, index, start, end, data);
            if (result == true)
                return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Cannot deliver segment {Index} of {Job}: {Error}", index, job.Id, ex.Message);
            result = null;
        }
        job.DeliveredSegments.TryRemove(index, out _);
        return result;
    }

    /// <summary>
    /// Periodic housekeeping (every 2 s from the host): drops silent workers, expires old session affinity and prunes
    /// ended jobs. Awaits each drop in turn, so one slow drop delays the others.
    /// </summary>
    public async Task CheckHeartbeatsAsync()
    {
        var limit = TimeSpan.FromSeconds(options.HeartbeatTimeoutSeconds);
        foreach (var w in workers.Values)
            if (DateTimeOffset.UtcNow - w.LastSeen > limit)
                await DropWorkerAsync(w, "heartbeat timeout");
        foreach (var (key, value) in sessionAffinity)
            if (DateTimeOffset.UtcNow - value.At > TimeSpan.FromHours(6))
                sessionAffinity.TryRemove(key, out _);
        foreach (var (key, at) in sessionFailures)
            if (DateTimeOffset.UtcNow - at > SessionFailureMemory)
                sessionFailures.TryRemove(key, out _);
        foreach (var (key, grant) in grants)
            if (grant.Expires <= DateTimeOffset.UtcNow)
                grants.TryRemove(key, out _);
        // Finished jobs are kept briefly for the status page.
        foreach (var job in jobs.Values.Where(j => !j.IsActive && DateTimeOffset.UtcNow - j.CreatedAt > TimeSpan.FromMinutes(30)))
            jobs.TryRemove(job.Id, out _);
    }

    /// <summary>Best-effort send: a peer that went away is detected by its own receive loop, not here.</summary>
    internal async Task TrySend(MessageSocket socket, Message message)
    {
        try
        {
            await socket.SendAsync(message);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Send failed: {Error}", ex.Message);
        }
    }

    // Owner callbacks run on the worker receive loop: an exception there must not tear down the worker's connection.
    private async Task Safe(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job owner callback failed");
        }
    }
}

/// <summary>Playback: everything goes to the ffmpeg shim that Emby started.</summary>
public sealed class ShimOwner(ShimConnection shim, Dispatcher dispatcher) : IJobOwner
{
    public ShimConnection Shim { get; } = shim;

    public async Task<bool> OnSegmentAsync(Job job, int index, double start, double end, ReadOnlyMemory<byte> data)
    {
        if (!Shim.IsOpen)
            return false;
        // The shim drops segments of a job id it does not know yet (before ShimAssigned) or any more.
        await Shim.SendWithPayloadAsync(new SegmentReady(job.Id, index, start, end, data.Length), data);
        return true;
    }

    public Task OnProgressAsync(Job job, JobProgress progress) => Shim.IsOpen ? dispatcher.TrySend(Shim, progress) : Task.CompletedTask;

    public Task OnEndedAsync(Job job, JobEnded ended) => Shim.IsOpen ? dispatcher.TrySend(Shim, ended) : Task.CompletedTask;

    public Task OnLostAsync(Job job, string reason) => Shim.IsOpen ? dispatcher.TrySend(Shim, new WorkerLost(job.Id, reason)) : Task.CompletedTask;
}
