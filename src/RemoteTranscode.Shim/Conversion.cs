using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteTranscode.Common;

namespace RemoteTranscode.Shim;

/// <summary>
/// An Emby conversion (Convert, downloads) captured by the shim. The video is encoded remotely in chunks while this
/// process reports progress to Emby; then the shim, running as Emby's user, assembles Emby's output with Emby's own
/// command (video copied from the chunks) and verifies it. Whenever the remote path cannot finish, Emby's original
/// command runs here, exactly as without workers.
/// </summary>
public sealed class ConversionSession : IDisposable
{
    private readonly ShimConfig config;
    private readonly StderrWriter err;
    private readonly ClientWebSocket socket;
    private readonly string[] args;
    private readonly string taskId;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private volatile bool quitRequested;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    private ConversionSession(ShimConfig config, StderrWriter err, ClientWebSocket socket, string[] args, string taskId)
    {
        this.config = config;
        this.err = err;
        this.socket = socket;
        this.args = args;
        this.taskId = taskId;
    }

    /// <summary>Submits the conversion; null (coordinator unreachable or the plan rejected) means Emby's ffmpeg runs it.</summary>
    public static async Task<ConversionSession?> TryStartAsync(ShimConfig config, string[] args, StderrWriter err)
    {
        var socket = await ShimSocket.ConnectAsync(config, err);
        if (socket is null)
            return null;
        try
        {
            var answer = await ShimSocket.SubmitAsync(socket, args, config);
            if (answer is ShimAssigned assigned)
            {
                err.Line($"remote-transcode: conversion {assigned.JobId} confiée aux workers");
                return new ConversionSession(config, err, socket, args, assigned.JobId);
            }
            err.Line($"remote-transcode: conversion sur le serveur ({(answer as ShimRejected)?.Reason ?? "pas de réponse"})");
        }
        catch (Exception ex)
        {
            err.Line($"remote-transcode: coordinateur injoignable ({ex.Message}), conversion sur le serveur");
        }
        socket.Dispose();
        return null;
    }

