using System.Text.RegularExpressions;

namespace RemoteTranscode.Common;

/// <summary>
/// Worker-side defence: a job template is accepted only if every option and filter is on an allow-list and every
/// file reference is a placeholder. A compromised or buggy coordinator therefore cannot make the worker read or
/// write arbitrary files or run filters that touch the file system or network.
/// </summary>
public static partial class ArgTemplateValidator
{
    // Option names as Emby generates them (stream specifiers such as ":v:0" are stripped before lookup).
    private static readonly HashSet<string> AllowedOptions = new(StringComparer.Ordinal)
    {
        "-y", "-copyts", "-start_at_zero", "-f", "-ss", "-t", "-to", "-c", "-codec", "-vcodec", "-acodec", "-scodec",
        "-noautorotate", "-autorotate", "-i", "-filter_complex", "-filter", "-vf", "-af", "-map", "-sn", "-an", "-vn", "-dn",
        "-g", "-maxrate", "-minrate", "-bufsize", "-b", "-ab", "-sc_threshold", "-keyint_min", "-r", "-pix_fmt",
        "-preset", "-profile", "-level", "-tune", "-x264opts", "-x264-params", "-x265-params", "-crf", "-qp",
        "-bf", "-refs", "-ac", "-ar", "-sample_fmt", "-channel_layout", "-metadata", "-disposition",
        "-max_delay", "-avoid_negative_ts", "-map_metadata", "-map_chapters", "-threads", "-filter_threads",
        "-filter_complex_threads", "-fflags", "-analyzeduration", "-probesize", "-max_muxing_queue_size",
        "-vsync", "-fps_mode", "-async", "-aspect", "-color_primaries", "-color_trc", "-colorspace", "-color_range",
        "-flags", "-strict", "-tag", "-bsf", "-max_interleave_delta", "-muxdelay", "-muxpreload", "-seek2any",
        "-accurate_seek", "-noaccurate_seek", "-err_detect", "-ignore_unknown", "-copypriorss", "-fix_sub_duration",
        "-canvas_size", "-itsoffset", "-re", "-readrate", "-segment_format", "-segment_list", "-segment_list_type",
        "-segment_time", "-segment_start_number", "-individual_header_trailer", "-write_header_trailer",
        "-segment_format_options", "-break_non_keyframes", "-reset_timestamps", "-frame_drop_threshold",
        "-movflags", "-frag_duration", "-min_frag_duration", "-start_number", "-mpegts_m2ts_mode", "-mpegts_copyts",
        "-hwaccel", "-hwaccel_output_format", "-hwaccel_device", "-init_hw_device", "-filter_hw_device",
        "-extra_hw_frames", "-global_quality", "-cq", "-rc", "-qvbr_quality_level", "-quality", "-sub_charenc", "-forced_subs_only", "-nostdin", "-nostats", "-stats", "-shortest",
    };

    private static readonly HashSet<string> AllowedFilters = new(StringComparer.Ordinal)
    {
        "scale", "subtitles", "format", "setparams", "zscale", "tonemap", "tonemapx", "fps", "yadif", "bwdif",
        "supertonemap", "pad", "crop", "transpose", "hflip", "vflip", "setsar", "setdar", "overlay", "hwupload", "hwdownload",
        "null", "anull", "volume", "aresample", "pan", "aformat", "dynaudnorm", "alimiter", "atempo", "asetpts",
        "setpts", "trim", "atrim", "split", "asplit", "copy", "tpad", "apad", "deinterlace_vaapi", "scale_cuda",
        "scale_qsv", "vpp_qsv", "tonemap_opencl", "hwmap", "colorspace", "loudnorm", "aformat", "amix", "afade",
        "fade", "rotate", "settb", "asettb", "showinfo", "unsharp", "framerate", "minterpolate",
    };

    // Values that would point at a file or URL; they are only legal as placeholders.
    [GeneratedRegex(@"^(/|[A-Za-z]:[\\/]|\\\\)|://|^file:|^pipe:|^tcp:|^udp:|^concat:|^subfile:|^data:")]
    private static partial Regex PathLike();

