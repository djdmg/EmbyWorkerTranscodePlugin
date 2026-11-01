using System.Globalization;
using System.Text.RegularExpressions;

namespace RemoteTranscode.Common;

public sealed class WorkerCommandContext
{
    public required string CoordinatorBaseUrl { get; init; }
    public required string JobId { get; init; }
    public required string JobToken { get; init; }
    /// <summary>Pinned coordinator certificate for ffmpeg; null = system trust store (public certificate).</summary>
    public required string? CaFile { get; init; }
    public required IReadOnlyList<JobFile> Files { get; init; }
    public required double SegmentSeconds { get; init; }

    /// <summary>Validated video encoders available on this worker, used to replace Emby's software encoder.</summary>
    public required IReadOnlyCollection<string> VideoEncoders { get; init; }

    /// <summary>
    /// False (default): run exactly the encoder and settings Emby chose, like Emby's ffmpeg on the server. A stream that
    /// moves between a worker and the server mid-way then comes from the same encoder on both sides, so players see no
    /// change at the junction. True swaps Emby's software encoder for a validated hardware one.
    /// </summary>
    public bool PreferHardwareEncoding { get; init; }

    /// <summary>
    /// Background encodes: hardware encoders use constant quality (from the template's -crf) instead of a bitrate
    /// target. Playback keeps the bitrate target, because the client's bandwidth is what matters there.
    /// </summary>
    public bool QualityMode { get; init; }

    /// <summary>
    /// Inputs read through the worker's local cache (conversions): file id → local URL. Those inputs need neither the
    /// job token nor TLS options (the cache adds them upstream).
    /// </summary>
    public IReadOnlyDictionary<int, string>? LocalInputs { get; init; }

    /// <summary>
    /// Hardware decoders validated on this worker (<see cref="WorkerCapabilities.HwDecoders"/>, "nvenc/hevc"...); empty
    /// when GPU decoding is switched off. Used only when the chosen encoder is a hardware one (see <see cref="HardwareDecoding"/>).
    /// </summary>
    public IReadOnlyCollection<string> HardwareDecoders { get; init; } = [];

    /// <summary>
    /// GPU tone mapping validated on this worker (<see cref="WorkerCapabilities.GpuToneMapping"/>); null when there is
    /// none, when it is switched off or suspended after failures. With it, Emby's supertonemap left in a template
    /// (playback) is translated with libplacebo; and any graph using libplacebo (conversions, translated by the
    /// coordinator) gets its Vulkan device.
    /// </summary>
    public GpuToneMapping? GpuToneMapping { get; init; }
}

/// <param name="HardwareDecoder">Hardware decoder used for the video input ("nvenc/hevc"), null for software decoding.</param>
/// <param name="GpuToneMapped">The filter graph tone maps on the GPU (libplacebo).</param>
/// <param name="ToneMappedHere">This worker chose libplacebo itself (playback); false when the coordinator did (conversion).</param>
public sealed record WorkerCommand(string[] Args, string VideoEncoder, string SegmentListFile, string SegmentPattern, string? HardwareDecoder = null,
    bool GpuToneMapped = false, bool ToneMappedHere = false);

/// <summary>
/// Builds the final ffmpeg command on the worker from a validated job template: placeholders become HTTPS
/// inputs and job-local relative paths, and Emby's software encoder is swapped for a validated hardware one.
/// </summary>
public static partial class WorkerCommandBuilder
{
    public const string SegmentListFile = "list.csv";
    public const string SegmentPattern = "seg_%d.ts";

    // Hardware encoders in order of preference for each software encoder Emby may request.
    private static readonly Dictionary<string, string[]> HardwareEquivalents = new(StringComparer.Ordinal)
    {
        ["libx264"] = ["h264_nvenc", "h264_qsv", "h264_amf"],
        ["libx265"] = ["hevc_nvenc", "hevc_qsv", "hevc_amf"],
    };

    // Software encoder tuning options with no meaning for hardware encoders.
    private static readonly HashSet<string> SoftwareOnlyOptions = new(StringComparer.Ordinal)
    {
        "-x264opts", "-x264-params", "-x265-params", "-crf", "-preset", "-tune",
        // Emby asks x265 for main10 even for 8-bit pictures (x265 converts); a hardware encoder refuses a profile that
        // does not match its input, so it picks the profile itself.
        "-profile",
    };

