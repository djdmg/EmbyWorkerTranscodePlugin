using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteTranscode.Common;

namespace RemoteTranscode.Shim;

/// <summary>
/// Stand-in for Emby's ffmpeg. Emby starts it with its usual arguments. Playback transcodes (HLS segments) go to a
/// worker, the segments are written exactly where Emby expects them and the stderr lines Emby parses are printed;
/// conversions are encoded remotely and assembled here (see <see cref="ConversionSession"/>). Whenever no worker can
/// help, or one disappears, Emby's own ffmpeg takes over, resuming where the remote work stopped.
/// </summary>
public static partial class Program
{
    /// <summary>
    /// Entry point; the returned code is what Emby sees as ffmpeg's exit code. Every path that cannot complete remotely
    /// ends in <see cref="LocalFfmpeg.RunAsync"/> with Emby's own arguments.
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        var config = ShimConfig.Load();
        var output = new StderrWriter();
        // Emby runs this for everything (probes, thumbnails, subtitles, remux...): only playback transcodes and
        // conversions that re-encode video can go to a worker, the rest is Emby's own ffmpeg straight away.
        if (config.Disabled || !IsOffloadable(args))
            return await LocalFfmpeg.RunAsync(config, args, StdinReader.Shared);
        try
        {
            // Same routing rule as the coordinator: a command mentioning "segment" is a playback, anything else a conversion.
            if (!args.Contains("segment"))
            {
                using var conversion = await ConversionSession.TryStartAsync(config, args, output);
                return conversion is null ? await LocalFfmpeg.RunAsync(config, args, StdinReader.Shared) : await conversion.RunAsync();
            }
            // Text subtitle outputs (WebVTT) are extracted on the server by Emby's ffmpeg, next to the video run.
            var (video, subtitleArgs) = EmbyJobPlanner.SplitSubtitleOutputs(args);
            using var session = config.LocalFirstSeconds > 0 && !RemoteSession.TooHeavyForServer(video)
                ? await RemoteSession.TryStartLocalFirstAsync(config, video, output)
                : await RemoteSession.TryStartAsync(config, video, output);
            if (session is null)
                return await LocalFfmpeg.RunAsync(config, args, StdinReader.Shared);
            using var subtitles = subtitleArgs is null ? null : SubtitleRun.Start(config, subtitleArgs, output);
            if (subtitleArgs != null && subtitles is null)
            {
                // Without the subtitle run the player would wait for subtitle segments forever: Emby's whole command.
                session.Abandon();
                return await LocalFfmpeg.RunAsync(config, args, StdinReader.Shared);
            }
            int code = await session.RunAsync();
            if (subtitles != null)
                await subtitles.FinishAsync(session.Stopped);
            return code;
        }
        catch (Exception ex)
        {
            // Last resort, reached only before any segment was delivered: RemoteSession.RunAsync handles its own
            // errors from the exact resume point (rerunning Emby's original command later would duplicate segments).
            output.Line($"remote-transcode: unexpected error, using local ffmpeg: {ex.GetType().Name}: {ex.Message}");
            try
            {
                return await LocalFfmpeg.RunAsync(config, args, StdinReader.Shared);
            }
            catch (Exception local)
            {
                output.Line($"remote-transcode: local ffmpeg could not start: {local.Message}");
                return 1;
            }
        }
    }
}

public static partial class Program
{
    /// <summary>
    /// Cheap pre-filter before contacting the coordinator: an HLS segment transcode, or a conversion re-encoding its
    /// first video stream with x264/x265. The coordinator's planners make the real decision.
    /// </summary>
    public static bool IsOffloadable(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "-f" && args[i + 1] == "segment")
                return true;
            if (args[i] == "-c:v:0" && args[i + 1] is "libx264" or "libx265")
                return true;
        }
        return false;
    }
}

/// <summary>
/// shim.json next to the executable (written by the Emby plugin); defaults match a Synology Emby installation.
/// </summary>
public sealed class ShimConfig
{
    /// <summary>Coordinator's loopback shim endpoint (hosted inside Emby by the plugin).</summary>
    public string CoordinatorUrl { get; set; } = "ws://127.0.0.1:8098/ws/shim";
    /// <summary>Bearer secret shared with the coordinator; relative paths are resolved against the executable's folder.</summary>
    public string SecretPath { get; set; } = "../data/shim.secret";
    /// <summary>Emby's wrapper: sets the working directory and environment its ffmpeg build needs.</summary>
    public string RealFfmpeg { get; set; } = "/var/packages/EmbyServer/target/bin/emby-ffmpeg";
    /// <summary>Emby's ffprobe, used to verify an assembled conversion; it needs <see cref="ToolsDirectory"/> as working directory.</summary>
    public string RealFfprobe { get; set; } = "/var/packages/EmbyServer/target/bin/ffprobe";
    public string? ToolsDirectory { get; set; } = "/var/packages/EmbyServer/target";
    /// <summary>Environment Emby gives its ffmpeg; set for the real ffmpeg/ffprobe when Emby did not pass it already.</summary>
    public Dictionary<string, string> Environment { get; set; } = [];

    /// <summary>Kill switch: with a file named "disabled" next to the configuration, everything runs Emby's own ffmpeg.</summary>
    public bool Disabled { get; private set; }

    /// <summary>Adds <see cref="Environment"/> to a child process; variables Emby already set win (TryAdd).</summary>
    public void Prepare(ProcessStartInfo psi)
    {
        foreach (var (name, value) in Environment)
            psi.Environment.TryAdd(name, value);
    }
    /// <summary>WebSocket connect timeout; kept short because Emby waits for this process before anything plays.</summary>
    public int ConnectTimeoutMs { get; set; } = 2000;
    /// <summary>Maximum wait for an answer to a submit or capacity query (the coordinator may try several workers in turn).</summary>
    public int SubmitTimeoutMs { get; set; } = 20000;

    /// <summary>
    /// Playback starts on Emby's own ffmpeg, which answers within a second or two, and moves to a worker once it has
    /// encoded this much video ahead (seconds; 0 = straight to a worker, as before). See <see cref="RemoteSession"/>.
    /// </summary>
    public double LocalFirstSeconds { get; set; } = 20;

