using System.Globalization;

namespace RemoteTranscode.Common;

/// <summary>A server file the worker will fetch; <see cref="Id"/> is the N of its {input:N}/{aux:N} placeholder.</summary>
public sealed record PlannedFile(int Id, JobFileKind Kind, string ServerPath, string Name, long Size);

/// <summary>
/// Result of <see cref="EmbyJobPlanner.Plan"/>. When <see cref="Remote"/> is false only <see cref="Reason"/> is
/// meaningful and Emby's ffmpeg runs the command.
/// </summary>
public sealed record JobPlan(
    bool Remote,
    string Reason,
    string[] Template,
    IReadOnlyList<PlannedFile> Files,
    double SegmentSeconds,
    int StartIndex,
    string OutputPattern,
    string SegmentListPath,
    JobRequirements? Requirements = null)
{
    /// <summary>
    /// Emby's per-playback output folder. A seek restarts ffmpeg in the same folder, so it identifies the playback
    /// session and keeps it on one worker.
    /// </summary>
    public string SessionKey => OutputPattern.LastIndexOf('/') is int slash and > 0 ? OutputPattern[..slash] : OutputPattern;

    /// <summary>Conversions: the input range the worker should fetch ahead for its next slice (see <see cref="JobOffer.Prefetch"/>).</summary>
    public ByteRange[]? Prefetch { get; init; }

    /// <summary>Conversions: the slice this run encodes, shown in the worker's window (see <see cref="JobOffer.Slice"/>).</summary>
    public SliceInfo? Slice { get; init; }
}

public sealed class PlannerOptions
{
    /// <summary>Server directories ffmpeg inputs and filter files may come from.</summary>
    public required IReadOnlyList<string> AllowedRoots { get; init; }

    /// <summary>Emby's transcoding temp directory (plus test directories); outputs must be inside one of them.</summary>
    public required IReadOnlyList<string> OutputRoots { get; init; }

    /// <summary>Size of a readable server file, null when it does not exist or may not be served.</summary>
    public required Func<string, long?> GetFileSize { get; init; }

    /// <summary>Files of a fonts directory (subtitles filter "fontsdir").</summary>
    public required Func<string, IEnumerable<string>> ListDirectory { get; init; }
}

/// <summary>
/// Turns the ffmpeg command line Emby generated into a job template a worker can run: every file reference
/// becomes a placeholder, Emby-fork-only options are removed, and anything unexpected keeps the job local.
/// </summary>
public static class EmbyJobPlanner
{
    // Options only Emby's ffmpeg fork understands, or that only make sense on the server.
    private static readonly HashSet<string> DroppedWithValue = new(StringComparer.Ordinal) { "-loglevel", "-v", "-print_graphs_file", "-segment_write_temp" };

    private static readonly HashSet<string> FilterOptions = new(StringComparer.Ordinal) { "-filter_complex", "-vf", "-af", "-filter" };

    /// <summary>
    /// Plans an HLS playback transcode. The template keeps Emby's options in order; inputs become {input:N}, filter
    /// files {aux:N}/{fonts}, the segment list {list} and the last argument (segment pattern) {out}. The worker
    /// re-validates it with <see cref="ArgTemplateValidator"/>.
    /// </summary>
    public static JobPlan Plan(IReadOnlyList<string> args, PlannerOptions options)
    {
        var files = new List<PlannedFile>();
        var template = new List<string>(args.Count);

        int formatIndex = LastIndexOfPair(args, "-f", "segment");
        if (formatIndex < 0)
            return Local("not an HLS segment transcode");
        string? segmentList = ValueOf(args, "-segment_list");
        if (segmentList is null)
            return Local("no segment list");
        string output = args[^1];
        if (!output.Contains("%d", StringComparison.Ordinal))
            return Local("unexpected output pattern");
        if (!options.OutputRoots.Any(r => IsUnder(output, r) && IsUnder(segmentList, r)))
            return Local("output outside transcoding temp path");
        if (!TranscodesVideo(args))
            return Local("no video encoding (remux/audio stays local)");
        // The shim sends only the video output (SplitSubtitleOutputs); several outputs here are something unknown.
        if (args.Skip(LastInputIndex(args) + 1).Count(a => a.Contains("%d", StringComparison.Ordinal)) > 1)
            return Local("several outputs");

        double segmentSeconds = ParseDuration(ValueOf(args, "-segment_time")) ?? 3;
        int startIndex = int.TryParse(ValueOf(args, "-segment_start_number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (i == args.Count - 1)
            {
                template.Add("{out}");
                break;
            }
            string baseName = a.Split(':')[0];
            if (DroppedWithValue.Contains(baseName))
            {
                i++;
                continue;
            }
            if (a == "-i")
            {
                string input = args[++i];
                if (input.StartsWith("file:", StringComparison.Ordinal))
                    input = input[5..];
                if (input.Contains("://", StringComparison.Ordinal))
                    return Local("network input");
                if (!IsAllowed(input, options))
                    return Local("input outside allowed roots");
                long? size = options.GetFileSize(input);
                if (size is null)
                    return Local("input not readable");
                int id = files.Count;
                files.Add(new PlannedFile(id, JobFileKind.Input, input, Path.GetFileName(input), size.Value));
                template.Add("-i");
                template.Add($"{{input:{id}}}");
                continue;
            }
            if (a == "-segment_list")
            {
                i++;
                template.Add(a);
                template.Add("{list}");
                continue;
            }
            if (FilterOptions.Contains(baseName) && i + 1 < args.Count)
            {
                template.Add(a);
                string? graph = MapFilterFiles(args[++i], files, options, out string? error);
                if (graph is null)
                    return Local(error!);
                template.Add(graph);
                continue;
            }
            template.Add(a);
        }

        return new JobPlan(true, "ok", template.ToArray(), files, segmentSeconds, startIndex, output, segmentList, Requirements(template));

        JobPlan Local(string reason) => new(false, reason, [], [], 0, 0, "", "");
    }

    // What a worker must support to run the template: the output video encoder and every filter name.
    private static JobRequirements Requirements(List<string> template)
    {
        string encoder = "copy";
        var filters = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < template.Count - 1; i++)
        {
            string a = template[i], name = a.Split(':')[0];
            if (i > LastInputIndex(template) && (a is "-c:v" or "-vcodec" || a.StartsWith("-c:v:", StringComparison.Ordinal)))
                encoder = template[i + 1];
            if (FilterOptions.Contains(name))
                filters.UnionWith(ArgTemplateValidator.FilterNames(template[i + 1]));
        }
        return new JobRequirements(encoder, filters.Order(StringComparer.Ordinal).ToArray());
    }