    [GeneratedRegex(@"\{(?<kind>input|aux):(?<id>\d+)\}|\{fonts\}")]
    private static partial Regex PlaceholderInText();

    public static IReadOnlyList<string> HardwareEquivalentsOf(string softwareEncoder) =>
        HardwareEquivalents.TryGetValue(softwareEncoder, out var hw) ? hw : [];

    /// <summary>
    /// Pure function of the template and context (no I/O). The template has already passed
    /// <see cref="ArgTemplateValidator"/>, so placeholders and option values are assumed present.
    /// </summary>
    public static WorkerCommand Build(IReadOnlyList<string> template, WorkerCommandContext ctx)
    {
        // "level+info" prefixes each stderr line with [error]/[fatal]... (JobRunner surfaces those); progress goes to
        // stdout as key=value blocks once a second.
        var args = new List<string> { "-hide_banner", "-loglevel", "level+info", "-nostats", "-progress", "pipe:1", "-stats_period", "1" };
        int globalOptionsAt = args.Count;
        string encoder = ChooseEncoder(template, ctx);
        // GPU decoding goes with GPU encoding only, and only on the video input (never a subtitle or attachment input).
        var (videoInput, decoder) = HardwareDecoding.VideoInput(template);
        var hwDecode = HardwareDecoding.InputOptions(encoder, decoder, ctx.HardwareDecoders);
        string? hardwareDecoder = hwDecode.Count > 0 ? HardwareDecoding.Key(HardwareDecoding.FamilyOf(encoder)!, decoder!) : null;
        int inputCount = 0;
        string? videoCodecOption = null;
        int videoCodecAt = -1;
        string? maxrate = null;
        string? bufsize = null;
        string? crf = null;
        // Playback templates still carry Emby's supertonemap: translated here, on the GPU when this worker validated it.
        var toneMapper = ctx.GpuToneMapping?.IsLibplacebo == true ? ToneMapper.Libplacebo : ToneMapper.Zscale;
        bool gpuToneMapped = false, toneMappedHere = false;

        for (int i = 0; i < template.Count; i++)
        {
            string a = template[i];
            if (a == "{out}")
            {
                args.Add(SegmentPattern);
                continue;
            }
            string name = a.Split(':')[0];
            string? value = i + 1 < template.Count ? template[i + 1] : null;

            if (a == "-i")
            {
                int id = PlaceholderId(value!, "input");
                // Input options are not order-sensitive: next to "-i", the template's -ss/-t/-copyts/-noautorotate keep
                // their meaning (frame-exact slice boundaries included, decoding being bit-exact).
                if (inputCount++ == videoInput)
                    args.AddRange(hwDecode);
                if (ctx.LocalInputs?.TryGetValue(id, out string? local) == true)
                {
                    args.AddRange(ReconnectOptions);
                    args.Add("-i");
                    args.Add(local);
                }
                else
                {
                    args.AddRange(InputOptions(ctx));
                    args.Add("-i");
                    args.Add($"{ctx.CoordinatorBaseUrl.TrimEnd('/')}/jobs/{ctx.JobId}/files/{id}");
                }
                i++;
                continue;
            }
            if (a == "-segment_list")
            {
                args.Add(a);
                args.Add(SegmentListFile);
                i++;
                continue;
            }
            if (a == "-segment_list_type")
            {
                // CSV lists are appended segment by segment with start/end times, which is what the uploader needs.
                args.Add(a);
                args.Add("csv");
                i++;
                continue;
            }
            if (name is "-filter_complex" or "-filter" or "-vf" or "-af")
            {
                args.Add(a);
                string graph = PlaceholderInText().Replace(value!, m => LocalReference(m, ctx));
                string translated = EmbyFilters.Translate(graph, toneMapper);
                if (EmbyFilters.UsesLibplacebo(translated))
                {
                    gpuToneMapped = true;
                    toneMappedHere |= translated != graph;
                }
                args.Add(translated);
                i++;
                continue;
            }
            if (IsVideoCodecOption(a) && value is "libx264" or "libx265")
            {
                videoCodecOption = a;
                videoCodecAt = args.Count;
                args.Add(a);
                args.Add(encoder);
                i++;
                continue;
            }
            // Emby scopes its encoder options (-crf:v:0, -preset:v:0...): unscoped ones are left untouched.
            if (name == "-crf" && IsVideoScoped(a))
                crf = value;
            if (encoder != "libx264" && encoder != "libx265" && SoftwareOnlyOptions.Contains(name) && IsVideoScoped(a))
            {
                i++;
                continue;
            }
            if (name == "-maxrate" && IsVideoScoped(a))
                maxrate = value;
            if (name == "-bufsize" && IsVideoScoped(a))
                bufsize = value;
            args.Add(a);
        }

        if (videoCodecOption != null && encoder != "libx264" && encoder != "libx265")
        {
            string? quality = ctx.QualityMode ? crf : null;
            args.InsertRange(videoCodecAt + 2, HardwareRateControl(encoder, videoCodecOption, maxrate, quality, ctx.SegmentSeconds));
        }

        // libplacebo runs on the Vulkan device the capability probe validated (with the options some drivers need), named
        // for the filters; global options, so before the first input.
        if (gpuToneMapped)
        {
            if (ctx.GpuToneMapping?.IsLibplacebo != true)
                throw new InvalidOperationException("graph tone mapped with libplacebo but GPU tone mapping is unavailable on this worker");
            args.InsertRange(globalOptionsAt, ["-init_hw_device", ctx.GpuToneMapping.Device, "-filter_hw_device", EmbyFilters.VulkanDevice]);
        }

        return new WorkerCommand(args.ToArray(), encoder, SegmentListFile, SegmentPattern, hardwareDecoder, gpuToneMapped, toneMappedHere);
    }

