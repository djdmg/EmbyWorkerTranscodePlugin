using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using RemoteTranscode.Common;
using Message = RemoteTranscode.Common.Message;

namespace RemoteTranscode.Worker;

/// <summary>
/// Runs one job: fetches the small files it needs, starts ffmpeg, uploads each finished segment and reports
/// progress. Partial output never leaves the job directory, which is deleted at the end.
/// </summary>
public sealed class JobRunner
{
    private readonly JobOffer offer;
    private readonly WorkerConfig config;
    private readonly WorkerCapabilities capabilities;
    private readonly HttpClient http;
    private readonly Func<Message, Task> send;
    private readonly Action<string> log;
    private readonly string jobDir;
    private readonly CancellationTokenSource stopping = new();
    private readonly Queue<string> stderrTail = new();
    // Written once by RunAsync, read by Stop() from the WebSocket receive loop and the availability loop.
    private Process? process;
    // Set by Stop()/Cancel() from other threads (first reason wins, under stopGate); read by the upload loop to apply the
    // truncated-segment rule.
    private string? stopReason;
    // True when the first stop came from the coordinator itself (CancelJob, 410 Gone): only then may a stopped job end
    // with exit 0 and no error. Any other deliberate stop is reported as "released:" so the work resumes elsewhere.
    private bool stopByCoordinator;
    private readonly object stopGate = new();
    // Start of the current run of failed segment deliveries (null while deliveries succeed). Upload loop / final flush only.
    private DateTime? uploadFailingSince;
    // Completed once JobEnded has been sent; lets WorkerService dispose the HttpClient this job used.
    private readonly TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Inputs are read through it (and the next slice or a playback about to move here is fetched ahead); null if unavailable.
    private readonly InputCache? cache;

    /// <summary>
    /// Jobs that failed with each hardware decoder ("nvenc/hevc") and then worked with software decoding, since the
    /// worker started. After <see cref="SuspendAfterFailures"/> of them that decoder is no longer used until the next
    /// start (each failed job was rerun at once, but that costs a start each time).
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> SuspendedDecoders = new(StringComparer.Ordinal);
    private const int SuspendAfterFailures = 3;

    // Jobs whose GPU tone mapping (libplacebo) failed since the worker started: playback that then worked tone mapped on
    // the CPU, conversion slices that failed with a Vulkan/libplacebo error. After SuspendAfterFailures of them, GPU tone
    // mapping is no longer used until the next start: playback is tone mapped on the CPU and conversions tone mapped on
    // the GPU are refused (the coordinator places them on another worker, or gives the conversion back to Emby).
    private static int toneMappingFailures;

    /// <summary>GPU tone mapping suspended after repeated failures (see <see cref="SuspendAfterFailures"/>).</summary>
    public static bool GpuToneMappingSuspended => Volatile.Read(ref toneMappingFailures) >= SuspendAfterFailures;

    private void CountToneMappingFailure()
    {
        if (Interlocked.Increment(ref toneMappingFailures) == SuspendAfterFailures)
            log($"tone mapping GPU suspendu jusqu'au prochain démarrage du worker ({SuspendAfterFailures} échecs)");
    }

    /// <summary>Without a single delivery for this long, the job is released so the coordinator resumes it elsewhere.</summary>
    private static readonly TimeSpan UploadGiveUp = TimeSpan.FromSeconds(20);
    private const string UploadReleaseReason = "released: envoi des segments impossible";
    // Index of the next segment-list line to upload. Only the upload loop and, after it has finished, the final flush
    // touch it, so it needs no lock.
    private int uploadedLines;

    public JobRunner(JobOffer offer, WorkerConfig config, WorkerCapabilities capabilities, HttpClient http, Func<Message, Task> send, Action<string> log,
        InputCache? cache = null)
    {
        this.offer = offer;
        this.cache = cache;
        this.config = config;
        this.capabilities = capabilities;
        this.http = http;
        this.send = send;
        this.log = log;
        jobDir = Path.Combine(WorkerConfig.JobsDirectory, offer.JobId);
        StartSeconds = InputSeek(offer.Args);
    }

