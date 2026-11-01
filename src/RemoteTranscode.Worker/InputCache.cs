using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace RemoteTranscode.Worker;

/// <summary>
/// Local read-through cache for job inputs (conversions and playback). ffmpeg reads the source from http://127.0.0.1
/// (a minimal HTTP server with range support) instead of the coordinator; data comes from 4 MB blocks kept on disk,
/// fetched from the coordinator the first time. Blocks are fetched ahead (<see cref="Prefetch"/>): the next slice of a
/// conversion while one is encoded, a playback the server started and this worker will take over. The file index
/// (moov, cues) read at every open also stays cached. Disk use is capped; least recently used blocks go first.
/// </summary>
public sealed class InputCache : IDisposable
{
    public const int BlockSize = 4 << 20;
    private readonly string root;
    private readonly long capacity;
    private readonly Action<string> log;
    private readonly TcpListener listener;
    // Random path prefix: other local processes cannot guess the URL of a source.
    private readonly string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private readonly ConcurrentDictionary<string, Source> sources = new();
    private readonly CancellationTokenSource stopping = new();
    private long used;

    private sealed class Source(string key, long size)
    {
        public string Key { get; } = key;
        public long Size { get; } = size;
        /// <summary>Where and with which job token blocks are fetched; replaced by each job that uses this source.</summary>
        public volatile Upstream? Upstream;
        public readonly ConcurrentDictionary<long, Task> Fetching = new();
        /// <summary>Prefetch in progress, cancelled when a newer one is asked for.</summary>
        public CancellationTokenSource? Prefetching;
    }

    private sealed record Upstream(string Url, string Token, HttpClient Http);