    /// <summary>
    /// The first video codec option after the last input is the output encoder (codec options before an input select
    /// decoders). It is kept as is unless hardware encoding is preferred and a validated equivalent exists.
    /// </summary>
    private static string ChooseEncoder(IReadOnlyList<string> template, WorkerCommandContext ctx)
    {
        for (int i = EmbyJobPlanner.LastInputIndex(template) + 1; i < template.Count - 1; i++)
        {
            if (!IsVideoCodecOption(template[i]))
                continue;
            string requested = template[i + 1];
            if (ctx.PreferHardwareEncoding && HardwareEquivalents.TryGetValue(requested, out var candidates))
            {
                string? hw = candidates.FirstOrDefault(ctx.VideoEncoders.Contains);
                if (hw != null)
                    return hw;
            }
            return requested;
        }
        return "copy";
    }

    private static IEnumerable<string> HardwareRateControl(string encoder, string codecOption, string? maxrate, string? quality, double segmentSeconds)
    {
        string spec = codecOption.Length > 2 ? codecOption[2..] : "";   // ":v:0" from "-c:v:0"
        string seg = segmentSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        // Keyframes on every segment boundary so each HLS segment starts with an IDR frame.
        yield return "-force_key_frames" + spec;
        yield return $"expr:gte(t,n_forced*{seg})";

        if (quality != null)
        {
            // Constant quality, still capped by the template's -maxrate/-bufsize when the encoder honours them.
            if (encoder.EndsWith("_nvenc", StringComparison.Ordinal))
            {
                yield return "-preset" + spec; yield return "p5";
                yield return "-tune" + spec; yield return "hq";
                yield return "-rc" + spec; yield return "vbr";
                yield return "-cq" + spec; yield return quality;
                yield return "-b" + spec; yield return "0";
                yield return "-forced-idr" + spec; yield return "1";
            }
            else if (encoder.EndsWith("_qsv", StringComparison.Ordinal))
            {
                yield return "-preset" + spec; yield return "medium";
                yield return "-global_quality" + spec; yield return quality;
                // With the template's -maxrate, a quality alone falls back to constant QP (no quality, no cap); with an
                // average bitrate too, QSV uses QVBR: constant quality capped like -crf with -maxrate. Without a cap: ICQ.
                if (long.TryParse(maxrate, NumberStyles.Integer, CultureInfo.InvariantCulture, out long cap))
                {
                    yield return "-b" + spec; yield return (cap * 8 / 10).ToString(CultureInfo.InvariantCulture);
                }
                yield return "-forced_idr" + spec; yield return "1";
            }
            else if (encoder.EndsWith("_amf", StringComparison.Ordinal))
            {
                yield return "-quality" + spec; yield return "balanced";
                yield return "-rc" + spec; yield return "qvbr";
                yield return "-qvbr_quality_level" + spec; yield return quality;
            }
            yield break;
        }

        // Average target at 80% of the cap leaves the VBR encoder room for peaks below Emby's -maxrate.
        if (long.TryParse(maxrate, NumberStyles.Integer, CultureInfo.InvariantCulture, out long max))
        {
            yield return "-b" + spec;
            yield return (max * 8 / 10).ToString(CultureInfo.InvariantCulture);
        }

        if (encoder.EndsWith("_nvenc", StringComparison.Ordinal))
        {
            yield return "-preset" + spec; yield return "p4";
            yield return "-tune" + spec; yield return "ll";
            yield return "-rc" + spec; yield return "vbr";
            yield return "-forced-idr" + spec; yield return "1";
        }
        else if (encoder.EndsWith("_qsv", StringComparison.Ordinal))
        {
            yield return "-preset" + spec; yield return "veryfast";
            // h264_qsv only (hevc_qsv has no such option and warns about it).
            if (encoder.StartsWith("h264", StringComparison.Ordinal))
            {
                yield return "-look_ahead" + spec; yield return "0";
            }
            yield return "-forced_idr" + spec; yield return "1";
        }
        else if (encoder.EndsWith("_amf", StringComparison.Ordinal))
        {
            yield return "-quality" + spec; yield return "speed";
            yield return "-rc" + spec; yield return "vbr_peak";
        }
    }