    /// <summary>Below this encoding speed the server is too slow to start the playback itself: it moves to a worker at once.</summary>
    public double LocalFirstMinSpeed { get; set; } = 1.2;
    /// <summary>Remote runs per playback before staying on Emby's ffmpeg (reset by a successful hand-back).</summary>
    public int MaxRemoteAttempts { get; set; } = 3;

    /// <summary>
    /// Conversions: use the audio the server converted ahead when the coordinator provides it (the assembly then only
    /// copies streams). Off: the assembly always converts the audio itself, as before 0.7.18.
    /// </summary>
    public bool UseAudioAhead { get; set; } = true;

    /// <summary>A "-name value" argument of the process that started this one (Emby), on Linux.</summary>
    private static string? ParentArgument(string name)
    {
        try
        {
            if (!OperatingSystem.IsLinux())
                return null;
            string stat = File.ReadAllText("/proc/self/stat");
            // Fields after the last ')' (the command name may contain spaces or parentheses): state, then ppid.
            int parent = int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[1], CultureInfo.InvariantCulture);
            string[] args = File.ReadAllText($"/proc/{parent}/cmdline").Split('\0');
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Never throws: Emby runs this for every ffmpeg call, so a missing, half-written or corrupt shim.json must still
    /// give a working ffmpeg. An unreadable configuration means defaults and <see cref="Disabled"/> (pure local).
    /// </summary>
    public static ShimConfig Load()
    {
        string dir = AppContext.BaseDirectory;
        ShimConfig config;
        try
        {
            string path = Path.Combine(dir, "shim.json");
            config = File.Exists(path)
                ? JsonSerializer.Deserialize<ShimConfig>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new()
                : new ShimConfig();
            // Explicit nulls in the JSON override the defaults; put them back.
            var defaults = new ShimConfig();
            config.CoordinatorUrl ??= defaults.CoordinatorUrl;
            config.SecretPath ??= defaults.SecretPath;
            config.RealFfmpeg ??= defaults.RealFfmpeg;
            config.RealFfprobe ??= defaults.RealFfprobe;
            config.Environment ??= [];
            if (!Path.IsPathRooted(config.SecretPath))
                config.SecretPath = Path.GetFullPath(Path.Combine(dir, config.SecretPath));
            config.Disabled = File.Exists(Path.Combine(dir, "disabled"));
        }
        catch (Exception)
        {
            config = new ShimConfig { Disabled = true };
        }
        // Emby moved its tools (update) while the plugin could not rewrite this file: take them from Emby's command line.
        if (!File.Exists(config.RealFfmpeg) && ParentArgument("-ffmpeg") is { } ffmpeg && File.Exists(ffmpeg))
            config.RealFfmpeg = ffmpeg;
        if (!File.Exists(config.RealFfprobe) && ParentArgument("-ffprobe") is { } ffprobe && File.Exists(ffprobe))
            config.RealFfprobe = ffprobe;
        return config;
    }
}

/// <summary>Writes stderr lines with the "HH:mm:ss.fff " prefix Emby's fork produces with -loglevel +timing.</summary>
public sealed class StderrWriter
{
    private readonly Stream stderr = Console.OpenStandardError();
    private readonly object gate = new();

    public void Line(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(EmbyFfmpegOutput.Prefix(DateTime.Now) + text + "\n");
        lock (gate)
        {
            stderr.Write(bytes);
            stderr.Flush();
        }
    }

    /// <summary>Writes text as is, without the timing prefix (the fork prints its interactive messages this way).</summary>
    public void Raw(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        lock (gate)
        {
            stderr.Write(bytes);
            stderr.Flush();
        }
    }
}

/// <summary>Single reader of the shim's stdin (Emby sends interactive commands there), redirectable to a child.</summary>
public sealed class StdinReader
{
    public static readonly StdinReader Shared = new();

    private readonly Stream stdin = Console.OpenStandardInput();
    private volatile Action<byte>? handler;
    private int started;

    /// <summary>
    /// Routes every following byte to <paramref name="onByte"/>, replacing the previous handler (the session itself,
    /// or the Emby ffmpeg currently covering for it). The reading thread starts on first use and never stops: stdin
    /// can only have one reader, and a blocked read cannot be cancelled portably.
    /// </summary>
    public void Attach(Action<byte> onByte)
    {
        handler = onByte;
        if (Interlocked.Exchange(ref started, 1) == 0)
            new Thread(Loop) { IsBackground = true, Name = "stdin" }.Start();
    }

    private void Loop()
    {
        var buffer = new byte[1];
        try
        {
            while (stdin.Read(buffer, 0, 1) == 1)
                handler?.Invoke(buffer[0]);
        }
        catch (IOException) { }
    }
}

/// <summary>Emby's own ffmpeg, run as a child of the shim (fallback, cover for a lost worker, conversion assembly).</summary>
public static partial class LocalFfmpeg
{
    /// <summary>
    /// Runs Emby's own ffmpeg with the given arguments, forwarding stdin; stdout/stderr are inherited.
    /// With a <paramref name="progressOffset"/> (a run that continues a transcode from a later position), stderr is
    /// relayed with the "time=" of progress lines shifted by that offset: Emby computes the transcoded position as
    /// start position + time, so without it its dashboard would jump back to the start of the session.
    /// </summary>
    public static async Task<int> RunAsync(ShimConfig config, IReadOnlyList<string> args, StdinReader stdin, double progressOffset = 0, ProgressClock? clock = null)
    {
        using var run = Start(config, args, stdin, progressOffset, progressOffset > 0 || clock != null ? _ => { } : null, clock);
        return await run.Completion;
    }

