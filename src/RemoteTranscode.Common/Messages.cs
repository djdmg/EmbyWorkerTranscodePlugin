using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteTranscode.Common;

/// <summary>Worker states shared by the worker, the coordinator and the UI.</summary>
public enum WorkerState
{
    Offline,
    Unavailable,
    Available,
    Busy,
    Draining,
    Error,
}

public enum JobKind
{
    /// <summary>HLS transcoding for a live playback session.</summary>
    Playback,
    /// <summary>Background conversion of a whole file.</summary>
    Background,
}

/// <summary>A file the worker must fetch from the coordinator before or while running a job.</summary>
public sealed record JobFile(int Id, JobFileKind Kind, string Name, long Size);

public enum JobFileKind
{
    /// <summary>Main media input, read by ffmpeg over HTTPS with range requests.</summary>
    Input,
    /// <summary>Small file referenced by a filter (subtitles), downloaded before starting.</summary>
    Aux,
    /// <summary>Font file belonging to the fonts directory, downloaded before starting.</summary>
    Font,
}

/// <summary>
/// Capabilities measured on the worker by short real encodes/decodes, never inferred from the GPU name.
/// </summary>
public sealed record WorkerCapabilities
{
    public string FfmpegVersion { get; init; } = "";
    public string[] VideoEncoders { get; init; } = [];
    public string[] HwDecoders { get; init; } = [];
    public string[] Filters { get; init; } = [];
    public string[] Gpus { get; init; } = [];
    public int LogicalCpus { get; init; }
    public long TotalMemoryBytes { get; init; }

    /// <summary>
    /// HDR→SDR tone mapping on the GPU, validated at start-up by comparing its picture with the CPU chain's (a clean exit
    /// code is not enough: some drivers return corrupted pictures without any error). Null when unavailable, switched
    /// off in the worker's settings, or on workers before 0.7.15.
    /// </summary>
    public GpuToneMapping? GpuToneMapping { get; init; }
}

/// <param name="Method">"libplacebo" (<see cref="ToneMapper.Libplacebo"/>).</param>
/// <param name="Device">
/// Value of -init_hw_device that worked, naming the device <see cref="EmbyFilters.VulkanDevice"/> ("vulkan=vk", or
/// "vulkan=vk:Intel,disable_multiplane=1,linear_images=1" for Intel drivers whose default corrupts the picture).
/// </param>
public sealed record GpuToneMapping(string Method, string Device)
{
    public const string LibplaceboMethod = "libplacebo";

    public bool IsLibplacebo => Method == LibplaceboMethod;
}

/// <summary>
/// Machine load. GPU figures are null on PCs without a GPU (or without GPU counters): software encoding is then
/// visible only through <see cref="OwnCpuPercent"/>. "Own" is this worker's ffmpeg processes, "foreign" everything else.
/// </summary>
public sealed record WorkerLoad(double CpuPercent, double? GpuPercent, double MemoryPercent, double OwnCpuPercent,
    double? OwnGpuPercent = null, double? ForeignGpuPercent = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WorkerHello), "hello")]
[JsonDerivedType(typeof(WorkerHeartbeat), "heartbeat")]
[JsonDerivedType(typeof(JobOffer), "offer")]
[JsonDerivedType(typeof(JobAnswer), "answer")]
[JsonDerivedType(typeof(JobProgress), "progress")]
[JsonDerivedType(typeof(JobEnded), "ended")]
[JsonDerivedType(typeof(CancelJob), "cancel")]
[JsonDerivedType(typeof(ShimSubmit), "submit")]
[JsonDerivedType(typeof(ShimAssigned), "assigned")]
[JsonDerivedType(typeof(ShimRejected), "rejected")]
[JsonDerivedType(typeof(SegmentReady), "segment")]
[JsonDerivedType(typeof(WorkerLost), "workerLost")]
[JsonDerivedType(typeof(ConversionReady), "conversionReady")]
[JsonDerivedType(typeof(ConversionFailed), "conversionFailed")]
[JsonDerivedType(typeof(ConversionDone), "conversionDone")]
[JsonDerivedType(typeof(ShimCapacityQuery), "capacityQuery")]
[JsonDerivedType(typeof(ShimCapacity), "capacity")]
[JsonDerivedType(typeof(ShimPrepare), "prepare")]
[JsonDerivedType(typeof(PrefetchHint), "prefetch")]
[JsonDerivedType(typeof(ShimStartQuery), "startQuery")]
[JsonDerivedType(typeof(ShimStartAdvice), "startAdvice")]
[JsonDerivedType(typeof(ShimServerSpeed), "serverSpeed")]
[JsonDerivedType(typeof(ConversionProgress), "conversionProgress")]
public abstract record Message;

// ---- Worker <-> coordinator -------------------------------------------------------------

public sealed record WorkerHello(string WorkerId, string Name, string Version, WorkerCapabilities Capabilities, int MaxJobs, WorkerState State) : Message;

public sealed record WorkerHeartbeat(WorkerState State, int ActiveJobs, int MaxJobs, WorkerLoad Load, string? Reason) : Message;

/// <summary>
/// Job proposed to a worker. <see cref="Args"/> is an ffmpeg argument template where file references
/// are placeholders ({input:N}, {aux:N}, {fonts}, {out}, {list}); the worker substitutes and validates them.
/// </summary>
/// <summary>A byte range of a job input.</summary>
public sealed record ByteRange(long Offset, long Length);

