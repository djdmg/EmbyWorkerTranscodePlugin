using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using RemoteTranscode.Common;
using RemoteTranscode.Coordinator;

namespace RemoteTranscode.Tests;

/// <summary>Workers older than 0.7.19 get no conversion slice (they ignore ServerFileStart) but keep their playbacks.</summary>
public class ConversionWorkersTests
{
    [Theory]
    [InlineData("0.7.19", true)]
    [InlineData("0.7.19.0", true)]
    [InlineData("0.7.19.3", true)]
    [InlineData("0.7.20.0", true)]
    [InlineData("0.8.0", true)]
    [InlineData("1.0", true)]
    [InlineData("0.7.18.0", false)]
    [InlineData("0.7.18.9", false)]
    [InlineData("0.7.18", false)]
    [InlineData("0.7", false)]
    [InlineData("0.6.99.0", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData("garbage", false)]
    [InlineData("0.7.19-beta", false)]
    [InlineData(null, false)]
    public void Version_comparison(string? version, bool allowed)
    {
        Assert.Equal(allowed, ConversionWorkers.Allowed(version));
        Assert.Equal(allowed, ConversionWorkers.Refusal(version) is null);
        Assert.Equal(allowed, ConversionWorkers.RefusalLabel(version) is null);
    }

    [Theory]
    [InlineData("0.7.17.0", "version 0.7.17, 0.7.19 required for conversions", "conversions refusées : version 0.7.17, 0.7.19 requise")]
    [InlineData("0.7.18.2", "version 0.7.18.2, 0.7.19 required for conversions", "conversions refusées : version 0.7.18.2, 0.7.19 requise")]
    [InlineData(null, "version unknown, 0.7.19 required for conversions", "conversions refusées : version inconnue, 0.7.19 requise")]
    [InlineData("garbage", "version unknown, 0.7.19 required for conversions", "conversions refusées : version inconnue, 0.7.19 requise")]
    public void Refusal_texts(string? version, string english, string french)
    {
        Assert.Equal(english, ConversionWorkers.Refusal(version));
        Assert.Equal(french, ConversionWorkers.RefusalLabel(version));
    }

    // ---- Selection -----------------------------------------------------------------------------------

    private static readonly JobRequirements Job = new("libx264", ["scale"]);
    private static readonly WorkerCapabilities Capabilities = new() { VideoEncoders = ["libx264"], Filters = ["scale"] };
    private static readonly JobPlan Plan = new(true, "test", [], [], 6, 0, "out/seg_%d.ts", "out/list.m3u8", Job);

    private sealed class CapturingLog : ILogger<Dispatcher>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    /// <summary>An open socket that accepts every send and never receives anything.</summary>
    private sealed class OpenSocket : WebSocket
    {
        public override WebSocketState State => WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token) =>
            Task.FromException<WebSocketReceiveResult>(new NotSupportedException());
    }

    private sealed class NoOwner : IJobOwner
    {
        public Task<bool> OnSegmentAsync(Job job, int index, double start, double end, ReadOnlyMemory<byte> data) => Task.FromResult(true);
        public Task OnProgressAsync(Job job, JobProgress progress) => Task.CompletedTask;
        public Task OnEndedAsync(Job job, JobEnded ended) => Task.CompletedTask;
        public Task OnLostAsync(Job job, string reason) => Task.CompletedTask;
    }

    private static Dispatcher NewDispatcher(CapturingLog? log = null) => new(new CoordinatorOptions(), log ?? new CapturingLog());

    private static async Task<WorkerConnection> ConnectAsync(Dispatcher dispatcher, string id, string? version)
    {
        var worker = new WorkerConnection(new PairedWorker(id, id, "", DateTimeOffset.UtcNow, false), new OpenSocket(), "192.0.2.1");
        dispatcher.AddWorker(worker);
        await dispatcher.OnWorkerMessageAsync(worker, new WorkerHello(id, id, version!, Capabilities, 2, WorkerState.Available));
        return worker;
    }

    /// <summary>Starts a job with the given kind; whichever worker gets the offer accepts it at once.</summary>
    private static Task<(Job? Job, string? Reason)> StartAsync(Dispatcher dispatcher, JobKind kind, IEnumerable<string>? avoid = null) =>
        dispatcher.StartJobAsync(Plan, kind, new NoOwner(), "test", CancellationToken.None,
            onCreated: job => job.Answer.TrySetResult(new JobAnswer(job.Id, true, null)), avoid: avoid);