    // Replaces the quoted filename=/fontsdir= values Emby writes in filter graphs by placeholders and registers the
    // files to download; only the quoted form is recognised (the one Emby generates).
    private static string? MapFilterFiles(string graph, List<PlannedFile> files, PlannerOptions options, out string? error)
    {
        string? failure = null;
        string mapped = FilterEscaping.QuotedFileOption().Replace(graph, m =>
        {
            if (failure != null)
                return m.Value;
            string key = m.Groups["key"].Value;
            string path = FilterEscaping.Unescape(m.Groups["value"].Value);
            if (!IsAllowed(path, options))
            {
                failure = $"{key} outside allowed roots";
                return m.Value;
            }
            if (key == "fontsdir")
            {
                foreach (string font in options.ListDirectory(path))
                {
                    long? fontSize = options.GetFileSize(font);
                    if (fontSize is not null)
                        files.Add(new PlannedFile(files.Count, JobFileKind.Font, font, Path.GetFileName(font), fontSize.Value));
                }
                return "fontsdir='{fonts}'";
            }
            long? size = options.GetFileSize(path);
            if (size is null)
            {
                failure = "filter file not readable";
                return m.Value;
            }
            int id = files.Count;
            files.Add(new PlannedFile(id, JobFileKind.Aux, path, Path.GetFileName(path), size.Value));
            return $"{key}='{{aux:{id}}}'";
        });
        error = failure;
        return failure is null ? mapped : null;
    }

    private static bool TranscodesVideo(IReadOnlyList<string> args)
    {
        // Codec options before the last input select decoders; only the ones after it describe the output.
        for (int i = LastInputIndex(args) + 1; i < args.Count - 1; i++)
        {
            string a = args[i];
            if ((a == "-c:v" || a.StartsWith("-c:v:", StringComparison.Ordinal) || a == "-vcodec") && args[i + 1] != "copy")
                return true;
        }
        return false;
    }

    /// <summary>Index of the value of the last "-i" (the last input path), or -1.</summary>
    public static int LastInputIndex(IReadOnlyList<string> args)
    {
        for (int i = args.Count - 2; i >= 0; i--)
            if (args[i] == "-i")
                return i + 1;
        return -1;
    }

    /// <summary>
    /// Emby adds to a playback transcode one extra output per text subtitle track (WebVTT segments the web player
    /// shows on demand), in the same ffmpeg command. Only the first output (the video) is heavy and goes to a worker;
    /// the subtitle outputs only copy text and stay on the server. Returns the command reduced to the inputs and the
    /// first output, and the command reduced to the inputs and the subtitle outputs (null when there are none). When
    /// an extra output is anything else the command is returned whole (the planner then keeps it local).
    /// </summary>
    public static (string[] Main, string[]? Subtitles) SplitSubtitleOutputs(IReadOnlyList<string> args)
    {
        int lastInput = LastInputIndex(args);
        // Output paths of Emby's segment outputs are the arguments with a %d pattern (segment lists have none).
        var outputs = new List<int>();
        for (int i = lastInput + 1; i < args.Count; i++)
            if (args[i].Contains("%d", StringComparison.Ordinal) && args[i - 1] is not "-segment_list" and not "-hls_segment_filename")
                outputs.Add(i);
        if (lastInput < 0 || outputs.Count < 2 || outputs[^1] != args.Count - 1)
            return (args.ToArray(), null);
        var groups = new List<List<string>>();
        int from = lastInput + 1;
        foreach (int end in outputs)
        {
            groups.Add(args.Skip(from).Take(end - from + 1).ToList());
            from = end + 1;
        }
        if (groups.Skip(1).Any(g => !IsSubtitleOutput(g)))
            return (args.ToArray(), null);
        var inputs = args.Take(lastInput + 1).ToList();
        string[] main = [.. inputs, .. groups[0]];
        // The graph file is Emby's log of the main run: the subtitle run must not write it too.
        var subtitleInputs = new List<string>();
        for (int i = 0; i < inputs.Count; i++)
        {
            if (inputs[i] == "-print_graphs_file" && i + 1 < inputs.Count)
            {
                i++;
                continue;
            }
            subtitleInputs.Add(inputs[i]);
        }
        string[] subtitles = [.. subtitleInputs, .. groups.Skip(1).SelectMany(g => g)];
        return (main, subtitles);
    }