/// <param name="Prefetch">
/// Conversions: where in the input (file 0) the slice this worker is expected to take next lies. The worker fetches it
/// into its local cache while this job runs, so the next run starts without waiting for the network. Only a hint.
/// </param>
public sealed record JobOffer(string JobId, JobKind Kind, string[] Args, JobFile[] Files, double SegmentSeconds, int StartIndex, string JobToken,
    ByteRange[]? Prefetch = null, SliceInfo? Slice = null) : Message;

/// <summary>
/// What a conversion job is, for the worker's window: the file, which slice of how many, and where the slice lies in
/// the film (seconds). Ignored by workers before 0.7.8.
/// </summary>
public sealed record SliceInfo(string Title, int Number, int Count, double Start, double End);

/// <summary>
/// Coordinator to worker (0.7.8 and later only: older workers do not know this message): progress of the whole
/// conversion a running job belongs to, in percent, and the estimated time left (null when unknown).
/// <paramref name="QueueRemaining"/>: items still waiting in Emby's conversion queue, this one included (null when the
/// server cannot tell); ignored by workers before 0.7.9. Also sent every 30 s to every worker with an empty
/// <paramref name="JobId"/>, for the queue length alone (workers before 0.7.10 ignore it, matching no job).
/// </summary>
public sealed record ConversionProgress(string JobId, double Percent, double? RemainingSeconds, int? QueueRemaining = null) : Message;

public sealed record JobAnswer(string JobId, bool Accepted, string? Reason) : Message;

public sealed record JobProgress(string JobId, long Frame, double Fps, double TimeSeconds, double Speed) : Message;

/// <param name="InputStart">The input's start as this run's ffmpeg read it ("Input #0 ... start:"), a number only; null from older workers.</param>
public sealed record JobEnded(string JobId, int ExitCode, string? Error, double? InputStart = null) : Message;

public sealed record CancelJob(string JobId, string Reason) : Message;

// ---- Shim <-> coordinator ---------------------------------------------------------------

/// <summary>Submitted by the ffmpeg shim with the exact argument list Emby gave it.</summary>
public sealed record ShimSubmit(string[] Args) : Message;

public sealed record ShimAssigned(string JobId, string WorkerName) : Message;

public sealed record ShimRejected(string Reason) : Message;

/// <summary>Asked while Emby's own ffmpeg covers for a lost worker: could a worker take this playback back now?</summary>
public sealed record ShimCapacityQuery(string[] Args) : Message;

/// <summary>Answer to <see cref="ShimCapacityQuery"/>; nothing is reserved.</summary>
public sealed record ShimCapacity(bool Available, string? Reason) : Message;

/// <summary>
/// Sent when a playback starts on the server (fast start): the worker expected to take it over fetches ahead what it
/// will read, so the hand-over does not wait for the network. <paramref name="AtSeconds"/> is the source time around
/// which the hand-over is expected. No answer.
/// </summary>
public sealed record ShimPrepare(string[] Args, double AtSeconds) : Message;

/// <summary>
/// Fast start: may this playback start on the server, or is the server known (or expected) to be too slow for it, in
/// which case it goes to a worker from the first segment? <paramref name="MinSpeed"/> is the shim's threshold.
/// </summary>
public sealed record ShimStartQuery(string[] Args, double MinSpeed) : Message;

/// <summary>Answer to <see cref="ShimStartQuery"/>.</summary>
public sealed record ShimStartAdvice(bool LocalFirst, string Reason) : Message;

/// <summary>Speed the server's own ffmpeg reached at the start of a playback, remembered per source file.</summary>
public sealed record ShimServerSpeed(string[] Args, double Speed) : Message;

/// <summary>
/// Coordinator → worker: fetch these ranges of a source into the local cache, from /prefetch/{GrantId} with this
/// short-lived token (valid for that file only).
/// </summary>
public sealed record PrefetchHint(string GrantId, string Token, JobFile File, ByteRange[] Ranges) : Message;

/// <summary>Header sent to the shim just before the binary WebSocket message carrying the segment.</summary>
public sealed record SegmentReady(string JobId, int Index, double Start, double End, long Size) : Message;

/// <summary>The worker running the job disappeared; the shim decides how to resume.</summary>
public sealed record WorkerLost(string JobId, string Reason) : Message;

// ---- Emby conversions (shim <-> coordinator) -------------------------------------------------------

/// <summary>
/// All video chunks are encoded: the shim assembles Emby's output from this concat list. With
/// <paramref name="AudioFile"/>, the audio was converted ahead on the server (see <see cref="AudioAheadPlan"/>) and
/// <paramref name="AudioOffset"/> is the -itsoffset that puts it back in place; without, the usual assembly.
/// </summary>
public sealed record ConversionReady(string TaskId, string ListFile, string? AudioFile = null, string? AudioOffset = null) : Message;

/// <summary>The remote encode gave up: the shim runs Emby's original command on the server.</summary>
public sealed record ConversionFailed(string TaskId, string Reason) : Message;

/// <summary>
/// The shim reports the outcome of the assembly so the coordinator can clean up. <paramref name="Check"/>: the check
/// line (source and result video spans), when the check got that far; used by the failure reports.
/// </summary>
public sealed record ConversionDone(string TaskId, bool Success, string? Error, string? Check = null) : Message;

public static class MessageJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(Message message) => JsonSerializer.Serialize(message, Options);

    public static Message? Deserialize(string json) => JsonSerializer.Deserialize<Message>(json, Options);
}