    [Fact]
    public async Task Old_worker_is_not_offered_a_slice_and_the_reason_says_why()
    {
        var dispatcher = NewDispatcher();
        var old = await ConnectAsync(dispatcher, "worker-old", "0.7.18.0");

        var (job, why) = await StartAsync(dispatcher, JobKind.Background);

        Assert.Null(job);
        Assert.Contains("worker-old: conversions refusées : version 0.7.18, 0.7.19 requise", why);
        Assert.Equal(0, old.Reserved);
    }

    [Fact]
    public async Task Refusal_shows_in_the_reason_even_for_an_avoided_worker()
    {
        var dispatcher = NewDispatcher();
        await ConnectAsync(dispatcher, "worker-old", "0.7.18.0");

        var (job, why) = await StartAsync(dispatcher, JobKind.Background, avoid: ["worker-old"]);

        Assert.Null(job);
        Assert.Contains("conversions refusées", why);
    }

    [Fact]
    public async Task Slice_goes_to_the_recent_worker_whichever_else_is_connected()
    {
        var dispatcher = NewDispatcher();
        await ConnectAsync(dispatcher, "worker-old", "0.7.18.0");
        await ConnectAsync(dispatcher, "worker-unknown", null);
        await ConnectAsync(dispatcher, "worker-new", "0.7.19.0");

        var (job, _) = await StartAsync(dispatcher, JobKind.Background);

        Assert.Equal("worker-new", job?.Worker.Identity.Id);
    }

    [Theory]
    [InlineData("0.7.18.0")]
    [InlineData(null)]
    [InlineData("garbage")]
    public async Task Refused_worker_still_runs_playbacks(string? version)
    {
        var dispatcher = NewDispatcher();
        await ConnectAsync(dispatcher, "worker-old", version);

        Assert.True(dispatcher.HasCapacityFor(Job, JobKind.Playback));
        var (job, _) = await StartAsync(dispatcher, JobKind.Playback);

        Assert.Equal("worker-old", job?.Worker.Identity.Id);
    }

    [Fact]
    public async Task Refused_worker_counts_for_nothing_in_a_conversion()
    {
        // As with no worker connected: no delay before the server, no hand-over of a local run, no GPU tone mapping.
        var dispatcher = NewDispatcher();
        await ConnectAsync(dispatcher, "worker-old", "0.7.18.0");

        Assert.False(dispatcher.HasWorkerFor(Job));
        Assert.False(dispatcher.HasCapacityFor(Job));
        Assert.Empty(dispatcher.ConnectedCapabilities());
        Assert.Equal("worker-old : conversions refusées : version 0.7.18, 0.7.19 requise", dispatcher.ConversionRefusals());
    }

    [Fact]
    public async Task Recent_worker_counts_in_a_conversion()
    {
        var dispatcher = NewDispatcher();
        var worker = await ConnectAsync(dispatcher, "worker-new", "0.7.19.0");

        Assert.True(dispatcher.HasWorkerFor(Job));
        Assert.True(dispatcher.HasCapacityFor(Job));
        Assert.Single(dispatcher.ConnectedCapabilities());
        Assert.Null(dispatcher.ConversionRefusals());
        Assert.Null(worker.ConversionsRefused);
    }

    [Fact]
    public async Task Warning_logged_once_per_connection()
    {
        var log = new CapturingLog();
        var dispatcher = NewDispatcher(log);
        int Refusals() => log.Warnings.Count(w => w.Contains("no conversion slices"));

        var first = await ConnectAsync(dispatcher, "worker-old", "0.7.17.0");
        Assert.Equal("version 0.7.17, 0.7.19 required for conversions", first.ConversionsRefused);
        Assert.Equal(1, Refusals());

        // Scheduling passes and a repeated hello on the same connection log nothing more.
        await StartAsync(dispatcher, JobKind.Background);
        dispatcher.HasWorkerFor(Job);
        await dispatcher.OnWorkerMessageAsync(first, first.Hello!);
        Assert.Equal(1, Refusals());

        // A new connection logs it again.
        await ConnectAsync(dispatcher, "worker-old", "0.7.17.0");
        Assert.Equal(2, Refusals());
    }

    [Fact]
    public async Task No_warning_for_a_recent_worker()
    {
        var log = new CapturingLog();
        await ConnectAsync(NewDispatcher(log), "worker-new", "0.7.19.0");
        Assert.Empty(log.Warnings);
    }
}