    public InputCache(string root, long capacity, Action<string> log)
    {
        this.root = root;
        this.capacity = capacity;
        this.log = log;
        // A previous session's blocks are of no use (keys are per session) and only take space.
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Directory.CreateDirectory(root);
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>A stable key for one source file: its name and size (the coordinator's job URLs change with every job).</summary>
    public static string KeyOf(string name, long size) =>
        size.ToString(CultureInfo.InvariantCulture) + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..16].ToLowerInvariant();

    /// <summary>
    /// Makes a job's input readable through the cache and returns the local URL for ffmpeg. The job's URL and token
    /// become the ones used to fetch missing blocks of this source (also for a prefetch started by this job).
    /// </summary>
    public string Attach(string key, long size, string url, string token, HttpClient http)
    {
        var source = sources.GetOrAdd(key, k => new Source(k, size));
        source.Upstream = new Upstream(url, token, http);
        Directory.CreateDirectory(Path.Combine(root, key));
        return $"http://127.0.0.1:{Port}/{secret}/{key}";
    }

    /// <summary>Fetches these ranges in the background, one block after the other; a newer prefetch of the source replaces it.</summary>
    public void Prefetch(string key, IEnumerable<(long Offset, long Length)> ranges)
    {
        if (!sources.TryGetValue(key, out var source))
            return;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        Interlocked.Exchange(ref source.Prefetching, cts)?.Cancel();
        var blocks = ranges.SelectMany(r => BlocksOf(source, r.Offset, r.Length)).Distinct().ToList();
        _ = Task.Run(async () =>
        {
            int fetched = 0;
            foreach (long block in blocks)
            {
                if (cts.IsCancellationRequested)
                    return;
                try
                {
                    if (!File.Exists(BlockPath(source, block)))
                        fetched++;
                    await EnsureAsync(source, block, cts.Token);
                }
                catch (Exception)
                {
                    return;   // the run that needs these blocks fetches them itself
                }
            }
            log($"cache : {fetched} bloc(s) de {BlockSize >> 20} Mo préchargés pour la tranche suivante");
        });
    }

    private static IEnumerable<long> BlocksOf(Source source, long offset, long length)
    {
        long start = Math.Clamp(offset, 0, source.Size), end = Math.Clamp(offset + length, 0, source.Size);
        for (long b = start / BlockSize; b * BlockSize < end; b++)
            yield return b;
    }

    private string BlockPath(Source source, long block) => Path.Combine(root, source.Key, block.ToString(CultureInfo.InvariantCulture) + ".blk");

    /// <summary>The block is on disk when this completes; concurrent callers share one fetch.</summary>
    private Task EnsureAsync(Source source, long block, CancellationToken token)
    {
        string path = BlockPath(source, block);
        if (File.Exists(path))
        {
            try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch (IOException) { }
            return Task.CompletedTask;
        }
        return source.Fetching.GetOrAdd(block, b => FetchAsync(source, b, path).ContinueWith(t =>
        {
            source.Fetching.TryRemove(b, out _);
            return t;
        }, TaskScheduler.Default).Unwrap()).WaitAsync(token);
    }

    private async Task FetchAsync(Source source, long block, string path)
    {
        long from = block * BlockSize, to = Math.Min(source.Size, from + BlockSize) - 1;
        for (int attempt = 1; ; attempt++)
        {
            var up = source.Upstream ?? throw new InvalidOperationException("source sans accès au coordinateur");
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, up.Url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", up.Token);
                request.Headers.Range = new RangeHeaderValue(from, to);
                using var response = await up.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stopping.Token);
                response.EnsureSuccessStatusCode();
                byte[] data = await response.Content.ReadAsByteArrayAsync(stopping.Token);
                if (data.Length != to - from + 1)
                    throw new IOException($"bloc {block} : {data.Length} octets reçus au lieu de {to - from + 1}");
                string part = path + ".part";
                await File.WriteAllBytesAsync(part, data, stopping.Token);
                File.Move(part, path, overwrite: true);
                Interlocked.Add(ref used, data.Length);
                Trim();
                return;
            }
            catch (Exception) when (attempt < 3 && !stopping.IsCancellationRequested)
            {
                await Task.Delay(1000 * attempt, stopping.Token);
            }
        }
    }

    /// <summary>Over capacity: the least recently used blocks are deleted (a block being read stays readable).</summary>
    private void Trim()
    {
        if (Interlocked.Read(ref used) <= capacity)
            return;
        lock (sources)
        {
            var files = new DirectoryInfo(root).EnumerateFiles("*.blk", SearchOption.AllDirectories).OrderBy(f => f.LastAccessTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (total <= capacity * 9 / 10)
                    break;
                try
                {
                    long length = f.Length;
                    f.Delete();
                    total -= length;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            Interlocked.Exchange(ref used, total);
        }
    }

    // ---- Minimal HTTP/1.1 server for ffmpeg (GET/HEAD, single range, one request per connection) ----

    private async Task AcceptLoopAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stopping.Token);
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var connection = client;
        try
        {
            var stream = client.GetStream();
            string? header = await ReadHeaderAsync(stream);
            if (header is null)
                return;
            string[] lines = header.Split("\r\n");
            string[] request = lines[0].Split(' ');
            if (request.Length < 2 || request[0] is not ("GET" or "HEAD"))
            {
                await WriteAsync(stream, "HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }
            string[] path = request[1].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (path.Length != 2 || path[0] != secret || !sources.TryGetValue(path[1], out var source))
            {
                await WriteAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }
            long start = 0, end = source.Size - 1;
            bool partial = false;
            string? range = lines.Skip(1).FirstOrDefault(l => l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase));
            if (range != null && range.Split('=', 2) is [_, var spec] && spec.Split('-', 2) is [var s, var e])
            {
                partial = true;
                start = long.Parse(s.Trim(), CultureInfo.InvariantCulture);
                if (e.Trim().Length > 0)
                    end = Math.Min(end, long.Parse(e.Trim(), CultureInfo.InvariantCulture));
            }
            if (start >= source.Size || start > end)
            {
                await WriteAsync(stream, $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{source.Size}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }
            var head = new StringBuilder(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            if (partial)
                head.Append(CultureInfo.InvariantCulture, $"Content-Range: bytes {start}-{end}/{source.Size}\r\n");
            head.Append(CultureInfo.InvariantCulture, $"Content-Length: {end - start + 1}\r\nAccept-Ranges: bytes\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n");
            await WriteAsync(stream, head.ToString());
            if (request[0] == "HEAD")
                return;

            var buffer = new byte[256 * 1024];
            for (long pos = start; pos <= end;)
            {
                long block = pos / BlockSize;
                await EnsureAsync(source, block, stopping.Token);
                // Read-ahead: ffmpeg reads on sequentially, so the next blocks are fetched while this one is sent.
                for (long ahead = block + 1; ahead <= block + 2 && ahead * BlockSize < source.Size; ahead++)
                    _ = EnsureAsync(source, ahead, stopping.Token).ContinueWith(t => t.Exception, TaskScheduler.Default);
                using var file = new FileStream(BlockPath(source, block), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                file.Position = pos - block * BlockSize;
                long blockEnd = Math.Min(end + 1, (block + 1) * BlockSize);
                while (pos < blockEnd)
                {
                    int n = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, blockEnd - pos)));
                    if (n == 0)
                        throw new IOException("bloc tronqué");
                    await stream.WriteAsync(buffer.AsMemory(0, n));
                    pos += n;
                }
            }
        }
        catch (Exception)
        {
            // Client gone (ffmpeg seeks by closing and reopening) or block unavailable: ffmpeg reconnects.
        }
    }

    private static async Task<string?> ReadHeaderAsync(NetworkStream stream)
    {
        var data = new List<byte>(1024);
        var one = new byte[1];
        while (data.Count < 16 * 1024)
        {
            if (await stream.ReadAsync(one) == 0)
                return null;
            data.Add(one[0]);
            int n = data.Count;
            if (n >= 4 && data[n - 4] == '\r' && data[n - 3] == '\n' && data[n - 2] == '\r' && data[n - 1] == '\n')
                return Encoding.ASCII.GetString(data.ToArray(), 0, n - 4);
        }
        return null;
    }

    private static Task WriteAsync(NetworkStream stream, string text) => stream.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    public void Dispose()
    {
        stopping.Cancel();
        listener.Stop();
    }
}
