namespace RemoteTranscode.Common;

/// <summary>
/// Video decoding on the GPU that runs the hardware encoder. Decoded pictures are copied back to system memory (no
/// -hwaccel_output_format), so Emby's software filter graphs (scale, zscale, tonemap, subtitles...) stay valid as they
/// are. Decoding is bit-exact: the pictures, hence the encoded segments, are the same as with software decoding.
/// </summary>
/// <remarks>
/// Only "hwaccel" methods that plug into ffmpeg's native decoders are used: when the GPU cannot decode a stream (4:4:4,
/// H.264 10-bit...) the native h264/hevc/vp9/mpeg2 decoders silently go on in software. "-hwaccel qsv" and
/// "-hwaccel amf" are avoided on purpose: they switch to separate decoders (h264_qsv, hevc_amf...) that fail on such
/// streams instead of falling back. Intel and AMD therefore decode through D3D11VA, on the adapter of that vendor.
/// </remarks>
public static class HardwareDecoding
{
    // Encoder family (suffix of the hardware encoder) → input options selecting the decode method, and the hardware
    // pixel format the probe keeps frames in to prove they really were decoded on the GPU.
    private static readonly Dictionary<string, (string[] Options, string HardwareFormat)> Methods = new(StringComparer.Ordinal)
    {
        // NVDEC through CUDA, on CUDA device 0 like NVENC's default.
        ["nvenc"] = (["-hwaccel", "cuda"], "cuda"),
        // A named D3D11VA device on the first adapter of the vendor (an Intel iGPU next to an AMD/NVIDIA card).
        ["qsv"] = (["-init_hw_device", "d3d11va=hwdec:,vendor_id=0x8086", "-hwaccel", "d3d11va", "-hwaccel_device", "hwdec"], "d3d11"),
        ["amf"] = (["-init_hw_device", "d3d11va=hwdec:,vendor_id=0x1002", "-hwaccel", "d3d11va", "-hwaccel_device", "hwdec"], "d3d11"),
    };

    /// <summary>
    /// Native decoder names (as Emby writes them before its input: "-c:v:0 hevc") that may be decoded on the GPU, each
    /// with the software encoder the capability probe uses to make its test clip.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Codecs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["h264"] = "libx264",
        ["hevc"] = "libx265",
        // ffmpeg's native av1 decoder has no software path: only used when Emby already asks for it and the probe
        // proved this GPU decodes AV1.
        ["av1"] = "libaom-av1",
        ["vp9"] = "libvpx-vp9",
        ["mpeg2video"] = "mpeg2video",
    };

    // Options that mean the template already chose a decode method: it is then left untouched.
    private static readonly HashSet<string> HardwareOptions = new(StringComparer.Ordinal)
    {
        "-hwaccel", "-hwaccel_device", "-hwaccel_output_format", "-init_hw_device", "-filter_hw_device",
    };

    public static IEnumerable<string> Families => Methods.Keys;

    /// <summary>"nvenc" for h264_nvenc, "qsv" for hevc_qsv...; null for a software encoder.</summary>
    public static string? FamilyOf(string encoder)
    {
        int underscore = encoder.LastIndexOf('_');
        string family = underscore < 0 ? "" : encoder[(underscore + 1)..];
        return Methods.ContainsKey(family) ? family : null;
    }

    /// <summary>Entry of <see cref="WorkerCapabilities.HwDecoders"/>: "nvenc/hevc" = HEVC decoded with the NVENC GPU's method.</summary>
    public static string Key(string family, string codec) => $"{family}/{codec}";

    /// <summary>Input options of the capability probe: the decode method, frames kept on the GPU (see <see cref="ProbeFilter"/>).</summary>
    public static string[] ProbeOptions(string family) =>
        [.. Methods[family].Options, "-hwaccel_output_format", Methods[family].HardwareFormat];

    /// <summary>
    /// hwdownload accepts only GPU frames: the probe fails if ffmpeg silently fell back to software decoding.
    /// </summary>
    public const string ProbeFilter = "hwdownload,format=nv12";

    /// <summary>
    /// Options to put before the video input's "-i", or none: the encoder must be a hardware one, the decoder Emby
    /// names for that input must have been validated on this worker for that encoder's GPU, and the template must not
    /// pick a decode method itself.
    /// </summary>
    public static IReadOnlyList<string> InputOptions(string encoder, string? decoder, IReadOnlyCollection<string> validated)
    {
        if (decoder is null || FamilyOf(encoder) is not { } family || !validated.Contains(Key(family, decoder)))
            return [];
        return Methods[family].Options;
    }

    /// <summary>
    /// The input whose video is decoded on the GPU: the first one (index among the template's inputs) for which Emby
    /// names a video decoder in its input options, with that decoder. (-1, null) when there is none or when the template
    /// already uses a hardware option, so subtitle or attachment inputs never get a decode method.
    /// </summary>
    public static (int Input, string? Decoder) VideoInput(IReadOnlyList<string> template)
    {
        if (template.Any(a => HardwareOptions.Contains(a.Split(':')[0])))
            return (-1, null);
        int input = 0;
        string? decoder = null;
        for (int i = 0; i < template.Count; i++)
        {
            string a = template[i];
            if (a == "-i")
            {
                if (decoder != null)
                    return (input, decoder);
                input++;
                i++;   // the input itself
                continue;
            }
            if (IsVideoCodecOption(a) && i + 1 < template.Count)
                decoder = template[++i];
        }
        return (-1, null);
    }

    private static bool IsVideoCodecOption(string a) =>
        a is "-c:v" or "-vcodec" || a.StartsWith("-c:v:", StringComparison.Ordinal) || a.StartsWith("-codec:v", StringComparison.Ordinal);
}
