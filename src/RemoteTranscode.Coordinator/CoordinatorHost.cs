using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using RemoteTranscode.Common;

namespace RemoteTranscode.Coordinator;

/// <summary>
/// The coordinator service: worker endpoint (TLS or behind a reverse proxy), loopback endpoint for the ffmpeg shim and
/// the administration API. Runs from the command line or inside Emby, hosted by the plugin.
/// </summary>
public static class CoordinatorHost
{
    /// <summary>
    /// Builds the web application (not started). Both listeners share one pipeline: every endpoint first checks the
    /// local port it was reached on, so worker-side routes never answer on the loopback port and vice versa.
    /// </summary>
    public static WebApplication Build(CoordinatorOptions options, Action<ILoggingBuilder> configureLogging)
    {
        Directory.CreateDirectory(options.DataDir);
        var store = new WorkerStore(options);
        var certificate = CoordinatorCertificate.LoadOrCreate(options);
        string shimSecret = LoadOrCreateShimSecret(options);
        string adminSecret = EncodeCli.LoadOrCreateAdminSecret(options);
        string pluginSecret = LoadOrCreatePluginSecret(options);

        // Empty builder: no configuration files, environment variables or console hooks, so it behaves the same on its own
        // and inside Emby's process (which has its own host, URLs and shutdown handling).
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { ContentRootPath = options.DataDir });
        builder.WebHost.UseKestrelCore();
        builder.Services.AddRoutingCore();
        builder.Services.AddLogging(logging =>
        {
            configureLogging(logging);
            logging.AddFilter("Microsoft", LogLevel.Warning);
        });
        builder.Services.AddSingleton<IHostLifetime, EmbeddedLifetime>();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<Dispatcher>();
        builder.Services.AddSingleton<ServerMedia>();
        builder.Services.AddSingleton<EncodeQueue>();
        builder.WebHost.ConfigureKestrel(k =>
        {
            var workerUri = new Uri(options.WorkerUrl);
            k.Listen(System.Net.IPAddress.Parse(workerUri.Host == "localhost" ? "127.0.0.1" : workerUri.Host), workerUri.Port, l =>
            {
                l.Protocols = HttpProtocols.Http1;
                if (options.ServesTls)
                    l.UseHttps(certificate);
            });
            var shimUri = new Uri(options.ShimUrl);
            // Shim and admin traffic: loopback only, whatever host ShimUrl names.
            k.Listen(System.Net.IPAddress.Loopback, shimUri.Port, l => l.Protocols = HttpProtocols.Http1);
            // Upper bound for one uploaded segment/chunk (buffered in memory by the segment endpoint).
            k.Limits.MaxRequestBodySize = 256L * 1024 * 1024;
        });