    /// <summary>
    /// Starts Emby's ffmpeg; with an <paramref name="observe"/>r, stderr is relayed and each line passed to it after
    /// relaying. With a <paramref name="clock"/>, relayed progress times never go below what Emby was already shown.
    /// </summary>
    public static LocalRun Start(ShimConfig config, IReadOnlyList<string> args, StdinReader stdin, double progressOffset, Action<string>? observe, ProgressClock? clock = null)
    {
        var psi = new ProcessStartInfo(config.RealFfmpeg) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = observe != null };
        config.Prepare(psi);
        foreach (string a in args)
            psi.ArgumentList.Add(a);
        var process = Process.Start(psi)!;
        var run = new LocalRun(process);
        stdin.Attach(b =>
        {
            if (b == 'q')
                run.QuitReceived = true;
            try
            {
                process.StandardInput.BaseStream.WriteByte(b);
                process.StandardInput.BaseStream.Flush();
            }
            catch (Exception) { }
        });
        Task relay = observe != null ? RelayStderrAsync(process.StandardError, progressOffset, clock, observe) : Task.CompletedTask;
        run.Completion = WaitAsync(process, relay);
        return run;
    }

    // The relay is awaited too: every SegmentComplete line has been observed before the caller reads the session state.
    private static async Task<int> WaitAsync(Process process, Task relay)
    {
        await process.WaitForExitAsync();
        await relay;
        return process.ExitCode;
    }

    /// <summary>A running Emby ffmpeg; <see cref="Completion"/> gives its exit code once stderr is fully relayed.</summary>
    public sealed class LocalRun(Process process) : IDisposable
    {
        public Task<int> Completion { get; internal set; } = Task.FromResult(0);
        /// <summary>Emby asked this ffmpeg to stop ('q'): the playback is over, it must not move anywhere.</summary>
        public volatile bool QuitReceived;

        /// <summary>Kills the run (the wrapper and the ffmpeg it started); a segment in progress is never published.</summary>
        public void Kill()
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception) { }
        }

        public void Dispose() => process.Dispose();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\btime=(\d+):(\d{2}):(\d{2}(?:\.\d+)?)")]
    private static partial System.Text.RegularExpressions.Regex ProgressTime();

    /// <summary>Copies stderr line by line (ffmpeg ends progress lines with \r or \n) shifting progress times.</summary>
    private static async Task RelayStderrAsync(StreamReader stderr, double offset, ProgressClock? clock, Action<string> observe)
    {
        using var output = Console.OpenStandardError();
        var line = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await stderr.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                char c = buffer[i];
                line.Append(c);
                if (c is '\n' or '\r')
                {
                    Write(output, line.ToString(), offset, clock);
                    observe(line.ToString());
                    line.Clear();
                }
            }
        }
        if (line.Length > 0)
            Write(output, line.ToString(), offset, clock);
    }

    private static void Write(Stream output, string text, double offset, ProgressClock? clock)
    {
        if ((offset > 0 || clock != null) && (text.Contains("elapsed=", StringComparison.Ordinal) || text.Contains("frame=", StringComparison.Ordinal)))
            text = ShiftProgressTime(text, offset, clock);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        output.Write(bytes);
        output.Flush();
    }

    /// <summary>
    /// Adds <paramref name="offset"/> seconds to every "time=HH:MM:SS.xx" of a progress line, keeping ffmpeg's format;
    /// with a <paramref name="clock"/>, the result is never below the highest time already reported.
    /// </summary>
    public static string ShiftProgressTime(string line, double offset, ProgressClock? clock = null) => ProgressTime().Replace(line, m =>
    {
        double seconds = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
            + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
            + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) + offset;
        if (clock != null)
            seconds = clock.Report(seconds);
        var t = TimeSpan.FromSeconds(seconds);
        return string.Create(CultureInfo.InvariantCulture, $"time={(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}");
    });
}

/// <summary>
/// Highest progress time shown to Emby during a playback session. Each run (worker or local) restarts slightly before
/// the point the previous one reported (it resumes at the last complete segment, a few ms early), so without this
/// Emby's transcoded position could step back after a hand-over.
/// </summary>
public sealed class ProgressClock
{
    private readonly object gate = new();
    private double max;

    /// <summary>Records <paramref name="seconds"/> and returns the time to show (never below an earlier one).</summary>
    public double Report(double seconds)
    {
        lock (gate)
        {
            max = Math.Max(max, seconds);
            return max;
        }
    }
}

/// <summary>
/// One HLS playback transcode: runs on a worker (segments relayed from the coordinator) or, when none is available, on
/// Emby's ffmpeg, moving between the two at segment boundaries. Only one receive is ever pending on the socket: the
/// receive loop runs while remote, capacity queries only while local.
/// </summary>
public sealed class RemoteSession : IDisposable
{
    private readonly ShimConfig config;
    private readonly StderrWriter err;
    private readonly ClientWebSocket socket;
    private readonly string[] originalArgs;
    private readonly string segmentListPath;
    private readonly double segmentSeconds;
    private readonly HlsPlaylist playlist;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    /// <summary>Current remote job; "" while Emby's ffmpeg runs from the start. Messages of older jobs are ignored.</summary>
    private string jobId;
    private int attempts = 1;
    /// <summary>Highest segment index Emby has been told is complete (by the worker relay or by Emby's own ffmpeg).</summary>
    private int lastCompleteIndex = -1;
    /// <summary>Absolute end time (seconds in the source) of <see cref="lastCompleteIndex"/>: the next run resumes here.</summary>
    private double lastCompleteEnd = -1;
    /// <summary>Added to progress times after a hand-over, so Emby's transcoded position keeps going forward.</summary>
    private double progressOffset;
    /// <summary>Emby's -ss: progress times are relative to it.</summary>
    private readonly double sessionStart;
    /// <summary>Emby sent 'q' while this session (not a local ffmpeg) owned stdin.</summary>
    private volatile bool quitRequested;
    /// <summary>The coordinator rejected the first submit: start on Emby's ffmpeg and poll for a worker.</summary>
    private bool startedLocally;
    /// <summary>
    /// Started on Emby's ffmpeg on purpose (fast start): the playback moves to a worker as soon as the server has
    /// encoded <see cref="ShimConfig.LocalFirstSeconds"/> ahead, or at once if it encodes too slowly to keep up.
    /// </summary>
    private bool warmUp;
    /// <summary>No hand-back to a worker before this: avoids ping-pong right after a worker was lost or released.</summary>
    private DateTime noHandBackBefore;
    private static readonly TimeSpan HandBackPoll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HandBackCooldown = TimeSpan.FromSeconds(15);
    /// <summary>A worker is free: the local run is stopped right after its next complete segment (or after this delay).</summary>
    private static readonly TimeSpan HandBackMaxWait = TimeSpan.FromSeconds(10);
    private static readonly System.Text.RegularExpressions.Regex ProgressSpeed = new(@"\bspeed=\s*(\d+(?:\.\d+)?)x", System.Text.RegularExpressions.RegexOptions.Compiled);
    /// <summary>Allowance for ffmpeg's own start (opening the source, first keyframe) before its first segment.</summary>
    private const double FirstSegmentStartup = 3;
    /// <summary>Progress times shown to Emby, kept monotonic across hand-overs.</summary>
    private readonly ProgressClock reported = new();
    /// <summary>Emby's ffmpeg currently covering for a worker, if any (killed before a forced exit).</summary>
    private volatile LocalFfmpeg.LocalRun? localRun;