    [GeneratedRegex(@"^\{(input:\d+|aux:\d+|fonts|out|list)\}$")]
    private static partial Regex Placeholder();

    // A filter instance inside a graph: optional [labels], name, optional @instance, optional =args.
    // ffmpeg trims whitespace around the name, so "movie =..." is the movie filter.
    [GeneratedRegex(@"(?:^|[,;])\s*(?:\[[^\]]*\]\s*)*(?<name>[A-Za-z0-9_]+)(?:@[A-Za-z0-9_]+)?(?=\s*(?:[=,;\[]|$))")]
    private static partial Regex FilterName();

    /// <summary>Names of the filters used in a filter graph (e.g. "scale", "subtitles").</summary>
    public static IEnumerable<string> FilterNames(string graph) =>
        FilterName().Matches(graph).Select(m => m.Groups["name"].Value).Distinct();

    /// <summary>
    /// Accepts "-option [value]" pairs ending with {out}; no positional argument, since ffmpeg would take it as an
    /// extra output file. Whether an option takes a value is decided by <see cref="IsFlag"/>, which must match ffmpeg.
    /// </summary>
    public static bool Validate(IReadOnlyList<string> template, out string? error)
    {
        for (int i = 0; i < template.Count; i++)
        {
            string a = template[i];
            if (i == template.Count - 1)
            {
                error = a == "{out}" ? null : "last argument must be {out}";
                return error is null;
            }
            if (!a.StartsWith('-'))
            {
                error = $"unexpected positional argument '{a}'";
                return false;
            }
            string name = a.Split(':')[0];
            if (!AllowedOptions.Contains(name))
            {
                error = $"option '{name}' is not allowed";
                return false;
            }
            if (IsFlag(name))
                continue;
            if (++i >= template.Count - 1)
            {
                error = $"option '{name}' has no value";
                return false;
            }
            string value = template[i];
            if (name is "-i" or "-segment_list")
            {
                if (!Placeholder().IsMatch(value))
                {
                    error = $"{name} must use a placeholder";
                    return false;
                }
                continue;
            }
            if (name is "-filter_complex" or "-filter" or "-vf" or "-af")
            {
                if (!ValidateFilterGraph(value, out error))
                    return false;
                continue;
            }
            if (PathLike().IsMatch(value) && !Placeholder().IsMatch(value))
            {
                error = $"value of {name} looks like a path or URL";
                return false;
            }
            if (KeyValueOptions.Contains(name) && !ValidateKeyValueOption(name, value, out error))
                return false;
        }
        error = "empty template";
        return false;
    }

    // Options whose value is a "key=value:key=value" list handed to an encoder or muxer. Some of their keys write
    // files anywhere (x264 stats=, x265 csv=/analysis-save=, hls_segment_filename=...), so each pair is checked.
    private static readonly HashSet<string> KeyValueOptions = new(StringComparer.Ordinal)
    {
        "-x264opts", "-x264-params", "-x265-params", "-segment_format_options",
    };