        var app = builder.Build();
        var dispatcher = app.Services.GetRequiredService<Dispatcher>();
        dispatcher.Store = store;
        dispatcher.Media = app.Services.GetRequiredService<ServerMedia>();
        var encodes = app.Services.GetRequiredService<EncodeQueue>();
        var log = app.Logger;
        int workerPort = new Uri(options.WorkerUrl).Port;
        int shimPort = new Uri(options.ShimUrl).Port;
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10) });

        // Token guessing on the worker endpoints (reachable from the internet) is slowed down per client address.
        var throttle = new AuthThrottle();
        app.Use(async (ctx, next) =>
        {
            if (ctx.Connection.LocalPort == workerPort && throttle.IsBlocked(ClientKey(ctx)))
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }
            await next();
        });

        // ---- Worker endpoint (HTTPS, LAN) ---------------------------------------------------------------

        app.Map("/ws/worker", async (HttpContext ctx) =>
        {
            if (ctx.Connection.LocalPort != workerPort || !ctx.WebSockets.IsWebSocketRequest)
                return Results.NotFound();
            // Authenticated before the upgrade: an unknown or revoked token never gets a socket.
            var identity = store.Authenticate(Bearer(ctx));
            if (identity is null)
            {
                bool blocked = throttle.Failure(ClientKey(ctx));
                log.LogWarning("Refused worker connection from {Address}{Blocked}", ClientKey(ctx), blocked ? " (blocked after repeated failures)" : "");
                return Results.Unauthorized();
            }
            throttle.Success(ClientKey(ctx));
            var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            // Behind the DSM reverse proxy the real client address is in X-Real-IP. Display only: the header is not
            // verified and must never be used for access decisions.
            string address = ctx.Request.Headers["X-Real-IP"].FirstOrDefault() ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            var worker = new WorkerConnection(identity, socket, address);
            dispatcher.AddWorker(worker);
            try
            {
                // Messages are handled one at a time, in order: a job's answer is always processed before its end.
                while (await worker.ReceiveAsync(ctx.RequestAborted) is { } message)
                    await dispatcher.OnWorkerMessageAsync(worker, message);
            }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or OperationCanceledException or JsonException or InvalidDataException)
            {
                log.LogInformation("Worker {Name} socket error: {Error}", identity.Name, ex.Message);
            }
            await dispatcher.DropWorkerAsync(worker, "connection closed");
            return Results.Empty;
        });

        // Media and filter files for a job, readable only with that job's token while it runs. Range requests let
        // ffmpeg seek inside the source exactly as it would on a local file.
        app.MapGet("/jobs/{jobId}/files/{fileId:int}", (HttpContext ctx, string jobId, int fileId) =>
        {
            if (ctx.Connection.LocalPort != workerPort)
                return Results.NotFound();
            if (dispatcher.FindJob(jobId) is not { IsActive: true } job)
                return Results.Unauthorized();
            if (!TokenMatches(job, ctx))
            {
                // A wrong token for a running job is a guess, not a late worker: it counts.
                throttle.Failure(ClientKey(ctx));
                return Results.Unauthorized();
            }
            // Only files the planner put in this job, re-checked against the roots at each request (links resolved).
            var file = job.Plan.Files.FirstOrDefault(f => f.Id == fileId);
            if (file is null || !ServerFiles.IsAllowed(file.ServerPath, options))
                return Results.NotFound();
            return Results.File(file.ServerPath, "application/octet-stream", enableRangeProcessing: true);
        });

        // A source the worker was told to fetch ahead (PrefetchHint): short-lived grant for one file, read-only.
        app.MapGet("/prefetch/{grantId}", (HttpContext ctx, string grantId) =>
        {
            if (ctx.Connection.LocalPort != workerPort)
                return Results.NotFound();
            if (!dispatcher.TryGetGrant(grantId, out string path, out string token))
                return Results.Unauthorized();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Bearer(ctx) ?? ""), Encoding.UTF8.GetBytes(token)))
            {
                throttle.Failure(ClientKey(ctx));
                return Results.Unauthorized();
            }
            if (!ServerFiles.IsAllowed(path, options))
                return Results.NotFound();
            return Results.File(path, "application/octet-stream", enableRangeProcessing: true);
        });

        // One encoded segment (playback) or chunk (background) uploaded by the job's worker. 410 Gone tells the worker
        // the output is no longer wanted (job stopped, lost or unknown), so it stops encoding.
        app.MapPut("/jobs/{jobId}/segments/{index:int}", async (HttpContext ctx, string jobId, int index) =>
        {
            if (ctx.Connection.LocalPort != workerPort)
                return Results.NotFound();
            if (dispatcher.FindJob(jobId) is not { IsActive: true } job)
                return Results.StatusCode(StatusCodes.Status410Gone);
            if (!TokenMatches(job, ctx))
            {
                throttle.Failure(ClientKey(ctx));
                return Results.StatusCode(StatusCodes.Status410Gone);
            }
            if (!double.TryParse(ctx.Request.Query["start"].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double start)
                || !double.TryParse(ctx.Request.Query["end"].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double end))
                return Results.BadRequest("start/end missing or malformed");
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            // Optional end-to-end integrity check (header sent by the worker): a corrupted upload is refused, never delivered.
            string? expected = ctx.Request.Headers["X-Content-SHA256"];
            if (expected != null && !string.Equals(expected, Convert.ToHexString(SHA256.HashData(ms.GetBuffer().AsSpan(0, (int)ms.Length))), StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("checksum mismatch");
            bool? delivered = await dispatcher.DeliverSegmentAsync(job, index, start, end, ms.GetBuffer().AsMemory(0, (int)ms.Length));
            // 410 makes the worker stop the job; a server-side failure must only make it retry (503).
            return delivered switch
            {
                true => Results.Ok(),
                false => Results.StatusCode(StatusCodes.Status410Gone),
                null => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
            };
        });

        // ---- Shim endpoint (loopback only) ----------------------------------------------------------------

        app.Map("/ws/shim", async (HttpContext ctx) =>
        {
            if (ctx.Connection.LocalPort != shimPort || !ctx.WebSockets.IsWebSocketRequest)
                return Results.NotFound();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Bearer(ctx) ?? ""), Encoding.UTF8.GetBytes(shimSecret)))
                return Results.Unauthorized();
            var shim = new ShimConnection(await ctx.WebSockets.AcceptWebSocketAsync(), Guid.NewGuid().ToString("N")[..8]);
            try
            {
                // Sequential: a submission (up to the offer timeout per worker tried) delays this shim's later messages.
                while (await shim.ReceiveAsync(ctx.RequestAborted) is { } message)
                {
                    switch (message)
                    {
                        // Playback transcodes write HLS segments; anything else Emby asks ffmpeg for is a file conversion.
                        case ShimSubmit submit when submit.Args.Contains("segment"):
                            await shim.SendAsync(await dispatcher.SubmitPlaybackAsync(shim, submit, ctx.RequestAborted));
                            break;
                        case ShimSubmit submit:
                        {
                            var plan = EmbyConversionPlanner.Plan(submit.Args, options.AllowedRoots);
                            await shim.SendAsync(plan.Remote
                                ? await encodes.StartConversionAsync(plan, shim, ctx.RequestAborted)
                                : new ShimRejected(plan.Reason));
                            break;
                        }
                        case ShimCapacityQuery query:
                            await shim.SendAsync(dispatcher.PlaybackCapacity(query));
                            break;
                        case CancelJob cancel when dispatcher.FindJob(cancel.JobId) is { } job && job.Owner is ShimOwner o && o.Shim == shim:
                            await dispatcher.StopJobAsync(job, cancel.Reason);
                            break;
                        case CancelJob cancel:
                            await encodes.CancelAsync(cancel.JobId, "conversion arrêtée par Emby");
                            break;
                        case ConversionDone done:
                            encodes.OnConversionDone(done);
                            break;
                        case ShimStartQuery startQuery:
                            await shim.SendAsync(await dispatcher.StartAdviceAsync(startQuery));
                            break;
                        case ShimServerSpeed speed:
                            dispatcher.RecordServerSpeed(speed);
                            break;
                        case ShimPrepare prepare:
                            // Fetching ahead is only an optimisation: never awaited by the shim, never fatal.
                            _ = Task.Run(async () =>
                            {
                                try { await dispatcher.PreparePlaybackAsync(prepare); }
                                catch (Exception ex) { log.LogDebug("Prefetch preparation failed: {Error}", ex.Message); }
                            });
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or OperationCanceledException or JsonException)
            {
                log.LogDebug("Shim {Id} socket error: {Error}", shim.Id, ex.Message);
            }
            await dispatcher.OnShimClosedAsync(shim);
            await encodes.OnShimClosedAsync(shim);
            return Results.Empty;
        });

        // Everything the Emby plugin page shows: paired workers (connected or not), jobs, encodes.
        object Status()
        {
            var connected = dispatcher.Workers.ToDictionary(w => w.Identity.Id);
            return new
            {
                workers = store.Load().Select(p =>
                {
                    connected.TryGetValue(p.Id, out var w);
                    return new
                    {
                        p.Id, p.Name, p.Revoked, p.CreatedAt,
                        State = p.Revoked ? "Revoked" : w?.State.ToString() ?? "Offline",
                        Reason = w?.LastHeartbeat?.Reason,
                        LastSeen = w?.LastSeen,
                        w?.RemoteAddress,
                        Jobs = w?.Reserved ?? 0, Playbacks = w?.ReservedPlayback ?? 0, MaxJobs = w?.MaxJobs ?? 0,
                        BackgroundPaused = w?.BackgroundPaused ?? false,
                        Load = w?.LastHeartbeat?.Load,
                        Encoders = w?.Hello?.Capabilities.VideoEncoders,
                        Gpus = w?.Hello?.Capabilities.Gpus,
                        Ffmpeg = w?.Hello?.Capabilities.FfmpegVersion,
                        Version = w?.Hello?.Version,
                    };
                }),
                jobs = dispatcher.Jobs.OrderByDescending(j => j.CreatedAt).Take(50).Select(j => new
                {
                    j.Id, Kind = j.Kind.ToString(), Status = j.Status.ToString(), Worker = j.Worker.Identity.Name, j.Plan.StartIndex, Segments = j.DeliveredSegments.Count,
                    Input = j.Plan.Files.FirstOrDefault()?.ServerPath, j.Description, j.LastProgress?.Speed, Position = j.LastProgress?.TimeSeconds, j.Error, j.CreatedAt,
                }),
                encodes = encodes.Summaries(100),
                // Emby's conversion queue (queued or converting), null when the server cannot tell.
                queueRemaining = encodes.QueueRemaining(),
                settings = new { options.PublicUrl, options.OffloadInputPattern, options.EncodeLocalFallback, options.PlaybackPriority },
            };
        }

        // No secret: any local process on the server can read it (worker addresses, media paths).
        app.MapGet("/status", (HttpContext ctx) => ctx.Connection.LocalPort != shimPort ? Results.NotFound() : Results.Json(Status()));

        // ---- Administration (loopback, admin secret) -------------------------------------------------------

        // Loopback only, with the admin secret (command line) or the plugin secret (Emby plugin, which enforces
        // Emby's own administrator authentication before relaying).
        bool IsAdmin(HttpContext ctx)
        {
            if (ctx.Connection.LocalPort != shimPort)
                return false;
            byte[] given = Encoding.UTF8.GetBytes(Bearer(ctx) ?? "");
            return CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(adminSecret))
                || CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(pluginSecret));
        }

        app.MapGet("/admin/status", (HttpContext ctx) => IsAdmin(ctx) ? Results.Json(Status()) : Results.Unauthorized());
        app.MapPost("/admin/workers/pair", (HttpContext ctx, PairRequest request) =>
        {
            if (!IsAdmin(ctx))
                return Results.Unauthorized();
            string name = (request.Name ?? "").Trim();
            if (name.Length is 0 or > 64)
                return Results.BadRequest("nom de worker invalide");
            var (worker, token) = store.Pair(name);
            log.LogInformation("Worker {Name} paired ({Id}) from the administration page", name, worker.Id);
            return Results.Json(new { worker.Id, worker.Name, Code = PairingCode(options, certificate, worker, token) });
        });
        app.MapPost("/admin/workers/{id}/revoke", async (HttpContext ctx, string id) =>
        {
            if (!IsAdmin(ctx))
                return Results.Unauthorized();
            // WorkerStore.Revoke also accepts a name: live connections are dropped by the id(s) it actually revoked.
            var revoked = store.Revoke(id);
            if (revoked.Count == 0)
                return Results.NotFound();
            // A revoked worker is disconnected at once; its jobs resume elsewhere.
            foreach (var w in dispatcher.Workers.Where(w => revoked.Contains(w.Identity.Id)).ToList())
                await dispatcher.DropWorkerAsync(w, "worker revoked");
            log.LogWarning("Worker {Id} revoked ({Ids})", id, string.Join(", ", revoked));
            // A JSON body: the administration page parses every answer as JSON.
            return Results.Json(new { Revoked = revoked });
        });

        app.MapGet("/admin/encodes", (HttpContext ctx) => IsAdmin(ctx) ? Results.Json(encodes.Summaries()) : Results.Unauthorized());

        // Background loops, fire-and-forget: they end with ApplicationStopping. Each catches its own failures so one bad
        // pass never ends the loop (the encode loop does so in RunAsync).
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var stopping = app.Lifetime.ApplicationStopping;
            _ = Task.Run(() => encodes.RunAsync(stopping));
            _ = Task.Run(async () =>
            {
                while (!stopping.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), stopping);
                        await dispatcher.CheckHeartbeatsAsync();
                    }
                    catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "Heartbeat check failed");
                    }
                }
            });
        });

        log.LogInformation("Coordinator listening for workers on {Workers} ({Tls}), public address {Public}, shim on {Shim}",
            options.WorkerUrl, options.ServesTls ? "TLS, certificate " + CoordinatorCertificate.Fingerprint(certificate) : "plain HTTP behind reverse proxy",
            options.PublicUrl ?? options.WorkerUrl, options.ShimUrl);
        return app;
    }


    /// <summary>The token of an "Authorization: Bearer ..." header, or null.</summary>
    static string? Bearer(HttpContext ctx)
    {
        string? h = ctx.Request.Headers.Authorization;
        return h != null && h.StartsWith("Bearer ", StringComparison.Ordinal) ? h[7..].Trim() : null;
    }

    /// <summary>
    /// The client address used to throttle authentication failures. Behind a reverse proxy on this host every
    /// connection comes from loopback, so the proxy's X-Real-IP / X-Forwarded-For is used then, and only then
    /// (a remote client cannot spoof it: the header is trusted only on loopback connections).
    /// </summary>
    static string ClientKey(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote != null && System.Net.IPAddress.IsLoopback(remote))
        {
            string? forwarded = ctx.Request.Headers["X-Real-IP"].FirstOrDefault()
                ?? ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(forwarded))
                return forwarded;
        }
        return remote?.ToString() ?? "?";
    }

    static bool TokenMatches(Job job, HttpContext ctx) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Bearer(ctx) ?? ""), Encoding.UTF8.GetBytes(job.Token));

    static string PublicWorkerUrl(CoordinatorOptions options)
    {
        var uri = new Uri(options.WorkerUrl);
        string host = uri.Host is "0.0.0.0" or "::" ? options.CertificateNames.First() : uri.Host;
        return $"https://{host}:{uri.Port}";
    }

    /// <summary>Base64 JSON the worker imports: URL, identity, token (a secret) and, without a public URL, the pinned fingerprint.</summary>
    public static string PairingCode(CoordinatorOptions options, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate, PairedWorker worker, string token)
    {
        // Behind a reverse proxy the public certificate is validated normally; otherwise the self-signed one is pinned.
        string? fingerprint = options.PublicUrl is null ? CoordinatorCertificate.Fingerprint(certificate) : null;
        var pairing = new { url = options.PublicUrl?.TrimEnd('/') ?? PublicWorkerUrl(options), id = worker.Id, token, fingerprint };
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pairing)));
    }

    // The coordinator, the Emby plugin and the shim all run as the Emby user, so these secrets are owner-only (0600):
    // any other local account able to read them could use the loopback admin API (pairing a worker) or pose as a shim.
    static string LoadOrCreatePluginSecret(CoordinatorOptions options) =>
        CoordinatorCertificate.LoadOrCreateSecret(Path.Combine(options.DataDir, "plugin.secret"));

    static string LoadOrCreateShimSecret(CoordinatorOptions options) =>
        CoordinatorCertificate.LoadOrCreateSecret(options.ShimSecretPath);

}

/// <summary>
/// The host is started and stopped by its owner; no console or signal handling of its own. Also used by the
/// standalone Program, which handles Ctrl+C/SIGTERM itself by stopping the application.
/// </summary>
internal sealed class EmbeddedLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