    private RemoteSession(ShimConfig config, StderrWriter err, ClientWebSocket socket, string[] args, string jobId)
    {
        this.config = config;
        this.err = err;
        this.socket = socket;
        originalArgs = args;
        this.jobId = jobId;
        segmentListPath = EmbyJobPlanner.ValueOf(args, "-segment_list")!;
        segmentSeconds = EmbyJobPlanner.ParseDuration(EmbyJobPlanner.ValueOf(args, "-segment_time")) ?? 3;
        playlist = new HlsPlaylist(segmentListPath, segmentSeconds);
        sessionStart = EmbyJobPlanner.ParseDuration(EmbyJobPlanner.ValueOf(args, "-ss")) ?? 0;
    }

    /// <summary>
    /// Transcodes the server cannot start quickly enough itself (HDR tone mapping, typically 4K): they go to a worker
    /// from the first segment instead of trying the server first.
    /// </summary>
    public static bool TooHeavyForServer(IReadOnlyList<string> args) =>
        args.Any(a => a.Contains("tonemap", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Fast start: connects to the coordinator but submits nothing yet; the session starts on Emby's ffmpeg (first
    /// segment in a second or two, against several on a worker that must open the file over the network) and hands
    /// the playback to a worker later, at the exact segment, like any hand-back. Null without a coordinator.
    /// </summary>
    public static async Task<RemoteSession?> TryStartLocalFirstAsync(ShimConfig config, string[] args, StderrWriter err)
    {
        var socket = await ShimSocket.ConnectAsync(config, err);
        if (socket is null)
            return null;
        // The coordinator knows when the server is too slow for this source (measured before, or large picture):
        // then the playback goes to a worker from the first segment.
        ShimStartAdvice? advice = null;
        try
        {
            await ShimSocket.SendAsync(socket, new ShimStartQuery(args, config.LocalFirstMinSpeed));
            using var timeout = new CancellationTokenSource(2000);
            while (advice is null)
            {
                var (message, _) = await ShimSocket.ReceiveAsync(socket, timeout.Token);
                if (message is null)
                    break;
                advice = message as ShimStartAdvice;
            }
        }
        catch (Exception) { }
        if (advice is { LocalFirst: false })
        {
            socket.Dispose();
            err.Line($"remote-transcode: directement sur un worker ({advice.Reason})");
            return await TryStartAsync(config, args, err);
        }
        if (socket.State != WebSocketState.Open)
        {
            // No advice in time (the receive was cancelled, which aborts the socket): the playback still starts on
            // the server, with a new connection, so that it can move to a worker later.
            socket.Dispose();
            err.Line("remote-transcode: pas d'avis du coordinateur à temps, démarrage sur le serveur");
            socket = await ShimSocket.ConnectAsync(config, err);
            if (socket is null)
                return null;
        }
        err.Line("remote-transcode: démarrage sur le serveur, puis passage à un worker");
        return new RemoteSession(config, err, socket, args, "") { startedLocally = true, warmUp = true };
    }

    /// <summary>
    /// Connects and submits Emby's command. Null means the session cannot even poll for workers (no coordinator, no
    /// answer): the caller runs Emby's ffmpeg directly. A rejection still returns a session that starts locally.
    /// </summary>
    public static async Task<RemoteSession?> TryStartAsync(ShimConfig config, string[] args, StderrWriter err)
    {
        var socket = await ShimSocket.ConnectAsync(config, err);
        if (socket is null)
            return null;
        try
        {
            var answer = await ShimSocket.SubmitAsync(socket, args, config);
            if (answer is ShimAssigned assigned)
            {
                err.Line($"remote-transcode: job {assigned.JobId} running on worker {assigned.WorkerName}");
                return new RemoteSession(config, err, socket, args, assigned.JobId);
            }
            if (answer is ShimRejected rejected)
            {
                // Emby's own ffmpeg transcodes, and the session hands the playback to a worker once one is free.
                err.Line($"remote-transcode: not offloaded ({rejected.Reason}), using local ffmpeg");
                return new RemoteSession(config, err, socket, args, "") { startedLocally = true };
            }
            err.Line("remote-transcode: not offloaded (no answer), using local ffmpeg");
        }
        catch (Exception ex)
        {
            err.Line($"remote-transcode: coordinator unreachable ({ex.Message}), using local ffmpeg");
        }
        socket.Dispose();
        return null;
    }

    /// <summary>
    /// Runs the playback (remote relay or local cover, with hand-overs between them); the value returned is the exit
    /// code of whatever run finished it. Exceptions never escape once segments may have been delivered.
    /// </summary>
    public async Task<int> RunAsync()
    {
        try
        {
            if (startedLocally)
            {
                startedLocally = false;
                return await RunLocallyAsync(originalArgs, offset: 0);
            }
            return await RelayAsync();
        }
        catch (Exception ex)
        {
            // Last resort for anything unforeseen. Main's own fallback reruns Emby's original command, which is only
            // right before the first segment: afterwards Emby would get duplicate segments and a backward position.
            err.Line($"remote-transcode: unexpected error ({ex.GetType().Name}: {ex.Message}), continuing with local ffmpeg");
            localRun?.Kill();
            if (quitRequested || localRun?.QuitReceived == true)
                return 0;
            await CancelCurrentJobAsync("shim error");
            try
            {
                string[] resumed = lastCompleteIndex >= 0 ? ResumePoint() : originalArgs;
                return await LocalFfmpeg.RunAsync(config, resumed, StdinReader.Shared, lastCompleteIndex >= 0 ? progressOffset : 0, reported);
            }
            catch (Exception local)
            {
                err.Line($"remote-transcode: local ffmpeg could not start: {local.Message}");
                return 1;
            }
        }
    }

    /// <summary>
    /// Relays the current remote job until it ends; hand-overs recurse through <see cref="ResumeElsewhereAsync"/> and
    /// <see cref="HandBackAsync"/>.
    /// </summary>
    private async Task<int> RelayAsync()
    {
        // The fork prints this banner at startup; it is printed again after each hand-back (harmless for Emby).
        err.Line("Press [q] to stop, [?] for help");
        StdinReader.Shared.Attach(OnStdin);
        while (true)
        {
            Message? message;
            byte[]? payload;
            try
            {
                (message, payload) = await Receive(socket, CancellationToken.None);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
            {
                message = null;
                payload = null;
                err.Line($"remote-transcode: coordinator connection lost ({ex.Message})");
            }
            catch (Exception ex)
            {
                // Unreadable message (bad JSON, segment header without its payload...): this job's stream can no longer
                // be trusted. Stop it and continue from the last delivered segment like for a lost worker.
                err.Line($"remote-transcode: unreadable message from coordinator ({ex.GetType().Name}: {ex.Message})");
                if (quitRequested)
                    return 0;
                await CancelCurrentJobAsync("unreadable message");
                return await ResumeElsewhereAsync();
            }

            switch (message)
            {
                case SegmentReady seg when payload != null:
                    try
                    {
                        WriteSegment(seg, payload);
                    }
                    catch (Exception ex)
                    {
                        // Segment folder gone, permissions...: another worker would fail the same way here. Emby's own
                        // ffmpeg takes over from the last published segment and behaves exactly as without workers.
                        err.Line($"remote-transcode: cannot publish segment {seg.Index} ({ex.GetType().Name}: {ex.Message}), continuing with local ffmpeg");
                        if (quitRequested)
                            return 0;
                        await CancelCurrentJobAsync("segment write failed");
                        noHandBackBefore = DateTime.UtcNow + HandBackCooldown;
                        string[] resumed = ResumePoint();
                        return await RunLocallyAsync(resumed, progressOffset);
                    }
                    break;
                case JobProgress p when p.JobId == jobId:
                    err.Line(EmbyFfmpegOutput.Progress(elapsed.Elapsed, p.Frame, p.Fps, ReportedTime(p.TimeSeconds), p.Speed));
                    break;
                case JobEnded ended when ended.JobId == jobId:
                    if (ended.Error != null)
                        err.Line($"remote-transcode: worker reported: {ended.Error}");
                    if (quitRequested)
                        return 0;
                    // Complete only on exit 0 *without* error. The shim cannot cheaply know where the source ends, so it
                    // trusts the worker's exit 0 (the worker never reports 0 for an aborted job); any error ("released:",
                    // ffmpeg failure) or non-zero code means "not finished" and the playback continues from the last
                    // delivered segment, elsewhere or with Emby's ffmpeg.
                    if (ended.ExitCode == 0 && ended.Error is null)
                        return 0;
                    return await ResumeElsewhereAsync();
                case WorkerLost lost when lost.JobId == jobId:
                    err.Line($"remote-transcode: {lost.Reason}");
                    return await ResumeElsewhereAsync();
                case null:
                    if (quitRequested)
                        return 0;
                    return await ResumeElsewhereAsync();
            }
        }
    }

    /// <summary>
    /// Publishes a worker segment the way the fork does: written to a temporary name and renamed, so Emby never serves
    /// a partial file, then the playlist, then the SegmentComplete line (Emby may serve the segment as soon as it reads it).
    /// </summary>
    private void WriteSegment(SegmentReady seg, byte[] payload)
    {
        if (seg.JobId != jobId || seg.Index <= lastCompleteIndex && File.Exists(SegmentPath(seg.Index)))
            return;
        string final = SegmentPath(seg.Index);
        string temp = final + ".tmp";
        File.WriteAllBytes(temp, payload);
        File.Move(temp, final, overwrite: true);
        // The first segment of a run that seeks is listed from 0 by ffmpeg's segment muxer: its real start is the end
        // of the segment before it (or the session's start), else the playlist would announce a huge duration.
        double start = seg.Index == lastCompleteIndex + 1 && lastCompleteEnd >= 0 ? lastCompleteEnd
            : seg.Start > 0 ? seg.Start
            : sessionStart;
        playlist.Add(Path.GetFileName(final), Math.Max(0, seg.End - start));
        if (seg.Index > lastCompleteIndex)
        {
            lastCompleteIndex = seg.Index;
            lastCompleteEnd = seg.End;
        }
        err.Line(EmbyFfmpegOutput.SegmentComplete(seg.Index, seg.Start, seg.End, Path.GetFileName(final)));
    }

    /// <summary>
    /// Progress shown to Emby never goes past the last segment actually delivered: a worker encodes ahead of what it has
    /// uploaded, and if it is lost the transcode resumes from the delivered end, so Emby's position would jump back.
    /// </summary>
    private double ReportedTime(double workerTime)
    {
        double time = workerTime + progressOffset;
        time = lastCompleteIndex >= 0 ? Math.Min(time, Math.Max(0, lastCompleteEnd - sessionStart)) : time;
        return reported.Report(time);
    }

    // Emby's last argument is the segment pattern with "%d" (enforced by EmbyJobPlanner before any remote run).
    private string SegmentPath(int index) => originalArgs[^1].Replace("%d", index.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>
    /// The worker is gone. Restart from the first missing segment, first on another worker, else locally.
    /// The new run must start exactly where the last delivered segment ends: starting at index × segment length (as
    /// Emby does for a seek) overlaps the previous segment by a few milliseconds, which leaves a hole in the player's
    /// buffer at the junction and stalls it (measured with Emby's hls.js). The end time is the timestamp of the next
    /// frame; seeking a few milliseconds before it lands exactly on that frame.
    /// </summary>
    private async Task<int> ResumeElsewhereAsync()
    {
        // After 'q' nothing may keep transcoding: no resubmit, no local ffmpeg.
        if (quitRequested)
            return 0;
        string[] resumed = ResumePoint();
        noHandBackBefore = DateTime.UtcNow + HandBackCooldown;

        if (attempts < config.MaxRemoteAttempts && socket.State == WebSocketState.Open)
        {
            attempts++;
            try
            {
                var answer = await ShimSocket.SubmitAsync(socket, resumed, config);
                if (answer is ShimAssigned assigned)
                {
                    jobId = assigned.JobId;
                    // 'q' arrived during the submit (OnStdin could only cancel the previous job).
                    if (quitRequested)
                    {
                        await CancelCurrentJobAsync("q received");
                        return 0;
                    }
                    err.Line($"remote-transcode: job {assigned.JobId} continues on worker {assigned.WorkerName}");
                    return await RelayAsync();
                }
                err.Line($"remote-transcode: no other worker ({(answer as ShimRejected)?.Reason}), continuing with local ffmpeg");
            }
            catch (Exception ex)
            {
                err.Line($"remote-transcode: resubmit failed ({ex.Message}), continuing with local ffmpeg");
            }
        }
        if (quitRequested)
            return 0;
        return await RunLocallyAsync(resumed, progressOffset);
    }

    /// <summary>Arguments continuing the transcode right after the last delivered segment (see <see cref="ResumeElsewhereAsync"/>).</summary>
    private string[] ResumePoint()
    {
        int startIndex = int.TryParse(EmbyJobPlanner.ValueOf(originalArgs, "-segment_start_number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
        int resumeIndex = Math.Max(startIndex, lastCompleteIndex + 1);
        double position = lastCompleteIndex >= 0 && resumeIndex == lastCompleteIndex + 1
            ? Math.Max(0, lastCompleteEnd - 0.005)
            : resumeIndex * segmentSeconds;
        // Emby shows "session start + reported time"; the new run reports time from its own start.
        progressOffset = Math.Max(0, position - sessionStart);
        err.Line($"remote-transcode: resuming at segment {resumeIndex} ({position:0.000}s)");
        return ResumeArgs.Build(originalArgs, resumeIndex, position);
    }

    /// <summary>
    /// Emby's own ffmpeg covers for the missing worker. Its segments are followed (Emby reads the same lines), and every
    /// few seconds the coordinator is asked whether a worker is free again; if so the local run is stopped right after
    /// a complete segment and the playback continues on the worker from that exact point, like any other hand-over.
    /// </summary>
    private async Task<int> RunLocallyAsync(string[] args, double offset)
    {
        var segmentDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Fast start (warm-up): the first local run moves to a worker once it is far enough ahead, or too slow.
        bool warming = warmUp;
        warmUp = false;
        var localStart = DateTime.UtcNow;
        double startEnd = lastCompleteEnd >= 0 ? lastCompleteEnd : sessionStart;
        double firstEnd = -1;
        DateTime firstAt = default;
        int segments = 0;
        int produced = 0;
        // ffmpeg's own speed (progress lines): known before any segment, so even a server that misses the first
        // segment's deadline leaves its actual speed for the next starts of this source.
        double progressSpeed = double.NaN;
        var warmedUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var run = LocalFfmpeg.Start(config, args, StdinReader.Shared, offset, line =>
        {
            // Emby's ffmpeg prints Start=0 on every segment; End is the absolute end time.
            if (!EmbyFfmpegOutput.TryParseSegmentComplete(line, out int index, out _, out double end, out string? file))
            {
                if (warming && ProgressSpeed.Match(line) is { Success: true } m
                    && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s))
                    progressSpeed = s;
                return;
            }
            double duration = index == lastCompleteIndex + 1 && lastCompleteEnd >= 0 ? end - lastCompleteEnd : segmentSeconds;
            if (index > lastCompleteIndex)
            {
                lastCompleteIndex = index;
                lastCompleteEnd = end;
            }
            if (file != null)
                playlist.Record(file, duration);
            Interlocked.Increment(ref produced);
            segmentDone.TrySetResult();
            if (warming && !warmedUp.Task.IsCompleted)
            {
                // Speed is measured after the first segment (which also pays for ffmpeg's start).
                segments++;
                if (segments == 1)
                {
                    firstEnd = end;
                    firstAt = DateTime.UtcNow;
                }
                double ahead = end - startEnd;
                double speed = segments >= 2 ? (end - firstEnd) / Math.Max(0.001, (DateTime.UtcNow - firstAt).TotalSeconds) : double.NaN;
                if (ahead >= config.LocalFirstSeconds)
                {
                    ReportServerSpeed(speed);
                    warmedUp.TrySetResult();
                }
                else if (segments >= 2 && speed < config.LocalFirstMinSpeed)
                {
                    err.Line($"remote-transcode: serveur trop lent pour démarrer ({speed:0.00}x), passage au worker");
                    ReportServerSpeed(speed);
                    warmedUp.TrySetResult();
                }
            }
        }, reported);
        localRun = run;
        try
        {
            Task<ShimCapacity?>? query = null;
            string? lastCapacityAnswer = null;
            var nextQuery = DateTime.UtcNow + HandBackPoll;
            if (warming)
            {
                // The worker expected to take over fetches ahead what it will read around the hand-over.
                _ = ShimSocket.SendAsync(socket, new ShimPrepare(args, startEnd + config.LocalFirstSeconds)).ContinueWith(t => t.Exception, TaskScheduler.Default);
                // Hand-back as soon as the warm-up is over; a server that has not delivered its first segment in time
                // (ffmpeg's start, plus one segment at the minimum speed) is too slow to start this playback. Its
                // speed is not known then, so nothing is remembered: one busy moment must not decide the next starts.
                var firstSegmentDeadline = TimeSpan.FromSeconds(FirstSegmentStartup + segmentSeconds / Math.Max(0.1, config.LocalFirstMinSpeed));
                _ = Task.Delay(firstSegmentDeadline).ContinueWith(_ =>
                {
                    if (segments == 0 && warmedUp.TrySetResult())
                    {
                        err.Line($"remote-transcode: premier segment trop lent sur le serveur ({progressSpeed:0.00}x), passage au worker");
                        // Only a measured speed is remembered (a server that has not even started reports none).
                        if (progressSpeed > 0)
                            ReportServerSpeed(progressSpeed);
                    }
                }, TaskScheduler.Default);
                nextQuery = DateTime.MaxValue;
            }
            while (true)
            {
                var tick = Task.Delay(HandBackPoll);
                var finished = await Task.WhenAny(run.Completion, query ?? tick, tick, warming ? warmedUp.Task : tick);
                if (warming && warmedUp.Task.IsCompleted)
                {
                    // Warm-up over: ask for a worker right away, then poll as usual.
                    warming = false;
                    nextQuery = DateTime.UtcNow;
                }
                if (finished == run.Completion)
                    return await run.Completion;
                if (query is { IsCompleted: true })
                {
                    ShimCapacity? capacity = query.IsCompletedSuccessfully ? query.Result : null;
                    query = null;
                    if (capacity is null)
                    {
                        err.Line("remote-transcode: coordinator does not answer, staying on local ffmpeg");
                        return await run.Completion;
                    }
                    string answer = capacity.Available ? "worker free" : capacity.Reason ?? "no worker";
                    if (answer != lastCapacityAnswer)
                        err.Line($"remote-transcode: hand-back check: {answer}");
                    lastCapacityAnswer = answer;
                    if (capacity.Available && !run.QuitReceived && DateTime.UtcNow >= noHandBackBefore)
                    {
                        // Stop between two segments so no half-written segment is lost or published twice. A run
                        // that has not completed any segment yet has nothing to keep: stopped at once.
                        segmentDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        if (Volatile.Read(ref produced) > 0)
                            await Task.WhenAny(segmentDone.Task, run.Completion, Task.Delay(HandBackMaxWait));
                        if (run.Completion.IsCompleted || run.QuitReceived)
                            return await run.Completion;
                        // Local is stopped *before* submitting: the resume point must be the exact end of the last
                        // segment local published, which is only final once it no longer runs. Submitting first would
                        // send an earlier point and the worker would redo segments with a different junction.
                        run.Kill();
                        await run.Completion;
                        // The dead run still owns stdin: take it back before the (possibly slow) submit so a 'q' is
                        // not lost. A 'q' that reached the run in between is in QuitReceived.
                        StdinReader.Shared.Attach(OnStdin);
                        if (run.QuitReceived || quitRequested)
                            return 0;
                        return await HandBackAsync();
                    }
                }
                if (query is null && DateTime.UtcNow >= nextQuery && DateTime.UtcNow >= noHandBackBefore && socket.State == WebSocketState.Open)
                {
                    query = ShimSocket.QueryCapacityAsync(socket, args, config);
                    nextQuery = DateTime.UtcNow + HandBackPoll;
                }
            }
        }
        catch (Exception)
        {
            // Never leave Emby's ffmpeg running behind an error: whoever handles it starts its own run.
            run.Kill();
            throw;
        }
    }

    /// <summary>
    /// A worker is free again: continue the playback there from the last segment Emby's ffmpeg completed. If the worker
    /// is no longer available, Emby's ffmpeg restarts at once from that same exact point.
    /// </summary>
    private async Task<int> HandBackAsync()
    {
        if (quitRequested)
            return 0;
        string[] resumed = ResumePoint();
        try
        {
            var answer = await ShimSocket.SubmitAsync(socket, resumed, config);
            if (answer is ShimAssigned assigned)
            {
                jobId = assigned.JobId;
                attempts = 1;
                if (quitRequested)
                {
                    await CancelCurrentJobAsync("q received");
                    return 0;
                }
                err.Line($"remote-transcode: job {assigned.JobId} back on worker {assigned.WorkerName}");
                return await RelayAsync();
            }
            err.Line($"remote-transcode: worker no longer free ({(answer as ShimRejected)?.Reason}), continuing with local ffmpeg");
        }
        catch (Exception ex)
        {
            err.Line($"remote-transcode: hand-back failed ({ex.Message}), continuing with local ffmpeg");
        }
        if (quitRequested)
            return 0;
        noHandBackBefore = DateTime.UtcNow + HandBackCooldown;
        return await RunLocallyAsync(resumed, progressOffset);
    }

    /// <summary>Tells the coordinator how fast the server started this source (remembered for its next playbacks).</summary>
    private void ReportServerSpeed(double speed)
    {
        if (double.IsNaN(speed) || socket.State != WebSocketState.Open)
            return;
        _ = ShimSocket.SendAsync(socket, new ShimServerSpeed(originalArgs, speed)).ContinueWith(t => t.Exception, TaskScheduler.Default);
    }

    /// <summary>Emby stopped the playback ('q'), as opposed to the transcode reaching the end of the file.</summary>
    public bool Stopped => quitRequested || localRun?.QuitReceived == true;

    /// <summary>The session will not run (its caller falls back to Emby's command): the job assigned to it is cancelled.</summary>
    public void Abandon()
    {
        quitRequested = true;
        CancelCurrentJobAsync("shim fallback").Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Best-effort stop of the current remote job (none while Emby's ffmpeg runs from the start).</summary>
    private async Task CancelCurrentJobAsync(string reason)
    {
        if (jobId.Length == 0 || socket.State != WebSocketState.Open)
            return;
        try
        {
            await Send(socket, new CancelJob(jobId, reason));
        }
        catch (Exception) { }
    }

    /// <summary>Emby's interactive commands while a worker runs (runs on the stdin thread, concurrently with the receive loop).</summary>
    private void OnStdin(byte b)
    {
        switch ((char)b)
        {
            case '?':
                err.Raw(EmbyFfmpegOutput.HelpText);
                break;
            case 'q':
                if (quitRequested)
                    return;
                quitRequested = true;
                // Same message as the fork; the worker stops gracefully and its JobEnded ends RunAsync with 0.
                err.Raw("\n\n[q] command received. Exiting.\n\n");
                _ = CancelCurrentJobAsync("q received");
                // If the worker never confirms, do not keep Emby waiting; a local ffmpeg started by a race with this
                // 'q' must not survive the shim (it would keep transcoding for a playback Emby stopped).
                _ = Task.Delay(10000).ContinueWith(_ =>
                {
                    localRun?.Kill();
                    Environment.Exit(0);
                });
                break;
        }
    }

    private static Task Send(ClientWebSocket socket, Message message) => ShimSocket.SendAsync(socket, message);

    private static Task<(Message?, byte[]?)> Receive(ClientWebSocket socket, CancellationToken token) => ShimSocket.ReceiveAsync(socket, token);

    public void Dispose() => socket.Dispose();
}

/// <summary>
/// Emby's ffmpeg writing the text subtitle segments of a playback whose video runs elsewhere (worker or another local
/// run). It only copies text, so it runs on the server for the whole session; stdin stays with the video run. Of its
/// output only the SegmentComplete lines of the subtitle segments are relayed, labelled as Emby's whole command labels
/// them (video:1; video:0 is the video output). Killed with the shim, whatever ends it.
/// </summary>
public sealed partial class SubtitleRun : IDisposable
{
    [System.Text.RegularExpressions.GeneratedRegex(@"SegmentComplete=video:\d+")]
    private static partial System.Text.RegularExpressions.Regex SegmentLabel();

    private readonly Process process;
    private readonly Queue<string> tail = new();
    private readonly StderrWriter err;
    private readonly EventHandler onExit;

    private SubtitleRun(Process process, StderrWriter err)
    {
        this.process = process;
        this.err = err;
        onExit = (_, _) => Kill();
        AppDomain.CurrentDomain.ProcessExit += onExit;
        _ = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                int segment = line.IndexOf("SegmentComplete=", StringComparison.Ordinal);
                if (segment >= 0)
                {
                    // Without its own timing prefix: the writer adds one.
                    err.Line(SegmentLabel().Replace(line[segment..], "SegmentComplete=video:1"));
                    continue;
                }
                lock (tail)
                {
                    tail.Enqueue(line);
                    if (tail.Count > 3)
                        tail.Dequeue();
                }
            }
        });
    }

    /// <summary>Starts the run; null (with the reason on stderr) when it cannot start.</summary>
    public static SubtitleRun? Start(ShimConfig config, IReadOnlyList<string> args, StderrWriter err)
    {
        try
        {
            var psi = new ProcessStartInfo(config.RealFfmpeg) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true };
            config.Prepare(psi);
            foreach (string a in args)
                psi.ArgumentList.Add(a);
            var run = new SubtitleRun(Process.Start(psi)!, err);
            err.Line("remote-transcode: sous-titres extraits sur le serveur");
            return run;
        }
        catch (Exception ex)
        {
            err.Line($"remote-transcode: extraction des sous-titres impossible ({ex.Message})");
            return null;
        }
    }

    /// <summary>
    /// Once the video run is over: a stopped playback stops the subtitles too ('q', then kill); a transcode that reached
    /// the end lets them finish (they are normally done long before).
    /// </summary>
    public async Task FinishAsync(bool stopped)
    {
        if (stopped && !process.HasExited)
        {
            try
            {
                process.StandardInput.BaseStream.WriteByte((byte)'q');
                process.StandardInput.BaseStream.Flush();
            }
            catch (Exception) { }
        }
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(stopped ? 5 : 60));
        try
        {
            await process.WaitForExitAsync(wait.Token);
            if (process.ExitCode != 0 && !stopped)
                lock (tail)
                    err.Line($"remote-transcode: extraction des sous-titres terminée avec le code {process.ExitCode} : {string.Join(" | ", tail)}");
        }
        catch (OperationCanceledException)
        {
            Kill();
        }
    }

    private void Kill()
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception) { }
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.ProcessExit -= onExit;
        Kill();
        process.Dispose();
    }
}