    /// <summary>Relays progress until the chunks are ready (assembly) or the remote encode gives up (Emby's command here).</summary>
    public async Task<int> RunAsync()
    {
        err.Line("Press [q] to stop, [?] for help");
        // Emby waits for a first progress report before considering the conversion started.
        err.Line(EmbyFfmpegOutput.Progress(elapsed.Elapsed, 0, 0, 0, 0));
        StdinReader.Shared.Attach(OnStdin);
        while (true)
        {
            Message? message;
            try
            {
                (message, _) = await ShimSocket.ReceiveAsync(socket, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Connection lost or unreadable message: either way the remote path is over, Emby's command runs here.
                message = null;
                err.Line($"remote-transcode: connexion au coordinateur perdue ({ex.GetType().Name}: {ex.Message})");
            }
            switch (message)
            {
                case JobProgress p when p.JobId == taskId:
                    err.Line(EmbyFfmpegOutput.Progress(elapsed.Elapsed, 0, 0, p.TimeSeconds, p.Speed));
                    break;
                case ConversionReady ready when ready.TaskId == taskId:
                    return await AssembleAsync(ready);
                case ConversionFailed failed when failed.TaskId == taskId:
                    if (quitRequested)
                        return 0;
                    err.Line($"remote-transcode: {failed.Reason} ; conversion sur le serveur");
                    return await LocalFfmpeg.RunAsync(config, args, StdinReader.Shared);
                case null:
                    if (quitRequested)
                        return 0;
                    err.Line("remote-transcode: conversion sur le serveur");
                    return await LocalFfmpeg.RunAsync(config, args, StdinReader.Shared);
            }
        }
    }

    /// <summary>
    /// Emby's command with the video taken from the chunks, then a check before Emby gets the file. With the audio
    /// converted ahead by the server, the assembly copies it too; if that assembly fails or its result does not pass
    /// the check, the usual assembly (audio converted here) runs instead.
    /// </summary>
    private async Task<int> AssembleAsync(ConversionReady ready)
    {
        // Same split as the coordinator made (it already checked the input against the allowed roots, hence "/").
        var plan = EmbyConversionPlanner.Plan(args, ["/"]);
        string listFile = ready.ListFile;
        if (ready.AudioFile != null && ready.AudioOffset != null && plan.AudioAhead != null && config.UseAudioAhead)
        {
            string[] ahead = EmbyConversionPlanner.WithVideoOffset(plan.AudioAhead.Assembly(ready.AudioFile, ready.AudioOffset), ready.VideoOffset)
                .Select(a => a == ConversionPlan.ListPlaceholder ? listFile : a).ToArray();
            err.Line("remote-transcode: vidéo encodée par les workers et audio converti à l'avance par le serveur, assemblage du fichier demandé par Emby (flux recopiés)");
            var first = await RunAssemblyAsync(ahead, plan, plan.AudioAhead.SourceStreams.Length);
            if (first.Quit)
                return first.Code;
            if (first.Problem is null || first.QuitLate)
            {
                await SendDoneAsync(new ConversionDone(taskId, first.Problem is null, first.Problem, lastCheck));
                return first.Code;
            }
            // The chunks are fine: the usual assembly converts the audio itself.
            err.Line($"remote-transcode: assemblage avec l'audio converti à l'avance rejeté ({first.Problem}), assemblage avec conversion de l'audio");
            try { File.Delete(plan.Output); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        else if (ready.AudioFile != null)
            err.Line("remote-transcode: audio converti à l'avance non utilisé (désactivé, ou commande non prise en charge)");
        string[] assembly = EmbyConversionPlanner.WithVideoOffset(plan.AssemblyTemplate, ready.VideoOffset)
            .Select(a => a == ConversionPlan.ListPlaceholder ? listFile : a).ToArray();
        err.Line("remote-transcode: vidéo encodée par les workers, assemblage du fichier demandé par Emby");
        var (code, problem, quit, quitLate) = await RunAssemblyAsync(assembly, plan, null);
        if (quit)
            return code;
        await SendDoneAsync(new ConversionDone(taskId, problem is null, problem, lastCheck));
        // A 'q' during verification still reaches the finished assembly run (it owns stdin until the next start).
        if (problem is null || quitRequested || quitLate)
            return code;

        // Never hand Emby a doubtful file: remove it and convert the original way.
        err.Line($"remote-transcode: résultat rejeté ({problem}), conversion sur le serveur");
        try { File.Delete(plan.Output); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return await LocalFfmpeg.RunAsync(config, args, StdinReader.Shared);
    }

    /// <summary>
    /// Runs one assembly and checks its result; <paramref name="audioStreams"/>, when given, is the number of audio
    /// streams it must have. Quit: Emby cancelled ('q') during the assembly, the coordinator is told and nothing else
    /// may run. QuitLate: a 'q' reached the finished run during the check.
    /// </summary>
    private async Task<(int Code, string? Problem, bool Quit, bool QuitLate)> RunAssemblyAsync(string[] assembly, ConversionPlan plan, int? audioStreams)
    {
        var clock = Stopwatch.StartNew();
        lastCheck = null;
        // Emby's 'q' now goes to the assembling ffmpeg, not to OnStdin: QuitReceived is the only trace of it.
        using var run = LocalFfmpeg.Start(config, assembly, StdinReader.Shared, 0, null);
        int code = await run.Completion;
        if (quitRequested || run.QuitReceived)
        {
            // Emby cancelled: no verification and above all no new full conversion; the output is left as Emby's own
            // ffmpeg would leave it after 'q'.
            await TrySend(new ConversionDone(taskId, false, "q reçu"));
            return (code, null, true, true);
        }
        err.Line(string.Create(CultureInfo.InvariantCulture, $"remote-transcode: assemblage terminé en {clock.Elapsed.TotalSeconds:0.0} s (code {code})"));
        string? problem = code != 0 ? $"assemblage terminé avec le code {code}" : null;
        if (problem is null)
        {
            try
            {
                var source = await ProbeAsync(plan.Input);
                var result = await ProbeAsync(plan.Output);
                problem = SourceInfo.VerifyDuration(source, result);
                if (problem is null && audioStreams is { } expected && result.AudioStreams != expected)
                    problem = $"{result.AudioStreams} piste(s) audio au lieu de {expected}";
                // To the frame: a few frames missing at the end (a lost tiny last chunk) are far below the duration
                // check's tolerance.
                if (problem is null)
                {
                    var sourceVideo = await VideoSpanAsync(plan.Input, source.DurationSeconds);
                    var resultVideo = await VideoSpanAsync(plan.Output, result.DurationSeconds);
                    lastCheck = $"vidéo source {sourceVideo}, résultat {resultVideo}";
                    err.Line($"remote-transcode: {lastCheck}");
                    // An end that cannot be read is never taken as a pass: the file is not handed to Emby unchecked.
                    problem = !sourceVideo.IsKnown || !resultVideo.IsKnown
                        ? $"vérification impossible : fin de la vidéo illisible ({(sourceVideo.IsKnown ? "résultat" : "source")})"
                        : SourceInfo.VerifyVideoSpan(sourceVideo, resultVideo)
                            ?? SourceInfo.VerifyVideoStart(sourceVideo, await FileStartAsync(plan.Input), resultVideo);
                    problem ??= await AudioStartProblemAsync(plan);
                }
            }
            catch (Exception ex)
            {
                problem = $"vérification impossible : {ex.Message}";
            }
        }
        return (code, problem, false, quitRequested || run.QuitReceived);
    }

    /// <summary>
    /// The audio streams start where they do in the source (SourceInfo.VerifyAudioStarts), for both assemblies. A measure that
    /// cannot be made says nothing about the file: unlike the video's end, it never rejects it.
    /// </summary>
    private async Task<string?> AudioStartProblemAsync(ConversionPlan plan)
    {
        if (EmbyConversionPlanner.WhyAudioStartIsNotCompared(plan.AssemblyTemplate) is { } why)
        {
            err.Line($"remote-transcode: début de l'audio non vérifié ({why})");
            return null;
        }
        if (EmbyConversionPlanner.AssemblySourceMaps(plan.AssemblyTemplate) is not { } maps)
            return null;
        try
        {
            var source = StreamStarts.Parse(await RunProbeAsync(StreamStarts.ProbeArgs(plan.Input)));
            var result = StreamStarts.Parse(await RunProbeAsync(StreamStarts.ProbeArgs(plan.Output)));
            return SourceInfo.VerifyAudioStarts(source, maps, result);
        }
        catch (Exception ex)
        {
            err.Line($"remote-transcode: début de l'audio non vérifié ({ex.GetType().Name})");
            return null;
        }
    }

    /// <summary>The file's start as Emby's ffprobe reads it; null when it gives none.</summary>
    private async Task<double?> FileStartAsync(string path)
    {
        using var doc = JsonDocument.Parse(await RunProbeAsync(["-v", "error", "-show_entries", "format=start_time", "-print_format", "json", path]));
        return doc.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("start_time", out var st) && double.TryParse(st.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double start)
            ? start
            : null;
    }

    /// <summary>Duration, video height and stream counts of a file, read with Emby's ffprobe.</summary>
    private async Task<SourceInfo> ProbeAsync(string path)
    {
        using var doc = JsonDocument.Parse(await RunProbeAsync(["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path]));
        double duration = double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString() ?? "0", CultureInfo.InvariantCulture);
        int height = 0, audio = 0, subs = 0;
        foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            string? type = s.GetProperty("codec_type").GetString();
            if (type == "video" && height == 0 && s.TryGetProperty("height", out var h))
                height = h.GetInt32();
            else if (type == "audio")
                audio++;
            else if (type == "subtitle")
                subs++;
        }
        return new SourceInfo(duration, 0, height, null, audio, subs);
    }

    /// <summary>
    /// Where the video starts and ends in a file, to the frame: the stream's start time, and the end of its last
    /// packets, read from the last 30 s only (no decoding).
    /// </summary>
    private async Task<VideoSpan> VideoSpanAsync(string path, double duration)
    {
        // Second try over the last 10 minutes: a probe that failed once (file briefly locked, by an antivirus for
        // instance), or a video ending well before the audio.
        var span = await VideoSpanAsync(path, duration, 30);
        if (!span.IsKnown)
        {
            err.Line($"remote-transcode: fin de la vidéo non lue dans {path} ({lastProbeError ?? "aucun paquet vidéo"}), nouvel essai");
            span = await VideoSpanAsync(path, duration, 600);
        }
        return span;
    }

    private string? lastProbeError;

    /// <summary>The check line of the last assembly (source and result video spans), sent with its verdict.</summary>
    private string? lastCheck;

    private async Task<VideoSpan> VideoSpanAsync(string path, double duration, double window)
    {
        // A bounded interval: ffprobe 9 sometimes prints no packet at all, without error, for an open-ended one.
        string from = Math.Max(0, duration - window).ToString("0.###", CultureInfo.InvariantCulture) + "%"
            + (duration + 3600).ToString("0.###", CultureInfo.InvariantCulture);
        using var doc = JsonDocument.Parse(await RunProbeAsync(["-v", "error", "-select_streams", "V:0", "-read_intervals", from,
            "-show_entries", "packet=pts_time,dts_time,duration_time:stream=start_time,avg_frame_rate", "-print_format", "json", path]));
        var root = doc.RootElement;
        double start = 0, frame = 0;
        if (root.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
        {
            var v = streams[0];
            if (v.TryGetProperty("start_time", out var st) && TryNumber(st.GetString(), out double s0))
                start = s0;
            if (v.TryGetProperty("avg_frame_rate", out var fr) && fr.GetString()?.Split('/') is [var num, var den]
                && TryNumber(num, out double n) && TryNumber(den, out double d) && n > 0 && d > 0)
                frame = d / n;
        }
        double end = double.NaN;
        bool approximate = false;
        if (root.TryGetProperty("packets", out var packets))
        {
            foreach (var p in packets.EnumerateArray())
            {
                // AVI (packed B-frames) stores no presentation time on many packets: their decoding time then counts.
                if (!(p.TryGetProperty("pts_time", out var pt) && TryNumber(pt.GetString(), out double pts)))
                {
                    if (!(p.TryGetProperty("dts_time", out var dtt) && TryNumber(dtt.GetString(), out pts)))
                        continue;
                    approximate = true;
                }
                double length = p.TryGetProperty("duration_time", out var dt) && TryNumber(dt.GetString(), out double l) ? l : frame;
                if (double.IsNaN(end) || pts + length > end)
                    end = pts + length;
            }
        }
        return new VideoSpan(start, end, frame, approximate);

        static bool TryNumber(string? text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Runs Emby's ffprobe and returns its standard output; a hung probe is killed (TimeoutException).</summary>
    private async Task<string> RunProbeAsync(IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo(config.RealFfprobe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (Directory.Exists(config.ToolsDirectory))
            psi.WorkingDirectory = config.ToolsDirectory;
        config.Prepare(psi);
        foreach (string a in arguments)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        // Both pipes are drained (a full stderr pipe would block ffprobe) and a hung probe is killed; the caller then
        // treats the result as unverifiable.
        using var timeout = new CancellationTokenSource(ProbeTimeout);
        var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
        string json, errors;
        try
        {
            json = await stdout;
            errors = await stderr;
            await p.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception) { }
            throw new TimeoutException($"ffprobe sans réponse après {ProbeTimeout.TotalSeconds:0} s");
        }
        // Kept for the log of a failed measure; JSON output is still parsed (ffprobe may warn and succeed).
        string tail = errors.Trim();
        lastProbeError = p.ExitCode != 0 || tail.Length > 0
            ? $"ffprobe code {p.ExitCode}{(tail.Length > 0 ? " : " + tail[Math.Max(0, tail.Length - 200)..] : "")}"
            : null;
        return json;
    }

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
                err.Raw("\n\n[q] command received. Exiting.\n\n");
                // Nothing to flush locally (Emby's output does not exist yet): exit once the coordinator is told to stop.
                _ = TrySend(new CancelJob(taskId, "q received")).ContinueWith(_ => Environment.Exit(0));
                break;
        }
    }

    /// <summary>
    /// The verdict must reach the coordinator, otherwise this process exiting reads as Emby cancelling and the task
    /// ends as cancelled although the file is fine. Sent, then the connection is closed cleanly; if it dropped during
    /// the assembly (no message for minutes), the verdict goes over a new connection.
    /// </summary>
    private async Task SendDoneAsync(ConversionDone done)
    {
        if (await TrySendAndClose(socket, done))
            return;
        err.Line("remote-transcode: connexion au coordinateur perdue pendant l'assemblage, nouvel envoi du résultat");
        using var again = await ShimSocket.ConnectAsync(config, err);
        if (again is null || !await TrySendAndClose(again, done))
            err.Line("remote-transcode: résultat de la conversion non transmis au coordinateur");
    }

    private static async Task<bool> TrySendAndClose(ClientWebSocket ws, Message message)
    {
        try
        {
            await ShimSocket.SendAsync(ws, message);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task TrySend(Message message)
    {
        try
        {
            await ShimSocket.SendAsync(socket, message);
        }
        catch (Exception) { }
    }

    public void Dispose() => socket.Dispose();
}

/// <summary>Coordinator connection helpers shared by playback and conversion sessions.</summary>
public static class ShimSocket
{
    /// <summary>Opens the authenticated loopback connection; null (with a reason on stderr) when it cannot.</summary>
    public static async Task<ClientWebSocket?> ConnectAsync(ShimConfig config, StderrWriter err)
    {
        string secret;
        try
        {
            secret = (await File.ReadAllTextAsync(config.SecretPath)).Trim();
        }
        catch (Exception ex)
        {
            err.Line($"remote-transcode: secret unavailable ({ex.Message}), using local ffmpeg");
            return null;
        }
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + secret);
        try
        {
            using var timeout = new CancellationTokenSource(config.ConnectTimeoutMs);
            await socket.ConnectAsync(new Uri(config.CoordinatorUrl), timeout.Token);
            return socket;
        }
        catch (Exception ex)
        {
            err.Line($"remote-transcode: coordinator unreachable ({ex.Message}), using local ffmpeg");
            socket.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Sends a submit and waits for <see cref="ShimAssigned"/>/<see cref="ShimRejected"/>; null on close. Other messages
    /// (late ones from a previous job) are dropped. A timeout cancels the receive, which aborts the socket in .NET.
    /// </summary>
    public static async Task<Message?> SubmitAsync(ClientWebSocket socket, string[] args, ShimConfig config)
    {
        await SendAsync(socket, new ShimSubmit(args));
        using var timeout = new CancellationTokenSource(config.SubmitTimeoutMs);
        while (true)
        {
            var (message, _) = await ReceiveAsync(socket, timeout.Token);
            if (message is ShimAssigned or ShimRejected or null)
                return message;
        }
    }

    /// <summary>Asks whether a worker could take this playback now; null when the coordinator does not answer.</summary>
    public static async Task<ShimCapacity?> QueryCapacityAsync(ClientWebSocket socket, string[] args, ShimConfig config)
    {
        await SendAsync(socket, new ShimCapacityQuery(args));
        using var timeout = new CancellationTokenSource(config.SubmitTimeoutMs);
        while (true)
        {
            var (message, _) = await ReceiveAsync(socket, timeout.Token);
            if (message is ShimCapacity or null)
                return message as ShimCapacity;
        }
    }

    // One send lock per socket: ClientWebSocket allows a single pending send, and the stdin thread ('q' → CancelJob)
    // sends concurrently with submits and capacity queries.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ClientWebSocket, SemaphoreSlim> SendLocks = new();

    /// <summary>One JSON text message; concurrent callers are serialised.</summary>
    public static async Task SendAsync(ClientWebSocket socket, Message message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(MessageJson.Serialize(message));
        var gate = SendLocks.GetValue(socket, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Receives a text message; a <see cref="SegmentReady"/> header is followed by its binary payload.</summary>
    public static async Task<(Message?, byte[]?)> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        var (type, data) = await ReceiveFrame(socket, token);
        if (type == WebSocketMessageType.Close)
            return (null, null);
        var message = MessageJson.Deserialize(Encoding.UTF8.GetString(data));
        if (message is SegmentReady)
        {
            var (payloadType, payload) = await ReceiveFrame(socket, token);
            if (payloadType != WebSocketMessageType.Binary)
                throw new InvalidDataException("segment payload expected");
            return (message, payload);
        }
        return (message, null);
    }

    // One complete WebSocket message (all its fragments), or Close.
    private static async Task<(WebSocketMessageType, byte[])> ReceiveFrame(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close)
                return (WebSocketMessageType.Close, []);
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return (result.MessageType, ms.ToArray());
        }
    }
}
