using RemoteTranscode.Common;

namespace RemoteTranscode.Coordinator;

/// <summary>Coordinator settings (coordinator.json standalone, or filled in by the Emby plugin when hosted inside Emby).</summary>
public sealed class CoordinatorOptions
{
    /// <summary>
    /// Endpoint workers connect to. "https://..." serves TLS itself with a self-signed certificate pinned by the
    /// workers; "http://127.0.0.1:..." is for running behind a TLS reverse proxy (see <see cref="PublicUrl"/>).
    /// </summary>
    public string WorkerUrl { get; set; } = "https://0.0.0.0:8097";

    /// <summary>
    /// Public HTTPS address of the reverse proxy in front of <see cref="WorkerUrl"/> (e.g. https://coor.example.org).
    /// When set, pairing codes carry this URL and no pinned fingerprint: workers validate the public certificate.
    /// </summary>
    public string? PublicUrl { get; set; }

    public bool ServesTls => WorkerUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Plain HTTP endpoint for the ffmpeg shim; must stay on loopback.</summary>
    public string ShimUrl { get; set; } = "http://127.0.0.1:8098";

    /// <summary>Holds the certificate, worker registry and shim secret.</summary>
    public string DataDir { get; set; } = "data";

    /// <summary>Media folders workers may read through /jobs/{id}/files (symbolic links resolved, see <see cref="ServerFiles"/>).</summary>
    public string[] AllowedRoots { get; set; } = [];

    /// <summary>Folders where Emby's playback outputs may be written (checked by the planner).</summary>
    public string[] OutputRoots { get; set; } = ["/var/packages/EmbyServer/var/transcoding-temp"];

    /// <summary>
    /// Optional regular expression on the input path. When set, only matching inputs are offloaded; everything else
    /// keeps Emby's normal local transcoding. Used to restrict the prototype to dedicated test media.
    /// </summary>
    public string? OffloadInputPattern { get; set; }

    /// <summary>A worker silent for this long is dropped and its jobs resume elsewhere.</summary>
    public int HeartbeatTimeoutSeconds { get; set; } = 15;

    /// <summary>How long a worker has to accept or refuse an offer before the next worker is tried.</summary>
    public int OfferTimeoutSeconds { get; set; } = 5;

    /// <summary>When a background encode must leave a worker to keep its playbacks smooth.</summary>
    public PlaybackPrioritySettings PlaybackPriority { get; set; } = new();

    // ---- Background encodes ----

    /// <summary>Encoded chunks waiting for assembly (relative to the configuration folder unless absolute).</summary>
    public string EncodeSpoolDir { get; set; } = "encode";

    /// <summary>Failed runs (not interruptions) allowed before an encode is marked as failed.</summary>
    public int EncodeMaxAttempts { get; set; } = 3;

    /// <summary>
    /// When no worker can take a background encode, encode it on the server itself as Emby would without workers
    /// (one at a time, normal priority). A worker becoming available takes over from the last chunk.
    /// </summary>
    public bool EncodeLocalFallback { get; set; } = true;

    /// <summary>
    /// How long a conversion waits for a worker before the server encodes it. Not 0: between two slices a worker's slot
    /// is briefly still taken, and the server would start (and stop again) for nothing. Playback never waits.
    /// </summary>
    public int EncodeLocalDelaySeconds { get; set; } = 20;

    /// <summary>
    /// An HDR → SDR conversion's tone mapper is chosen when its first slice starts (libplacebo when a connected worker
    /// can, zscale otherwise) and kept for the whole file. During this many seconds after the coordinator starts, a
    /// conversion that would start in zscale while some paired worker has not reconnected yet waits instead (on workers
    /// and on the server alike): after an Emby restart, Emby relaunches its conversions before the workers are back, and
    /// the GPU worker would otherwise miss the whole episode. Measured from the coordinator's start, not from the
    /// capture: later on, a GPU worker that is not connected is off, and nothing should wait for it. 0: never wait.
    /// </summary>
    public int EncodeToneMapperGraceSeconds { get; set; } = 60;

    /// <summary>
    /// A conversion the server cannot encode itself (it lacks a filter the workers use) keeps trying to place its
    /// waiting slices on the workers this long (set-aside workers retried, see EncodeSetAsideRetryMinutes) while
    /// nothing runs, before it is given back to Emby, which then converts it with its own command on the server.
    /// </summary>
    public int EncodeNoWorkerMinutes { get; set; } = 30;

    /// <summary>
    /// Workers set aside for a conversion after failing a slice are retried when, for this long, no other machine
    /// could take its waiting slices although they are connected (seen after two workers were updated during one
    /// conversion): within EncodeNoWorkerMinutes the conversion is rebalanced on the workers rather than given up.
    /// </summary>
    public int EncodeSetAsideRetryMinutes { get; set; } = 5;

    /// <summary>
    /// The server also encodes slices while workers work on the same conversion (one local ffmpeg at a time), except
    /// slices reserved for a worker and, at the end of a conversion, a slice a busy worker would finish clearly sooner.
    /// Off: the server only steps in when no worker works on the conversion at all.
    /// </summary>
    public bool EncodeLocalAlongsideWorkers { get; set; } = true;