    // Keys of those lists that name a file (read or written) in x264, x265 and the muxers.
    private static readonly HashSet<string> FileKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "stats", "csv", "analysis-save", "analysis-load", "analysis-reuse-file", "dump-yuv", "qpfile", "cqmfile",
        "recon", "zonefile", "lambda-file", "scaling-list", "dolby-vision-rpu", "tcfile-in", "tcfile-out",
        "passlogfile", "segment_list", "hls_segment_filename", "hls_key_info_file", "key_info_file",
        "hls_fmp4_init_filename", "master_pl_name", "init_seg_name", "media_seg_name", "hls_base_url",
    };

    // A ratio such as "sar=1/1" or "fps=24000/1001": the only '/' an encoder parameter legitimately contains.
    [GeneratedRegex(@"^\d+/\d+$")]
    private static partial Regex Ratio();

    /// <summary>
    /// Checks a "key=value" list the way ffmpeg splits it (pairs separated by ':'; ',' also separates sub-values such
    /// as partitions=i4x4,p8x8). File keys are refused outright, and so is anything that could be a path: a drive
    /// letter, '\', '..', or a '/' outside a numeric ratio.
    /// </summary>
    private static bool ValidateKeyValueOption(string option, string value, out string? error)
    {
        error = $"value of {option} references a file";
        if (value.Contains('\\') || value.Contains("..", StringComparison.Ordinal) || DriveLetter().IsMatch(value))
            return false;
        foreach (string pair in value.Split(':'))
        {
            string[] kv = pair.Split('=', 2);
            string key = kv[0].Trim();
            if (FileKeys.Contains(key) || key.Contains("file", StringComparison.OrdinalIgnoreCase)
                || key.Contains("path", StringComparison.OrdinalIgnoreCase) || key.Contains("url", StringComparison.OrdinalIgnoreCase))
                return false;
            foreach (string part in (kv.Length == 2 ? kv[1] : kv[0]).Split(','))
                if (part.Contains('/') && !Ratio().IsMatch(part.Trim()))
                    return false;
        }
        error = null;
        return true;
    }

    [GeneratedRegex(@"(^|[^A-Za-z0-9])[A-Za-z]:[\\/]")]
    private static partial Regex DriveLetter();

    // Filters that read a file given as their first positional argument (or f=/filename=).
    private static readonly HashSet<string> FileFilters = new(StringComparer.Ordinal) { "subtitles", "ass", "movie", "amovie" };

    // Filter option keys naming a file, whatever the filter.
    private static readonly HashSet<string> FilterFileKeys = new(StringComparer.Ordinal)
    {
        "filename", "fontsdir", "file", "textfile", "fontfile", "stats_file", "model",
    };

    /// <summary>
    /// Parses the graph the way ffmpeg does (chains split by ';', filters by ',', [labels], name '=' arguments, then
    /// arguments split by ':' with quotes and '\' escapes at both levels, whitespace trimmed), then requires every
    /// filter name to be allowed and every file argument to be exactly a job placeholder.
    /// </summary>
    private static bool ValidateFilterGraph(string graph, out string? error)
    {
        if (graph.Contains("textfile", StringComparison.Ordinal) || graph.Contains("://", StringComparison.Ordinal))
        {
            error = "filter graph references external resources";
            return false;
        }
        // Placeholders contain ':' ({aux:1}), which ffmpeg would split on; the worker substitutes them before ffmpeg
        // sees the graph, so they are swapped for inert tokens while parsing.
        var placeholders = new List<string>();
        string masked = PlaceholderInText().Replace(graph, m =>
        {
            placeholders.Add(m.Value);
            return $"{placeholders.Count - 1}";
        });
        if (!FilterGraphParser.TryParse(masked, out var filters, out error))
            return false;
        foreach (var (name, options) in filters)
        {
            if (!AllowedFilters.Contains(name))
            {
                error = $"filter '{name}' is not allowed";
                return false;
            }
            bool fileFilter = FileFilters.Contains(name);
            foreach (var (key, value) in options)
            {
                bool isPlaceholder = MaskedPlaceholder().IsMatch(value);
                bool isFile = key is null ? fileFilter : FilterFileKeys.Contains(key) || (fileFilter && key == "f");
                if (isFile && !isPlaceholder)
                {
                    error = $"filter {name} {key ?? "file argument"} must use a placeholder";
                    return false;
                }
                if (!isPlaceholder && (PathLike().IsMatch(value.Trim()) || value.Contains('')))
                {
                    error = $"filter {name} argument looks like a path or URL";
                    return false;
                }
            }
        }
        error = null;
        return true;
    }

    [GeneratedRegex(@"\{(?:input:\d+|aux:\d+|fonts|out|list)\}")]
    private static partial Regex PlaceholderInText();

    [GeneratedRegex("^\\d+$")]
    private static partial Regex MaskedPlaceholder();

    // Options without a value. A flag missing here would make the validator skip the next argument as its value.
    private static bool IsFlag(string name) => name is "-y" or "-copyts" or "-start_at_zero" or "-noautorotate" or "-autorotate"
        or "-sn" or "-an" or "-vn" or "-dn" or "-re" or "-accurate_seek" or "-noaccurate_seek"
        or "-ignore_unknown" or "-nostdin" or "-nostats" or "-stats" or "-shortest" or "-fix_sub_duration";
}

