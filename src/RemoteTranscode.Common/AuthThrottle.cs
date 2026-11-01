using System.Collections.Concurrent;

namespace RemoteTranscode.Common;

/// <summary>
/// Slows down token guessing on the worker endpoints, which may be reachable from the internet: a client that fails
/// authentication <see cref="MaxFailures"/> times within <see cref="Window"/> is refused for <see cref="BlockFor"/>.
/// Only real authentication failures count (a wrong worker token, a wrong job token for a running job): a worker
/// that is late for a job that just ended must never lock itself out. Memory stays bounded.
/// </summary>
public sealed class AuthThrottle(Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly ConcurrentDictionary<string, Entry> clients = new();

    public int MaxFailures { get; init; } = 10;
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan BlockFor { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Upper bound on tracked clients; beyond it the oldest entries are forgotten.</summary>
    public int MaxClients { get; init; } = 10_000;

    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset WindowStart;
        public DateTimeOffset BlockedUntil;
    }

    public bool IsBlocked(string client) =>
        clients.TryGetValue(client, out var e) && e.BlockedUntil > now();

    /// <summary>Records a failed authentication; returns true when this client is now blocked.</summary>
    public bool Failure(string client)
    {
        var t = now();
        if (clients.Count >= MaxClients)
            Prune(t);
        var e = clients.GetOrAdd(client, _ => new Entry { WindowStart = t });
        lock (e)
        {
            if (t - e.WindowStart > Window)
            {
                e.WindowStart = t;
                e.Failures = 0;
            }
            if (++e.Failures >= MaxFailures)
            {
                e.BlockedUntil = t + BlockFor;
                e.Failures = 0;
                e.WindowStart = t;
            }
            return e.BlockedUntil > t;
        }
    }

    /// <summary>A successful authentication clears the client's failures (not an active block).</summary>
    public void Success(string client)
    {
        if (clients.TryGetValue(client, out var e) && e.BlockedUntil <= now())
            clients.TryRemove(client, out _);
    }

    private void Prune(DateTimeOffset t)
    {
        foreach (var (key, e) in clients)
            if (e.BlockedUntil <= t && t - e.WindowStart > Window)
                clients.TryRemove(key, out _);
        // Still full (an attack from many addresses): forget the oldest windows rather than grow without limit.
        if (clients.Count >= MaxClients)
            foreach (var key in clients.OrderBy(kv => kv.Value.WindowStart).Take(clients.Count - MaxClients / 2).Select(kv => kv.Key))
                clients.TryRemove(key, out _);
    }
}