    /// <summary>Source time this run starts at (its input -ss): ffmpeg's progress counts from there.</summary>
    public double StartSeconds { get; }

    private static double InputSeek(IReadOnlyList<string> template)
    {
        int input = template.ToList().IndexOf("-i");
        for (int i = 0; i + 1 < input; i++)
            if (template[i] == "-ss" && double.TryParse(template[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double ss))
                return ss;
        return 0;
    }

    public string JobId => offer.JobId;
    public JobKind Kind => offer.Kind;
    public int StartIndex => offer.StartIndex;
    public DateTime StartedAt { get; } = DateTime.Now;
    public double MediaSeconds { get; private set; }
    public string? StopReason => stopReason;
    public string? Encoder { get; private set; }
    public double Speed { get; private set; }
    public int SegmentsUploaded { get; private set; }
    /// <summary>Conversions: the file and slice this job encodes (null for playback, or from a coordinator before 0.7.14).</summary>
    public SliceInfo? Slice => offer.Slice;
    /// <summary>Conversions: the whole conversion's progress, sent by the coordinator (null until received).</summary>
    public double? TotalPercent { get; set; }
    public double? TotalRemainingSeconds { get; set; }

    /// <summary>Share of the slice encoded, in percent (null when the slice is not known).</summary>
    public double? SlicePercent => Slice is { } sl && sl.End > sl.Start
        ? Math.Clamp(100 * (StartSeconds + MediaSeconds - sl.Start) / (sl.End - sl.Start), 0, 100)
        : null;
    public Process? Process => process;
    public Task Completion => ended.Task;

    /// <summary>
    /// Runs the job to completion on a thread-pool thread and always ends with exactly one <see cref="JobEnded"/>.
    /// The UI reads the public progress properties concurrently; they are plain fields updated without locks
    /// (values are only displayed).
    /// </summary>
    public async Task RunAsync()
    {
        int exitCode = -1;
        string? error = null;
        try
        {
            Directory.CreateDirectory(Path.Combine(jobDir, "aux"));
            Directory.CreateDirectory(Path.Combine(jobDir, "fonts"));
            await DownloadSupportFilesAsync();

            // Inputs are read through the local cache: blocks fetched ahead (next slice of a conversion, playback
            // about to move here) are already on disk, and the file's header and index are fetched only once.
            Dictionary<int, string>? localInputs = null;
            string? cacheKey = null;
            if (cache != null)
            {
                localInputs = [];
                foreach (var f in offer.Files.Where(f => f.Kind == JobFileKind.Input))
                {
                    string key = InputCache.KeyOf(f.Name, f.Size);
                    cacheKey ??= key;
                    localInputs[f.Id] = cache.Attach(key, f.Size, $"{config.CoordinatorUrl!.TrimEnd('/')}/jobs/{offer.JobId}/files/{f.Id}", offer.JobToken, http);
                }
            }

            // GPU decoding is dropped for this job if ffmpeg failed with it before anything was produced (see below).
            string? failedDecoder = null;
            bool softwareDecoding = false;
            // Likewise for GPU tone mapping chosen by this worker (playback only: a conversion's graph is the coordinator's).
            bool cpuToneMapping = false;
            while (true)
            {
                var command = WorkerCommandBuilder.Build(offer.Args, new WorkerCommandContext
                {
                    CoordinatorBaseUrl = config.CoordinatorUrl!,
                    JobId = offer.JobId,
                    JobToken = offer.JobToken,
                    CaFile = config.CertificateFingerprint is null ? null : WorkerConfig.CaFilePath,
                    Files = offer.Files,
                    SegmentSeconds = offer.SegmentSeconds,
                    VideoEncoders = capabilities.VideoEncoders,
                    // By default the same encoder as Emby's ffmpeg on the server, so a playback handed over mid-stream stays
                    // seamless; each kind of job can be moved to the GPU in the settings.
                    PreferHardwareEncoding = offer.Kind == JobKind.Background ? config.HardwareEncodingForConversions : config.HardwareEncodingForPlayback,
                    QualityMode = offer.Kind == JobKind.Background,
                    LocalInputs = localInputs,
                    // With a GPU encoder, the GPU decodes too (codecs validated at start-up, not suspended after failures).
                    HardwareDecoders = config.HardwareDecoding && !softwareDecoding
                        ? capabilities.HwDecoders.Where(d => SuspendedDecoders.GetValueOrDefault(d) < SuspendAfterFailures).ToArray()
                        : [],
                    GpuToneMapping = cpuToneMapping || GpuToneMappingSuspended ? null : capabilities.GpuToneMapping,
                });
                Encoder = command.VideoEncoder;
                log($"job {offer.JobId}: start at segment {offer.StartIndex}, encoder {command.VideoEncoder}, decoding {command.HardwareDecoder ?? "software"}" +
                    (command.GpuToneMapped ? ", tone mapping GPU" : ""));
                log($"job {offer.JobId}: ffmpeg {Mask(string.Join(' ', command.Args.Select(Quote)))}");

                var psi = new ProcessStartInfo(config.ResolvedFfmpegPath ?? config.FfmpegPath)
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = jobDir,
                };
                foreach (string a in command.Args)
                    psi.ArgumentList.Add(a);
                process?.Dispose();
                process = Process.Start(psi)!;
                ChildProcessGuard.Attach(process);
                // Stop() may have run while support files were downloading, before there was a process to send "q" to.
                if (stopping.IsCancellationRequested)
                    RequestQuit();
                // The slice this worker is expected to take next is fetched while this one is encoded.
                if (cacheKey != null && offer.Prefetch is { Length: > 0 } next && !softwareDecoding)
                    cache!.Prefetch(cacheKey, next.Select(r => (r.Offset, r.Length)));

                // stdout (progress) and stderr must be drained continuously: if a reader stops, the pipe fills up and ffmpeg
                // blocks on its next write.
                var progress = ReadProgressAsync(process.StandardOutput);
                var stderr = ReadStderrAsync(process.StandardError);
                var uploads = UploadLoopAsync(command.SegmentListFile);
                await process.WaitForExitAsync();
                await Task.WhenAll(progress, stderr);
                await uploads;
                await FinalFlushAsync(command.SegmentListFile);   // segments closed while quitting

                // Safety net: the native decoders fall back to software by themselves on streams the GPU cannot decode, but
                // a driver or device failure would still stop ffmpeg. As long as nothing was produced, the same command is
                // run again with software decoding: same pictures, hence same segments, only slower.
                if (stopReason is null && process.ExitCode != 0 && command.HardwareDecoder is { } failed && uploadedLines == 0 && !softwareDecoding)
                {
                    log($"job {offer.JobId}: échec de FFmpeg avec le décodage matériel {failed}, nouvel essai avec le décodage logiciel");
                    failedDecoder = failed;
                    softwareDecoding = true;
                    DeleteOutput(command.SegmentListFile);
                    lock (stderrTail)
                        stderrTail.Clear();
                    continue;
                }

                // Playback tone mapped on the GPU by this worker's choice: rerun with the CPU chain (same frames and
                // timestamps, a slightly different rendering). A conversion never is (its slices must all look alike).
                if (stopReason is null && process.ExitCode != 0 && command.ToneMappedHere && uploadedLines == 0 && !cpuToneMapping)
                {
                    log($"job {offer.JobId}: échec de FFmpeg avec le tone mapping GPU, nouvel essai avec le tone mapping par le processeur");
                    cpuToneMapping = true;
                    DeleteOutput(command.SegmentListFile);
                    lock (stderrTail)
                        stderrTail.Clear();
                    continue;
                }

                // The rerun worked: what it dropped last was the cause. Repeated, it is suspended.
                if (cpuToneMapping && stopReason is null && process.ExitCode == 0)
                    CountToneMappingFailure();
                else if (failedDecoder != null && stopReason is null && process.ExitCode == 0
                    && SuspendedDecoders.AddOrUpdate(failedDecoder, 1, (_, n) => n + 1) == SuspendAfterFailures)
                    log($"décodage matériel {failedDecoder} suspendu jusqu'au prochain démarrage du worker ({SuspendAfterFailures} échecs)");
                // A conversion slice tone mapped on the GPU that failed on a Vulkan/libplacebo error before producing anything.
                if (stopReason is null && process.ExitCode != 0 && command.GpuToneMapped && !command.ToneMappedHere && uploadedLines == 0)
                {
                    bool gpuError;
                    lock (stderrTail)
                        gpuError = stderrTail.Any(l => System.Text.RegularExpressions.Regex.IsMatch(l, "vulkan|libplacebo|hwupload|hwdownload|hw_device",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                    if (gpuError)
                        CountToneMappingFailure();
                }

                // A deliberate stop ("q" or kill) is not an ffmpeg failure, whatever exit code it produced.
                exitCode = stopReason != null ? 0 : process.ExitCode;
                if (stopReason is null && process.ExitCode != 0)
                    lock (stderrTail)
                        error = "ffmpeg failed: " + string.Join(" | ", stderrTail.TakeLast(5));
                break;
            }
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            log($"job {offer.JobId}: {error}");
        }
        finally
        {
            // Contract with the coordinator (Dispatcher.OnJobEndedAsync): an error starting with "released:" means "not
            // finished, resume elsewhere"; exit code 0 without error means the job is complete (playback ends). A stop the
            // coordinator did not ask for (user release, worker exit, lost connection, failed uploads) is never complete.
            string? reason;
            bool byCoordinator;
            lock (stopGate)
                (reason, byCoordinator) = (stopReason, stopByCoordinator);
            if (reason != null && !byCoordinator)
            {
                error = reason.StartsWith("released:", StringComparison.Ordinal) ? reason : "released: " + reason;
                exitCode = 1;
            }
            try
            {
                await send(new JobEnded(offer.JobId, exitCode, error));
                log($"job {offer.JobId}: ended (exit {exitCode}{(error is null ? "" : ", " + error)}), {SegmentsUploaded} segments uploaded");
            }
            catch (Exception ex)
            {
                log($"job {offer.JobId}: fin non signalée ({ex.Message})");
            }
            TryDelete(jobDir);
            process?.Dispose();
            ended.TrySetResult();
        }
    }

    /// <summary>
    /// Graceful stop: "q" lets ffmpeg close the current segment, then it is killed if it lingers.
    /// Called from any thread (WebSocket receive loop, availability loop, UI) and possibly several times.
    /// </summary>
    public void Stop(string reason) => StopCore(reason, byCoordinator: false);

    /// <summary>Stop asked by the coordinator (CancelJob, 410 Gone): the only stop that may end with exit 0.</summary>
    public void Cancel(string reason) => StopCore(reason, byCoordinator: true);

    private void StopCore(string reason, bool byCoordinator)
    {
        lock (stopGate)
        {
            if (stopReason is null)
                (stopReason, stopByCoordinator) = (reason, byCoordinator);
        }
        stopping.Cancel();
        RequestQuit();
    }

    /// <summary>
    /// Sends ffmpeg its interactive "q" command on stdin (what Emby itself does) and arms a 5-second kill as a fallback,
    /// e.g. when ffmpeg is stuck reconnecting to an input that no longer answers.
    /// </summary>
    private void RequestQuit()
    {
        var p = process;
        try
        {
            if (p is null || p.HasExited)
                return;
            p.StandardInput.Write('q');
            p.StandardInput.Flush();
        }
        // The pipe is closed once ffmpeg exits (or is exiting), and the Process is disposed once the job has ended:
        // nothing left to ask.
        catch (Exception) { return; }
        // Kill(true) also takes any child process down; the Job Object only covers the worker itself dying.
        _ = Task.Delay(5000).ContinueWith(_ =>
        {
            try { if (!p.HasExited) p.Kill(true); } catch (Exception) { }
        });
    }

    /// <summary>
    /// Downloads subtitles, attachments and fonts before ffmpeg starts (filters need local files). Media inputs are not
    /// downloaded: ffmpeg reads them directly over HTTPS range requests.
    /// </summary>
    private async Task DownloadSupportFilesAsync()
    {
        foreach (var file in offer.Files.Where(f => f.Kind != JobFileKind.Input))
        {
            if (file.Kind == JobFileKind.Font)
            {
                // Emby's fonts rarely change and weigh ~15 MB: keep them across jobs, keyed by name and size.
                string cached = Path.Combine(WorkerConfig.Directory, "cache", "fonts", $"{file.Size}-{SafeFileName(file.Name)}");
                if (!File.Exists(cached) || new FileInfo(cached).Length != file.Size)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
                    // Per-job temporary name: two jobs may fetch the same font at the same time.
                    string part = $"{cached}.{offer.JobId}.part";
                    try
                    {
                        await DownloadAsync(file, part);
                        try
                        {
                            File.Move(part, cached, overwrite: true);
                        }
                        // The other job won the race and its copy may be open by its File.Copy: use that one.
                        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(cached) && new FileInfo(cached).Length == file.Size)
                        {
                        }
                    }
                    finally
                    {
                        try { File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }
                File.Copy(cached, Path.Combine(jobDir, "fonts", SafeFileName(file.Name)), overwrite: true);
                continue;
            }
            await DownloadAsync(file, Path.Combine(jobDir, WorkerCommandBuilder.LocalAuxPath(file).Replace('/', Path.DirectorySeparatorChar)));
        }
    }

    private async Task DownloadAsync(JobFile file, string target)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{config.CoordinatorUrl}/jobs/{offer.JobId}/files/{file.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", offer.JobToken);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stopping.Token);
        response.EnsureSuccessStatusCode();
        await using var fs = File.Create(target);
        await response.Content.CopyToAsync(fs, stopping.Token);
    }

    /// <summary>Polls the segment list while ffmpeg runs; the final flush after exit is done by <see cref="RunAsync"/>.</summary>
    private async Task UploadLoopAsync(string listFile)
    {
        var delay = TimeSpan.FromMilliseconds(200);
        while (process is { HasExited: false })
        {
            delay = await TryUploadAsync(listFile)
                ? TimeSpan.FromMilliseconds(200)
                : TimeSpan.FromMilliseconds(Math.Min(2000, delay.TotalMilliseconds * 2));
            await Task.Delay(delay);
        }
    }

    /// <summary>
    /// Segments closed after the last poll. After a deliberate stop a single attempt is made (the job resumes elsewhere
    /// or was cancelled: nobody waits for them); after a natural exit, delivery is retried until it works or gives up.
    /// </summary>
    private async Task FinalFlushAsync(string listFile)
    {
        var delay = TimeSpan.FromMilliseconds(200);
        while (!await TryUploadAsync(listFile) && stopReason is null)
        {
            await Task.Delay(delay);
            delay = TimeSpan.FromMilliseconds(Math.Min(2000, delay.TotalMilliseconds * 2));
        }
    }

    /// <summary>
    /// One delivery pass. No failure (network, coordinator error, unreadable or missing segment file) may end the upload
    /// loop: ffmpeg would keep encoding and reporting progress while nothing reaches Emby, and the playback would stall.
    /// When nothing could be delivered for <see cref="UploadGiveUp"/>, the job is released instead so the coordinator
    /// resumes it on another worker or the server. Returns false when the pass failed.
    /// </summary>
    private async Task<bool> TryUploadAsync(string listFile)
    {
        var attemptStart = DateTime.UtcNow;
        try
        {
            await UploadNewSegmentsAsync(listFile);
            uploadFailingSince = null;
            return true;
        }
        catch (Exception ex)
        {
            uploadFailingSince ??= attemptStart;
            log($"job {offer.JobId}: envoi différé ({ex.GetType().Name}: {ex.Message})");
            if (DateTime.UtcNow - uploadFailingSince >= UploadGiveUp && stopReason is null)
            {
                log($"job {offer.JobId}: aucun segment livré depuis {UploadGiveUp.TotalSeconds:0} s, la tâche est rendue au coordinateur");
                Stop(UploadReleaseReason);
            }
            return false;
        }
    }

    /// <summary>The CSV list gets one "file,start,end" line once a segment file is closed.</summary>
    private async Task UploadNewSegmentsAsync(string listFile)
    {
        string path = Path.Combine(jobDir, listFile);
        if (!File.Exists(path))
            return;
        string[] lines;
        // ffmpeg keeps the list open for writing: open with full sharing. A line still being written has fewer than
        // three fields and is picked up on the next pass.
        using (var fs = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(fs))
            lines = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (; uploadedLines < lines.Length; uploadedLines++)
        {
            string[] parts = lines[uploadedLines].Split(',');
            if (parts.Length < 3)
                return;
            string name = parts[0];
            int index = int.Parse(Path.GetFileNameWithoutExtension(name)["seg_".Length..], CultureInfo.InvariantCulture);
            // A job stopped on purpose (released, preempted, cancelled) closes its current segment early. That truncated
            // segment must not be delivered: whoever resumes re-encodes it whole, otherwise the stream has a time gap
            // where the players stall. Only the very end of a file may legitimately be shorter.
            // ffmpeg failing (exit code != 0) also closes its current segment early: same rule. A short segment that is the
            // latest one listed while ffmpeg still runs waits for the exit code (final flush) to be judged; once a later
            // line exists ffmpeg has moved on, so it was not cut short.
            double duration = double.Parse(parts[2], CultureInfo.InvariantCulture) - double.Parse(parts[1], CultureInfo.InvariantCulture);
            if (duration < offer.SegmentSeconds - 0.25 && (stopReason != null || uploadedLines == lines.Length - 1))
            {
                var p = process;
                if (stopReason is null && p is { HasExited: false })
                    return;
                if (stopReason != null || p is null || p.ExitCode != 0)
                {
                    log($"job {offer.JobId}: segment {index} tronqué par l'arrêt ({duration:0.00}s), non livré");
                    continue;
                }
            }
            if (!await UploadSegmentAsync(Path.Combine(jobDir, name), index, parts[1], parts[2]))
            {
                Cancel("coordinator no longer wants this job");
                return;
            }
        }
    }

    /// <summary>
    /// PUTs one segment (with its SHA-256 so the coordinator can reject a corrupted body). Returns false when the
    /// coordinator answers 410 Gone, i.e. the job is no longer wanted. Quick network errors are retried a few times,
    /// then thrown to the caller (the upload loop keeps retrying and eventually releases the job).
    /// </summary>
    private async Task<bool> UploadSegmentAsync(string file, int index, string start, string end)
    {
        byte[] data = await ReadClosedSegmentAsync(file);
        string hash = Convert.ToHexString(SHA256.HashData(data));
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, $"{config.CoordinatorUrl}/jobs/{offer.JobId}/segments/{index}?start={start}&end={end}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", offer.JobToken);
                request.Headers.Add("X-Content-SHA256", hash);
                request.Content = new ByteArrayContent(data);
                // Well below HttpClient's 60 s, so a hung connection counts as a failure in time for the give-up rule (a
                // segment that takes longer than this to send cannot keep up with playback anyway).
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var response = await http.SendAsync(request, timeout.Token);
                if (response.StatusCode == HttpStatusCode.Gone)
                    return false;
                response.EnsureSuccessStatusCode();
                SegmentsUploaded++;
                uploadFailingSince = null;
                // Delivered: a file still held by a scanner is removed with the job folder at the end.
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                return true;
            }
            // A timeout is not retried here: it already took 30 s.
            catch (HttpRequestException) when (attempt < 3)
            {
                await Task.Delay(300 * attempt);
            }
        }
    }

    /// <summary>
    /// Reads a segment ffmpeg has closed. An antivirus often opens new files to scan them, which can briefly refuse
    /// access (sharing violation): read with the widest sharing and retry for a few seconds.
    /// </summary>
    private static async Task<byte[]> ReadClosedSegmentAsync(string file)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var fs = new FileStream(file, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var data = new byte[fs.Length];
                await fs.ReadExactlyAsync(data);
                return data;
            }
            catch (IOException) when (attempt < 40)
            {
                await Task.Delay(100);
            }
        }
    }

    /// <summary>
    /// Parses "-progress pipe:1": blocks of key=value lines, each block ending with "progress=continue|end", which is
    /// when one report is sent to the coordinator (about once a second with -stats_period 1).
    /// </summary>
    private async Task ReadProgressAsync(StreamReader stdout)
    {
        long frame = 0;
        double fps = 0, time = 0, speed = 0;
        while (await stdout.ReadLineAsync() is { } line)
        {
            int eq = line.IndexOf('=');
            if (eq < 0)
                continue;
            string key = line[..eq], value = line[(eq + 1)..].Trim();
            // Whatever happens to one report, keep draining: a reader that stops blocks ffmpeg.
            try
            {
                switch (key)
                {
                    case "frame": long.TryParse(value, CultureInfo.InvariantCulture, out frame); break;
                    case "fps": double.TryParse(value, CultureInfo.InvariantCulture, out fps); break;
                    case "out_time_us" when long.TryParse(value, CultureInfo.InvariantCulture, out long us): time = us / 1_000_000.0; MediaSeconds = time; break;
                    case "speed": double.TryParse(value.TrimEnd('x'), CultureInfo.InvariantCulture, out speed); Speed = speed; break;
                    case "progress":
                        await send(new JobProgress(offer.JobId, frame, fps, time, speed));
                        break;
                }
            }
            catch (Exception) { }
        }
    }

    private async Task ReadStderrAsync(StreamReader stderr)
    {
        // Full ffmpeg log per job, kept with the worker logs for diagnosis and export. Optional: without it (disk full,
        // folder not writable) stderr must still be drained, or ffmpeg blocks.
        StreamWriter? jobLog = null;
        try
        {
            Directory.CreateDirectory(WorkerConfig.LogsDirectory);
            jobLog = new StreamWriter(Path.Combine(WorkerConfig.LogsDirectory, $"job-{offer.JobId}.log"), append: true) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try
        {
            while (await stderr.ReadLineAsync() is { } line)
            {
                // Last lines kept for the JobEnded error message when ffmpeg fails.
                lock (stderrTail)
                {
                    stderrTail.Enqueue(line);
                    if (stderrTail.Count > 50)
                        stderrTail.Dequeue();
                }
                try
                {
                    if (jobLog != null)
                        await jobLog.WriteLineAsync($"{DateTime.Now:HH:mm:ss.fff} {Mask(line)}");
                    if (line.Contains("[error]", StringComparison.Ordinal) || line.Contains("[fatal]", StringComparison.Ordinal))
                        log($"job {offer.JobId}: {Mask(line)}");
                }
                catch (Exception) { }
            }
        }
        finally
        {
            try { jobLog?.Dispose(); } catch (IOException) { }
        }
    }

    /// <summary>The job token appears in ffmpeg's command line (-headers) and sometimes in its log: never write it out.</summary>
    private string Mask(string text) => text.Replace(offer.JobToken, "***", StringComparison.Ordinal);

    /// <summary>
    /// Display-only quoting for the logged command line; the process gets the raw ArgumentList. CR and LF (the input
    /// headers end with them) are written as \r \n so a command stays on one log line.
    /// </summary>
    private static string Quote(string a)
    {
        a = a.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        return a.Contains(' ') || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;
    }

    private static string SafeFileName(string name)
    {
        string clean = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return clean is "" or "." or ".." ? "font.bin" : clean;
    }

    /// <summary>Removes what a failed run left (segment list, partial segments) before the job is run again.</summary>
    private void DeleteOutput(string listFile)
    {
        foreach (string file in Directory.EnumerateFiles(jobDir, "seg_*.ts").Append(Path.Combine(jobDir, listFile)))
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Best effort: an antivirus or the indexer may still hold a file for a moment. Whatever is left is removed by
    /// <see cref="WorkerHost"/> at the next start.
    /// </summary>
    private static void TryDelete(string dir)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
                return;
            }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
    }
}
