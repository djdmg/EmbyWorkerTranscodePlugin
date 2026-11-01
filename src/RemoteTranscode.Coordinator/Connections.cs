using System.Net.WebSockets;
using System.Text;
using RemoteTranscode.Common;

namespace RemoteTranscode.Coordinator;

/// <summary>A WebSocket with serialized sends (WebSocket allows one outstanding send at a time).</summary>
public class MessageSocket(WebSocket socket)
{
    private readonly SemaphoreSlim sendLock = new(1, 1);

    public WebSocket Socket { get; } = socket;

    public bool IsOpen => Socket.State == WebSocketState.Open;

    public async Task SendAsync(Message message, CancellationToken token = default)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(MessageJson.Serialize(message));
        await sendLock.WaitAsync(token);
        try
        {
            await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
        }
        finally
        {
            sendLock.Release();
        }
    }

    /// <summary>Sends a JSON header immediately followed by a binary payload, atomically with respect to other sends.</summary>
    public async Task SendWithPayloadAsync(Message header, ReadOnlyMemory<byte> payload, CancellationToken token = default)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(MessageJson.Serialize(header));
        await sendLock.WaitAsync(token);
        try
        {
            await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
            await Socket.SendAsync(payload, WebSocketMessageType.Binary, true, token);
        }
        finally
        {
            sendLock.Release();
        }
    }

    /// <summary>
    /// Closes the socket from our side without ever blocking for long: the close frame goes out under the send lock
    /// (so it cannot interleave with a send) with a short timeout, then the socket is aborted unless the peer completed
    /// the close in time. Aborting also ends the endpoint's pending ReceiveAsync on a half-open connection, which a
    /// plain CloseAsync would wait on forever. Never throws.
    /// </summary>
    public async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await sendLock.WaitAsync(cts.Token);
            try
            {
                if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await Socket.CloseOutputAsync(status, reason, cts.Token);
            }
            finally
            {
                sendLock.Release();
            }
            // Give the peer a moment to answer with its close frame (seen by the endpoint's receive loop).
            for (int i = 0; i < 25 && Socket.State != WebSocketState.Closed; i++)
                await Task.Delay(200);
        }
        catch (Exception)
        {
            // Timeout or socket already broken: abort below.
        }
        try
        {
            if (Socket.State != WebSocketState.Closed)
                Socket.Abort();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Reads the next text message; returns null when the socket closes. Only one reader at a time (the endpoint's
    /// receive loop); messages over 4 MB are refused so a peer cannot exhaust memory.
    /// </summary>
    public async Task<Message?> ReceiveAsync(CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await Socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            ms.Write(buffer, 0, result.Count);
            if (ms.Length > 4 * 1024 * 1024)
                throw new InvalidDataException("message too large");
            if (result.EndOfMessage)
                break;
        }
        return MessageJson.Deserialize(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
    }
}

/// <summary>
/// One authenticated worker WebSocket and its live state. A reconnect creates a new instance, so slot counts never
/// carry over from a previous connection.
/// </summary>
public sealed class WorkerConnection(PairedWorker identity, WebSocket socket, string remoteAddress) : MessageSocket(socket)
{
    // Both counters change only under lock (this); readers use Volatile.Read and may see a slightly stale value.
    private int reserved;

    /// <summary>Replaced (whole) when the worker announces another name; readers always see one consistent record.</summary>
    public PairedWorker Identity { get; set; } = identity;
    public string RemoteAddress { get; } = remoteAddress;
    /// <summary>Capabilities announced at connection; a worker is never offered a job before it arrives.</summary>
    public WorkerHello? Hello { get; set; }
    public WorkerHeartbeat? LastHeartbeat { get; set; }
    /// <summary>Time of the last message of any kind; the heartbeat loop drops the worker when it gets too old.</summary>
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    public WorkerState State => LastHeartbeat?.State ?? Hello?.State ?? WorkerState.Unavailable;
    public int MaxJobs => LastHeartbeat?.MaxJobs ?? Hello?.MaxJobs ?? 0;
    private int reservedPlayback;

    /// <summary>All jobs (playback + background) reserved or running on this worker.</summary>
    public int Reserved => Volatile.Read(ref reserved);
    public int ReservedPlayback => Volatile.Read(ref reservedPlayback);

    /// <summary>
    /// Reserves a slot before an offer is sent, so two dispatches cannot overbook a worker. Background encodes only
    /// use free slots; playback is limited by the number of playbacks only, background encodes never block it
    /// (whether they must move away is decided from the worker's real load).
    /// </summary>
    public bool TryReserve(JobKind kind)
    {
        lock (this)
        {
            if (kind == JobKind.Playback ? reservedPlayback >= MaxJobs : reserved >= MaxJobs)
                return false;
            reserved++;
            if (kind == JobKind.Playback)
                reservedPlayback++;
            return true;
        }
    }

    /// <summary>Undoes one <see cref="TryReserve"/>; call through <see cref="Job.ReleaseSlot"/> so it happens once per job.</summary>
    public void Release(JobKind kind)
    {
        lock (this)
        {
            reserved--;
            if (kind == JobKind.Playback)
                reservedPlayback--;
            // The playbacks that pushed encodes away are over: encodes may come back.
            if (reservedPlayback == 0)
                BackgroundPaused = false;
        }
    }

    /// <summary>
    /// Set when an encode had to leave this worker to keep its playbacks smooth; no background work is sent here
    /// until those playbacks have ended.
    /// </summary>
    public bool BackgroundPaused { get; set; }

    /// <summary>Lock-free pre-check used for candidate selection; <see cref="TryReserve"/> is the authoritative test.</summary>
    public bool HasRoomFor(JobKind kind) => kind == JobKind.Playback ? ReservedPlayback < MaxJobs : Reserved < MaxJobs && !BackgroundPaused;
}

/// <summary>The loopback WebSocket of one ffmpeg shim process, i.e. one ffmpeg invocation by Emby.</summary>
public sealed class ShimConnection(WebSocket socket, string id) : MessageSocket(socket)
{
    public string Id { get; } = id;
}