    /// <summary>
    /// Options placed before each HTTPS input: the job token (accepted only for this job's files), TLS verification
    /// against the pinned certificate, and ffmpeg's own reconnection on network errors during range reads.
    /// </summary>
    private static IEnumerable<string> InputOptions(WorkerCommandContext ctx) =>
    [
        "-headers", $"Authorization: Bearer {ctx.JobToken}\r\n",
        .. (ctx.CaFile is null ? Array.Empty<string>() : ["-ca_file", ctx.CaFile]),
        "-tls_verify", "1",
        .. ReconnectOptions,
    ];

    private static readonly string[] ReconnectOptions = ["-reconnect", "1", "-reconnect_on_network_error", "1", "-reconnect_delay_max", "5"];

    private static string LocalReference(Match m, WorkerCommandContext ctx)
    {
        if (m.Value == "{fonts}")
            return "fonts";
        int id = int.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture);
        JobFile file = ctx.Files.First(f => f.Id == id);
        return FilterEscaping.Escape(LocalAuxPath(file));
    }

    /// <summary>Relative path (from the job directory) where an aux file is stored on the worker.</summary>
    public static string LocalAuxPath(JobFile file) => $"aux/{file.Id}{SafeExtension(file.Name)}";

    /// <summary>The server-side name is untrusted: keep only a short alphanumeric extension (filters sniff by extension).</summary>
    private static string SafeExtension(string name)
    {
        string ext = Path.GetExtension(name);
        return Regex.IsMatch(ext, @"^\.[A-Za-z0-9]{1,8}$") ? ext.ToLowerInvariant() : ".bin";
    }

    private static int PlaceholderId(string value, string kind)
    {
        var m = PlaceholderInText().Match(value);
        if (!m.Success || m.Groups["kind"].Value != kind)
            throw new InvalidOperationException($"expected {{{kind}:N}} placeholder");
        return int.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture);
    }

    private static bool IsVideoCodecOption(string a) => a is "-c:v" or "-vcodec" || a.StartsWith("-c:v:", StringComparison.Ordinal) || a.StartsWith("-codec:v", StringComparison.Ordinal);

    private static bool IsVideoScoped(string a) => a.Contains(":v", StringComparison.Ordinal);
}