    /// <summary>
    /// A worker gets its next slice this many seconds (wall time, from its measured speed) before its current one ends,
    /// so the new ffmpeg's start (source opening, seek, encoder init: ~4 s) overlaps the end of the current one instead
    /// of leaving the encoder idle. Needs a free slot on the worker (MaxJobs of 2 or more). 0: one slice at a time.
    /// </summary>
    public double EncodeOverlapSeconds { get; set; } = 6;

    /// <summary>
    /// While the workers encode the video, the server converts the conversion's audio ahead with Emby's own options (one
    /// at a time), so the assembly only copies streams instead of reading the whole source again
    /// to convert its audio. Used only for the commands where it gives the same file (see AudioAheadPlan); whenever
    /// it is not ready or not right, the assembly converts the audio as before.
    /// </summary>
    public bool EncodeAudioAhead { get; set; } = true;

    /// <summary>
    /// Every slice is encoded but the audio converted ahead is not ready yet: the assembly waits for it as long as
    /// converting the whole audio again would take (what the usual assembly would do), and at least this long.
    /// </summary>
    public double EncodeAudioWaitSeconds { get; set; } = 30;

    /// <summary>
    /// Number of items still waiting in Emby's conversion queue (queued or converting), shown on the workers; set by the
    /// Emby plugin, null result when unknown. Called at most every 30 s.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<int?>? ConversionQueueRemaining { get; set; }

    // ---- Automatic failure reports (see FailureReporter) ----

    /// <summary>
    /// Report failed conversions as anonymous GitHub issues on the project's repository. On by default; it does
    /// anything only when the build embedded a token (<see cref="IssueReportToken"/>).
    /// </summary>
    public bool IssueReporting { get; set; } = true;

    /// <summary>
    /// GitHub token used for the reports, taken from the plugin's build (never from a file or the repository); null:
    /// no report is ever sent. Never serialized, logged or shown.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? IssueReportToken { get; set; }

    /// <summary>
    /// Writes each report (title, then body) to this local file instead of sending it to GitHub, for the bench, which
    /// has no token. Null in production. Nothing leaves the machine when it is set.
    /// </summary>
    public string? IssueReportFile { get; set; }

    /// <summary>
    /// A source with irregular frames and Emby's -r: Emby's rate is also done by the fps filter in the slices (EmbyConversionPlanner.WithFrameRateFilter).
    /// Off: those files go on being rejected by the check when their slices come out too long, and Emby converts them again (the bench's way to show it).
    /// </summary>
    public bool EncodeFrameRateFilter { get; set; } = true;

    /// <summary>
    /// Forces how far below a frame's time the runs set -ss and -t, in seconds, instead of half a frame
    /// (EmbyConversionPlanner.SeekMargin). For the bench, which provokes a rejection with 0.001. Null in production.
    /// </summary>
    public double? EncodeSeekMarginSeconds { get; set; }

    /// <summary>
    /// Seconds added to the listed length of the concat list's middle chunk, which moves every chunk after it: a really desynchronized
    /// assembly, for the bench to see it rejected. Null in production.
    /// </summary>
    public double? EncodeTestListedDurationShift { get; set; }

    /// <summary>The server as a report describes it: "Linux x64, Emby 4.10.2" (set by the Emby plugin).</summary>
    public string ServerDescription { get; set; } =
        $"{(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "macOS" : "other OS")} " +
        $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}, standalone coordinator";

    public string ServerFfprobe { get; set; } = "/var/packages/EmbyServer/target/bin/ffprobe";
    public string ServerFfmpeg { get; set; } = "/var/packages/EmbyServer/target/bin/ffmpeg";

    /// <summary>Emby's binaries resolve their libraries relative to this folder.</summary>
    public string? ServerToolsWorkingDirectory { get; set; } = "/var/packages/EmbyServer/target";

    /// <summary>Environment Emby gives its ffmpeg (fonts, drivers...); applied to the server's own ffmpeg/ffprobe runs.</summary>
    public Dictionary<string, string> ServerEnvironment { get; set; } = [];

    /// <summary>Subject alternative names (IPs or host names) for the generated TLS certificate.</summary>
    public string[] CertificateNames { get; set; } = ["localhost"];

    /// <summary>The settings shown in the status (camelCased by the JSON answer); only what an administrator may see.</summary>
    public object StatusSettings() => new { PublicUrl, OffloadInputPattern, EncodeLocalFallback, PlaybackPriority, EncodeAudioAhead };

    public string CertificatePath => Path.Combine(DataDir, "coordinator.pfx");
    public string CertificatePemPath => Path.Combine(DataDir, "coordinator.pem");
    public string WorkersPath => Path.Combine(DataDir, "workers.json");
    public string ShimSecretPath => Path.Combine(DataDir, "shim.secret");
    public string AdminSecretPath => Path.Combine(DataDir, "admin.secret");
    public string EncodeQueuePath => Path.Combine(DataDir, "encodes.json");
}