    // A WebVTT segment output that only converts a subtitle stream (no audio, video at most copied).
    private static bool IsSubtitleOutput(List<string> group)
    {
        bool webvtt = false;
        for (int i = 0; i + 1 < group.Count; i++)
        {
            string a = group[i], v = group[i + 1];
            if ((a is "-c:s" or "-scodec" || a.StartsWith("-c:s:", StringComparison.Ordinal)) && v == "webvtt")
                webvtt = true;
            if ((a is "-c:v" or "-vcodec" || a.StartsWith("-c:v:", StringComparison.Ordinal)) && v != "copy")
                return false;
            if (a is "-c:a" or "-acodec" || a.StartsWith("-c:a:", StringComparison.Ordinal) || a.StartsWith("-filter", StringComparison.Ordinal) || a == "-vf" || a == "-af")
                return false;
        }
        return webvtt && group.Contains("-an");
    }

    /// <summary>The (last) input file of an ffmpeg command, without a "file:" prefix; null when there is none.</summary>
    public static string? InputPath(IReadOnlyList<string> args)
    {
        int i = LastInputIndex(args);
        if (i < 0)
            return null;
        string input = args[i];
        return input.StartsWith("file:", StringComparison.Ordinal) ? input[5..] : input;
    }

    /// <summary>Value following the first occurrence of <paramref name="option"/> (exact match, no stream specifier).</summary>
    public static string? ValueOf(IReadOnlyList<string> args, string option)
    {
        for (int i = 0; i < args.Count - 1; i++)
            if (args[i] == option)
                return args[i + 1];
        return null;
    }

    private static int LastIndexOfPair(IReadOnlyList<string> args, string option, string value)
    {
        for (int i = args.Count - 2; i >= 0; i--)
            if (args[i] == option && args[i + 1] == value)
                return i;
        return -1;
    }

    /// <summary>
    /// An ffmpeg duration in seconds: "[-][HH:]MM:SS[.m...]" or "[-]S+[.m...]" (av_parse_time); null when absent or
    /// unreadable. Like ffmpeg, two fields are MM:SS (TimeSpan would read "05:30" as five hours and a half).
    /// </summary>
    public static double? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string v = value.Trim();
        bool negative = v.StartsWith('-');
        if (negative)
            v = v[1..];
        string[] parts = v.Split(':');
        if (parts.Length > 3)
            return null;
        double seconds = 0;
        for (int p = 0; p < parts.Length; p++)
        {
            double field;
            // Hours and minutes are integers; only the last field may carry a fraction.
            if (p == parts.Length - 1)
            {
                if (!double.TryParse(parts[p], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out field))
                    return null;
            }
            else
            {
                if (!int.TryParse(parts[p], NumberStyles.None, CultureInfo.InvariantCulture, out int whole))
                    return null;
                field = whole;
            }
            seconds = seconds * 60 + field;
        }
        return negative ? -seconds : seconds;
    }

    private static bool IsAllowed(string path, PlannerOptions options) =>
        options.AllowedRoots.Any(root => IsUnder(path, root));

    /// <summary>
    /// True when <paramref name="path"/> is inside <paramref name="root"/>. Any ".." segment is refused outright
    /// (Emby only passes absolute, already normalised paths), then "." and duplicate '/' are normalised. Purely
    /// lexical (symbolic links are resolved by the caller, see ServerFiles). Path.GetFullPath is not used on purpose:
    /// these are the Linux server's paths, and on Windows (tests) it would add a drive letter and turn '/' into '\'.
    /// </summary>
    public static bool IsUnder(string path, string root)
    {
        if (!path.StartsWith('/') || path.Contains('\0') || path.Split('/').Contains(".."))
            return false;
        string full = NormalizeUnixPath(path);
        string r = NormalizeUnixPath(root).TrimEnd('/');
        return full == r || full.StartsWith(r + "/", StringComparison.Ordinal);
    }

    private static string NormalizeUnixPath(string path)
    {
        var parts = new List<string>();
        foreach (string part in path.Split('/'))
        {
            if (part.Length == 0 || part == ".")
                continue;
            if (part == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return "/" + string.Join('/', parts);
    }
}