/// <summary>
/// Filter graph tokenizer following libavfilter: the graph level (labels, filter name, arguments up to "[],;") and the
/// filter level (arguments split by ':', "key=value" or positional), each with av_get_token's quoting rules.
/// </summary>
internal static partial class FilterGraphParser
{
    public static bool TryParse(string graph, out List<(string Name, List<(string? Key, string Value)> Options)> filters, out string? error)
    {
        filters = [];
        int i = 0;
        while (true)
        {
            SkipWhitespace(graph, ref i);
            if (!SkipLabels(graph, ref i, out error))
                return false;
            int start = i;
            while (i < graph.Length && graph[i] is not ('=' or ',' or ';' or '['))
                i++;
            string name = graph[start..i].Trim();
            if (!FilterNameSyntax().IsMatch(name))
            {
                error = $"invalid filter name '{name}'";
                return false;
            }
            string arguments = "";
            if (i < graph.Length && graph[i] == '=')
            {
                i++;
                arguments = GetToken(graph, ref i, "[],;");
            }
            filters.Add((name.Split('@')[0], ParseOptions(arguments)));
            SkipWhitespace(graph, ref i);
            if (!SkipLabels(graph, ref i, out error))
                return false;
            SkipWhitespace(graph, ref i);
            if (i >= graph.Length)
                return true;
            if (graph[i] is not (',' or ';'))
            {
                error = $"unexpected '{graph[i]}' in filter graph";
                return false;
            }
            i++;
        }
    }

    // Filter arguments: "key=value" pairs or positional values, separated by ':' (libavfilter process_options).
    private static List<(string? Key, string Value)> ParseOptions(string arguments)
    {
        var options = new List<(string?, string)>();
        int i = 0;
        while (i < arguments.Length)
        {
            int k = i;
            while (k < arguments.Length && (char.IsAsciiLetterOrDigit(arguments[k]) || arguments[k] is '-' or '_' or '/' or '.'))
                k++;
            string? key = null;
            if (k > i && k < arguments.Length && arguments[k] == '=')
            {
                key = arguments[i..k];
                i = k + 1;
            }
            options.Add((key, GetToken(arguments, ref i, ":")));
            if (i < arguments.Length && arguments[i] == ':')
                i++;
        }
        return options;
    }

    /// <summary>
    /// av_get_token: leading whitespace skipped, stops at a terminator outside quotes, '\' escapes the next character,
    /// '...' is literal; trailing whitespace is trimmed unless it was escaped or quoted.
    /// </summary>
    private static string GetToken(string s, ref int i, string terminators)
    {
        SkipWhitespace(s, ref i);
        var sb = new System.Text.StringBuilder();
        int protectedLength = 0;
        while (i < s.Length && terminators.IndexOf(s[i]) < 0)
        {
            char c = s[i++];
            if (c == '\\' && i < s.Length)
            {
                sb.Append(s[i++]);
                protectedLength = sb.Length;
            }
            else if (c == '\'')
            {
                while (i < s.Length && s[i] != '\'')
                    sb.Append(s[i++]);
                if (i < s.Length)
                    i++;
                protectedLength = sb.Length;
            }
            else
            {
                sb.Append(c);
            }
        }
        int end = sb.Length;
        while (end > protectedLength && sb[end - 1] is ' ' or '\t' or '\r' or '\n')
            end--;
        return sb.ToString(0, end);
    }

    private static void SkipWhitespace(string s, ref int i)
    {
        while (i < s.Length && s[i] is ' ' or '\t' or '\r' or '\n')
            i++;
    }

    private static bool SkipLabels(string s, ref int i, out string? error)
    {
        while (i < s.Length && s[i] == '[')
        {
            int close = s.IndexOf(']', i);
            if (close < 0)
            {
                error = "unterminated label in filter graph";
                return false;
            }
            i = close + 1;
            SkipWhitespace(s, ref i);
        }
        error = null;
        return true;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]+(@[A-Za-z0-9_]+)?$")]
    private static partial Regex FilterNameSyntax();
}