/// <summary>Rewrites Emby's arguments to start at a given segment, mirroring how Emby builds a seek.</summary>
public static class ResumeArgs
{
    /// <summary>
    /// Sets the input seek (-ss before the first -i) to <paramref name="seconds"/> and -segment_start_number to
    /// <paramref name="index"/>, so the new run's segments carry the right file names.
    /// </summary>
    public static string[] Build(IReadOnlyList<string> args, int index, double seconds)
    {
        var list = args.ToList();
        string position = seconds.ToString("0.000", CultureInfo.InvariantCulture);
        int firstInput = list.IndexOf("-i");
        int ss = list.IndexOf("-ss");
        if (ss >= 0 && ss < firstInput)
            list[ss + 1] = position;
        else
            list.InsertRange(firstInput, ["-ss", position]);
        int start = list.IndexOf("-segment_start_number");
        if (start >= 0)
            list[start + 1] = index.ToString(CultureInfo.InvariantCulture);
        else
            list.InsertRange(list.Count - 1, ["-segment_start_number", index.ToString(CultureInfo.InvariantCulture)]);
        return list.ToArray();
    }
}

/// <summary>Maintains the m3u8 segment list Emby asked ffmpeg to write, replaced atomically like the fork does.</summary>
public sealed class HlsPlaylist(string path, double segmentSeconds)
{
    private readonly List<(string Name, double Duration)> entries = [];

    /// <summary>Notes a segment written by Emby's own ffmpeg (which maintains the file itself meanwhile).</summary>
    public void Record(string name, double duration) => entries.Add((name, duration));

    /// <summary>Adds a segment delivered by a worker and rewrites the whole list (temporary file + rename).</summary>
    public void Add(string name, double duration)
    {
        entries.Add((name, duration));
        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-ALLOW-CACHE:YES\n");
        sb.Append(CultureInfo.InvariantCulture, $"#EXT-X-TARGETDURATION:{(int)Math.Ceiling(Math.Max(segmentSeconds, entries.Max(e => e.Duration)))}\n");
        foreach (var (n, d) in entries)
            sb.Append(CultureInfo.InvariantCulture, $"#EXTINF:{d:F6},\n{n}\n");
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString());
        File.Move(tmp, path, overwrite: true);
    }
}
