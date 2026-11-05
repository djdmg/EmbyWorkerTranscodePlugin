using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RemoteTranscode.Common;

namespace RemoteTranscode.Coordinator;

/// <summary>What went wrong with a conversion, for an automatic report.</summary>
public enum FailureKind
{
    /// <summary>The shim's check rejected the assembled file (duration, frames).</summary>
    AssemblyRejected,
    /// <summary>A worker failed a slice and was set aside for this conversion.</summary>
    WorkerFailed,
    /// <summary>The conversion was given back to Emby, which converts it with its own command.</summary>
    GivenBack,
}

/// <summary>Whether failures are reported to GitHub, as shown on the plugin page.</summary>
public enum FailureReportState
{
    Active,
    /// <summary>This build carries no token (local build, fork): nothing can be sent.</summary>
    NoToken,
    /// <summary>Turned off by the administrator.</summary>
    Disabled,
}

/// <summary>A source as an automatic report describes it: technical characteristics only, never a name or a path.</summary>
public sealed record SourceDescription(
    string? Container,
    string? VideoCodec,
    string? Profile,
    string? PixelFormat,
    int? BitDepth,
    int Width,
    int Height,
    string? FrameRate,
    double? VideoStartTime,
    double DurationSeconds,
    string? ColorTransfer,
    string? ColorPrimaries,
    IReadOnlyList<string> AudioCodecs);

/// <summary>
/// Everything an automatic report says about one failed conversion. The free texts (errors, check line, worker and
/// GPU descriptions) are cleaned by <see cref="FailureReportFormatter"/> when the report is written, with
/// <see cref="Sensitive"/> (paths, file names, worker and host names of this installation) removed first.
/// </summary>
public sealed class FailureReport
{
    public required FailureKind Kind { get; init; }
    /// <summary>The error as the coordinator saw it (Emby's message, the shim's verdict, the worker's ffmpeg error).</summary>
    public required string Error { get; init; }
    /// <summary>Last lines of the failing worker's ffmpeg error output (worker failures only).</summary>
    public IReadOnlyList<string> WorkerErrorTail { get; init; } = [];
    /// <summary>The failing worker, described by its type (<see cref="FailureReportFormatter.DescribeWorker"/>).</summary>
    public string? FailedWorker { get; init; }
    public string PluginVersion { get; init; } = "?";
    /// <summary>"Linux x64, Emby 4.10.2".</summary>
    public string Server { get; init; } = "?";
    /// <summary>Truncated installation id: tells reports of one installation apart, nothing more.</summary>
    public string Installation { get; init; } = "?";
    /// <summary>Anonymous code of the source (<see cref="FailureReportFormatter.SourceCode"/>), linked to the file in the local log only.</summary>
    public string? SourceCode { get; init; }
    public SourceDescription? Source { get; init; }
    public string RequestedEncoder { get; init; } = "?";
    /// <summary>Filters Emby's command asked for (names only).</summary>
    public IReadOnlyList<string> Filters { get; init; } = [];
    /// <summary>For each slice in order, the type of machine that encoded it (null: not done).</summary>
    public IReadOnlyList<string?> SliceMachines { get; init; } = [];
    /// <summary>Every machine type that took part, the failing one included.</summary>
    public IReadOnlyList<string> Machines { get; init; } = [];
    /// <summary>The "assembled from chunks" summary (slice:count@end/listed length).</summary>
    public string? AssemblySummary { get; init; }
    /// <summary>The shim's check line (source and result video spans).</summary>
    public string? Check { get; init; }
    /// <summary>Strings of this installation that must never be published (never sent themselves).</summary>
    public IReadOnlyList<string> Sensitive { get; init; } = [];
    /// <summary>The automatic analysis of this failure (see <see cref="FailureDiagnostics"/>); null when none could be run.</summary>
    public DiagnosticsResult? Diagnostics { get; init; }
}

/// <summary>
/// Builds the title, body and signature of an automatic report. Pure functions, so the anonymisation can be tested.
/// </summary>
public static class FailureReportFormatter
{
    public const string Marker = "auto-report";

    public static string KindText(FailureKind kind) => kind switch
    {
        FailureKind.AssemblyRejected => "Assembly rejected by the check",
        FailureKind.WorkerFailed => "Worker failed on a slice",
        _ => "Conversion given back to Emby",
    };

    private static string KindDetail(FailureKind kind) => kind switch
    {
        FailureKind.AssemblyRejected => "the assembled file did not pass the check (duration/frames); Emby converted the source again on the server",
        FailureKind.WorkerFailed => "a worker's run of a slice failed; the worker was set aside for this conversion, which went on elsewhere",
        _ => "the conversion could not be finished by the workers and was given back to Emby, which converted it with its own command",
    };

    /// <summary>
    /// Duplicate key: the kind of event, the error without anything that varies from one file or run to the next
    /// (numbers, times, paths), the source's video codec and the verdict of the analysis, so two causes with the same
    /// error do not share an issue. Eight hex digits of its SHA-256.
    /// </summary>
    public static string Signature(FailureKind kind, string error, string? codec, string? verdict = null)
    {
        string key = $"{kind}|{Normalize(error)}|{(codec ?? "unknown").ToLowerInvariant()}";
        if (verdict != null)
            key += "|" + verdict;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8].ToLowerInvariant();
    }

    /// <summary>
    /// The error reduced to what does not vary between occurrences: cleaned (<see cref="Sanitize"/>), numbers and
    /// hexadecimal words (addresses, ids) replaced by '#', lower case, single spaces.
    /// </summary>
    public static string Normalize(string? error)
    {
        string s = Sanitize(error, []).ToLowerInvariant();
        s = Regex.Replace(s, @"\b0x[0-9a-f]+\b", "#");
        s = Regex.Replace(s, @"\b[0-9a-f]*\d[0-9a-f]*\b", "#");
        s = Regex.Replace(s, @"\d+", "#");
        s = Regex.Replace(s, @"#(?:[.,:/]#)+", "#");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > 300 ? s[..300] : s;
    }

    /// <summary>
    /// Anonymous and stable code of a source: the same file gives the same code on this installation, and nothing
    /// links it to the file outside this installation's own log.
    /// </summary>
    public static string SourceCode(long size, double durationSeconds, string installId)
    {
        string key = string.Create(CultureInfo.InvariantCulture, $"{size}|{durationSeconds:0.###}|{installId}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8].ToLowerInvariant();
    }

    // Paths: Windows drive and UNC paths, and Unix absolute paths (from a separator on, up to a space or a quote).
    private static readonly Regex WindowsPath = new(@"\b[A-Za-z]:[\\/][^\s'""|<>,;]*", RegexOptions.Compiled);
    private static readonly Regex UncPath = new(@"\\\\[^\s'""|<>,;]+", RegexOptions.Compiled);
    private static readonly Regex UnixPath = new(@"(?<=^|[\s'""(\[=,:@])/(?:[^\s'""|<>)\],;])+", RegexOptions.Compiled);
    private static readonly Regex Url = new(@"\b[a-z][a-z0-9+.-]*://[^\s'""<>]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Email = new(@"\b[\w.+-]+@[\w-]+(?:\.[\w-]+)*\.[A-Za-z]{2,}\b", RegexOptions.Compiled);
    private static readonly Regex IPv4 = new(@"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?\b", RegexOptions.Compiled);
    private static readonly Regex IPv6 = new(@"(?<![\w:.])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?![\w:])", RegexOptions.Compiled);
    private static readonly Regex Guid32 = new(@"\b[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}\b", RegexOptions.Compiled);
    private static readonly Regex HostName = new(
        @"\b(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+(?:com|net|org|fr|de|be|ch|ca|uk|eu|nl|es|it|io|me|info|xyz|dev|app|cloud|lan|local|home|internal|localdomain|duckdns\.org|synology\.me)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MediaFile = new(
        @"[^\s'""/\\|<>]+\.(?:mkv|mp4|m4v|avi|mov|wmv|webm|mpg|mpeg|m2ts|mts|vob|iso|flv|ogv|3gp|ts)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ChunkFile = new(@"^(?:chunk|seg)_\d+\.ts$", RegexOptions.Compiled);
    // What is left of a path cut at a space ("House/S01E01" from ".../Dr House/S01E01 - Pilot.mkv"): any word with a
    // separator and a letter. Numbers with a slash (frame rates, 24000/1001) stay.
    private static readonly Regex PathFragment = new(@"[^\s'""|<>()\[\],;]*[/\\][^\s'""|<>()\[\],;]*", RegexOptions.Compiled);

    /// <summary>
    /// Removes what could identify the installation or its owner: first the given strings of this installation
    /// (paths, file and folder names, titles, worker and host names), then any path, URL, address, e-mail, host name,
    /// id or media file name left. Not meant to be reversible: what is removed shows as &lt;path&gt;, &lt;ip&gt;...
    /// </summary>
    private static bool IsAddress(string match)
    {
        var octets = match.Split(':')[0].Split('.');
        return octets.Length == 4 && octets.All(o => int.TryParse(o, out int v) && v <= 255) && octets[0] != "0";
    }

    /// <summary>
    /// Removes only this installation's own strings: for generated text (recipes, commands, tables) whose shape the
    /// general rules of <see cref="Sanitize"/> would take for a path ("scale=iw/2", "rate=24000/1001").
    /// </summary>
    public static string ScrubLiterals(string text, IEnumerable<string> sensitive)
    {
        // Longest first: a path goes as a whole before its file name or folder names are looked for.
        foreach (string literal in sensitive.Where(v => !string.IsNullOrWhiteSpace(v) && v.Trim().Length >= 3)
                     .Select(v => v.Trim()).Distinct().OrderByDescending(v => v.Length))
            // Whole words only: a folder called "out" or "media" must not eat "output" or "median". "_" and "." do separate words.
            text = Regex.Replace(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(literal) + @"(?![\p{L}\p{N}])",
                literal.Contains('/') || literal.Contains('\\') ? "<path>" : "<redacted>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return text;
    }

    /// <summary>
    /// <see cref="ScrubLiterals"/> for the plugin's own tables and commands, which are made of numbers: a folder named with digits only took a chunk's
    /// number out of a report, and could take the decimals of a time the same way. Such a folder's whole path is still removed, and free text
    /// still goes through <see cref="Sanitize"/> with every literal.
    /// </summary>
    public static string ScrubGenerated(string text, IEnumerable<string> sensitive) =>
        ScrubLiterals(text, sensitive.Where(v => !v.Trim().All(char.IsAsciiDigit)));

    // ffmpeg's dumps of a file's metadata: they hold titles, and no cause of a failure.
    private static readonly Regex MetadataLine = new(
        @"^\s*(?:Metadata:|Chapters?\b|Chapter #|Input #|Output #|Side data:|(?:title|comment|description|synopsis|album|album_artist|artist|composer|show|episode_id|episode_sort|season_number|network|genre|date|creation_time|encoder|handler_name|vendor_id|major_brand|minor_version|compatible_brands|filename|mimetype|language|publisher|copyright|performer|service_name|service_provider|BPS\S*|DURATION\S*|NUMBER_OF\S*|_STATISTICS\S*)\s*:)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The lines of a failed run's error ("ffmpeg failed: line | line | ..."), without ffmpeg's metadata dumps.</summary>
    public static List<string> ErrorLines(string? error)
    {
        const string FfmpegFailed = "ffmpeg failed: ";
        string text = error ?? "";
        if (text.StartsWith(FfmpegFailed, StringComparison.Ordinal))
            text = text[FfmpegFailed.Length..];
        return text.Split([" | ", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !MetadataLine.IsMatch(l)).ToList();
    }

    /// <summary>The error without ffmpeg's metadata dumps, which sanitizing could not recognise as titles.</summary>
    public static string WithoutMetadata(string? error)
    {
        const string FfmpegFailed = "ffmpeg failed: ";
        if (string.IsNullOrEmpty(error))
            return "";
        bool prefixed = error.StartsWith(FfmpegFailed, StringComparison.Ordinal);
        return (prefixed ? FfmpegFailed : "") + string.Join(" | ", ErrorLines(error));
    }

    public static string Sanitize(string? text, IEnumerable<string> sensitive)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        // URLs and e-mail addresses first, whole (a host name replaced inside them would cut them).
        string s = Url.Replace(text, "<url>");
        s = Email.Replace(s, "<email>");
        s = ScrubLiterals(s, sensitive);
        s = WindowsPath.Replace(s, "<path>");
        s = UncPath.Replace(s, "<path>");
        s = UnixPath.Replace(s, "<path>");
        s = PathFragment.Replace(s, m => m.Value.Any(char.IsLetter) && m.Value != "N/A" ? "<path>" : m.Value);
        // Only real addresses: four numbers up to 255, the first one not 0. A worker version (0.7.15.0) or a driver
        // version (11.1.45.729) has the same shape and is no personal data (reports showed "worker <ip>").
        s = IPv4.Replace(s, m => IsAddress(m.Value) ? "<ip>" : m.Value);
        s = IPv6.Replace(s, m => m.Value.Contains("::") || m.Value.Any(char.IsLetter) ? "<ip>" : m.Value);
        s = Guid32.Replace(s, "<id>");
        s = HostName.Replace(s, "<host>");
        s = MediaFile.Replace(s, m => ChunkFile.IsMatch(m.Value) ? m.Value : "<file>");
        return s;
    }

    /// <summary>
    /// A worker by its type only: "worker 0.7.14, NVIDIA GeForce RTX 4080, libx264 or h264_nvenc" (its version, its GPUs,
    /// and the validated encoders that can serve the requested one). Never its name.
    /// </summary>
    public static string DescribeWorker(string? version, IEnumerable<string>? gpus, IEnumerable<string>? encoders, string requestedEncoder)
    {
        var parts = new List<string> { "worker " + (string.IsNullOrWhiteSpace(version) ? "?" : version) };
        var gpuList = (gpus ?? []).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct().ToList();
        parts.Add(gpuList.Count > 0 ? string.Join(" + ", gpuList) : "no GPU listed");
        var all = (encoders ?? []).ToList();
        var relevant = new[] { requestedEncoder }.Concat(WorkerCommandBuilder.HardwareEquivalentsOf(requestedEncoder)).Where(all.Contains).ToList();
        if (relevant.Count > 0)
            parts.Add(string.Join(" or ", relevant));
        return string.Join(", ", parts);
    }

    public static string Title(FailureReport report, string signature)
    {
        string error = FirstLine(Sanitize(WithoutMetadata(report.Error), report.Sensitive));
        if (error.Length > 90)
            error = error[..87] + "...";
        string codec = report.Source?.VideoCodec ?? "unknown codec";
        string verdict = report.Diagnostics?.Verdict is { Class: not VerdictClass.Undetermined } v ? $", {v.Name}" : "";
        return $"[{Marker}] {KindText(report.Kind)}: {error} ({codec}{verdict}) sig:{signature}";
    }

    /// <summary>
    /// The report, within GitHub's limit (<see cref="ReportBudget"/>): the verdict and the recipe are served first, then
    /// the summaries, the raw tables last and collapsed.
    /// </summary>
    public static string Body(FailureReport report, string signature)
    {
        string Clean(string? text) => Sanitize(text, report.Sensitive);
        string Scrub(string text) => ScrubGenerated(text, report.Sensitive);
        static string Fence(string text) => "```\n" + text.Replace("```", "'''") + "\n```\n";
        static string Capped(string text, int length) => text.Length <= length ? text : text[..length] + "...";
        var diagnostics = report.Diagnostics;
        var sections = new List<ReportSection>();

        var head = new StringBuilder();
        head.Append(CultureInfo.InvariantCulture, $"<!-- {Marker} sig:{signature} -->\n");
        head.Append("Automatic report sent by the Emby plugin \"Emby Distributed Transcoding\" (anonymous: no path, file name, title, address, host or user name).\n\n");
        head.Append(CultureInfo.InvariantCulture, $"**Event:** {KindText(report.Kind)}: {KindDetail(report.Kind)}.\n");
        if (diagnostics is { } d)
        {
            head.Append(CultureInfo.InvariantCulture, $"**Verdict:** `{d.Verdict.Name}`: {Scrub(d.Verdict.Text)}\n");
            head.Append(CultureInfo.InvariantCulture, $"**Diagnostics:** {DiagnosticsRenderer.StatusText(d)}.\n");
            foreach (string note in d.Notes.Take(20))
                head.Append("- ").Append(Clean(note)).Append('\n');
        }
        else
            head.Append("**Diagnostics:** none could be run.\n");
        head.Append(CultureInfo.InvariantCulture, $"**Signature:** `sig:{signature}` (event + normalised error + video codec{(diagnostics != null ? " + verdict" : "")})\n");
        sections.Add(ReportSection.Whole(0, "header", head.ToString()));

        if (diagnostics != null)
            sections.AddRange(DiagnosticsRenderer.Recipe(diagnostics, Scrub));

        sections.Add(ReportSection.Whole(2, "error", "\n### Error\n" + Fence(Capped(Clean(WithoutMetadata(report.Error)), 4000))));
        var tail = report.WorkerErrorTail.Where(l => ErrorLines(l).Count > 0).Take(30).Select(l => Capped(Clean(l), 400).Replace("```", "'''")).ToList();
        if (tail.Count > 0)
            sections.Add(new ReportSection(2, "worker error lines", "\n### Last ffmpeg error lines of the worker\n```\n", tail, "```\n"));

        var versions = new StringBuilder("\n### Versions\n");
        versions.Append(CultureInfo.InvariantCulture, $"- Plugin: {Clean(report.PluginVersion)}\n");
        versions.Append(CultureInfo.InvariantCulture, $"- Server: server ({Clean(report.Server)}){FfmpegOf(diagnostics?.ServerFfmpeg)}\n");
        versions.Append(CultureInfo.InvariantCulture, $"- Installation: `{Clean(report.Installation)}`\n");
        string FfmpegFor(string machine) => FfmpegOf(diagnostics?.MachineFfmpeg.GetValueOrDefault(machine));
        if (report.FailedWorker != null)
            versions.Append(CultureInfo.InvariantCulture, $"- Failing machine: {Clean(report.FailedWorker)}{FfmpegFor(report.FailedWorker)}\n");
        foreach (string m in report.Machines.Where(m => m != report.FailedWorker))
            versions.Append(CultureInfo.InvariantCulture, $"- Also involved: {Clean(m)}{FfmpegFor(m)}\n");
        sections.Add(ReportSection.Whole(3, "versions", versions.ToString()));

        var source = new StringBuilder();
        source.Append(CultureInfo.InvariantCulture, $"\n### Source (code `{report.SourceCode ?? "?"}`)\n");
        if (report.Source is { } src)
        {
            source.Append(CultureInfo.InvariantCulture, $"- Container: {Clean(src.Container ?? "?")}\n");
            string bits = src.BitDepth is { } depth ? $"{depth} bits" : "? bits";
            source.Append(CultureInfo.InvariantCulture, $"- Video: {Clean(src.VideoCodec ?? "?")}{(src.Profile is { } p ? $" ({Clean(p)})" : "")}, {bits} ({Clean(src.PixelFormat ?? "?")}), {src.Width}x{src.Height}, {Clean(src.FrameRate ?? "?")} fps\n");
            source.Append(CultureInfo.InvariantCulture, $"- Video start_time: {(src.VideoStartTime is { } st ? st.ToString("0.######", CultureInfo.InvariantCulture) + " s" : "?")}\n");
            source.Append(CultureInfo.InvariantCulture, $"- Duration: {src.DurationSeconds:0.###} s\n");
            bool hdr = src.ColorTransfer is "smpte2084" or "arib-std-b67";
            source.Append(CultureInfo.InvariantCulture, $"- HDR: {(hdr ? "yes" : "no")} (transfer {Clean(src.ColorTransfer ?? "?")}, primaries {Clean(src.ColorPrimaries ?? "?")})\n");
            source.Append(CultureInfo.InvariantCulture, $"- Audio: {src.AudioCodecs.Count} track(s){(src.AudioCodecs.Count > 0 ? " (" + Clean(string.Join(", ", src.AudioCodecs)) + ")" : "")}\n");
        }
        else
            source.Append("- not analysed (ffprobe failed or source gone)\n");
        sections.Add(ReportSection.Whole(3, "source", source.ToString()));

        var conversion = new StringBuilder("\n### Conversion\n");
        conversion.Append(CultureInfo.InvariantCulture, $"- Requested encoder: {Clean(report.RequestedEncoder)}\n");
        conversion.Append(CultureInfo.InvariantCulture, $"- Filters requested by Emby: {(report.Filters.Count > 0 ? Clean(string.Join(", ", report.Filters)) : "none")}\n");
        if (report.Check != null)
            conversion.Append(CultureInfo.InvariantCulture, $"- Check: {Clean(report.Check)}\n");
        conversion.Append(CultureInfo.InvariantCulture, $"- Slices: {report.SliceMachines.Count}\n");
        var conversionLines = SliceRanges(report.SliceMachines).Select(line => $"  - {Clean(line)}").ToList();
        if (report.AssemblySummary != null)
            conversionLines.Add("- Assembled from chunks (slice:count@end/listed length):\n" + Fence(Clean(report.AssemblySummary.Trim())).TrimEnd());
        sections.Add(new ReportSection(3, "conversion", conversion.ToString(), conversionLines));

        if (diagnostics != null)
        {
            sections.AddRange(DiagnosticsRenderer.Summaries(diagnostics, Scrub));
            sections.AddRange(DiagnosticsRenderer.Raw(diagnostics).Select(s => s with { Lines = s.Lines.Select(Scrub).ToList() }));
        }
        return ReportBudget.Compose(sections);
    }

    private static string FfmpegOf(string? version) => string.IsNullOrEmpty(version) ? "" : $", ffmpeg {version}";

    /// <summary>Consecutive slices done by the same type of machine on one line: "slices 1-12: worker ...".</summary>
    public static IEnumerable<string> SliceRanges(IReadOnlyList<string?> machines)
    {
        int i = 0;
        while (i < machines.Count)
        {
            int j = i;
            while (j + 1 < machines.Count && machines[j + 1] == machines[i])
                j++;
            string who = machines[i] ?? "not done";
            yield return i == j ? $"slice {i + 1}: {who}" : $"slices {i + 1}-{j + 1}: {who}";
            i = j + 1;
        }
    }

    private static string FirstLine(string text)
    {
        string line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        // A worker's error carries its ffmpeg lines after the first " | ": the title keeps what comes before.
        int bar = line.IndexOf(" | ", StringComparison.Ordinal);
        return bar > 0 ? line[..bar] : line;
    }
}

/// <summary>
/// Opens (or comments) an issue on the project's GitHub repository when a conversion fails, so problems met on any
/// installation reach the author with what is needed to fix them, and nothing personal (see FailureReportFormatter).
/// At most <see cref="DailyLimit"/> reports a day per installation, one per conversion; sent in the background with a
/// short timeout, and any network or API error is ignored (logged at Debug): a report never affects a conversion.
/// </summary>
/// <remarks>
/// The token comes from the build (GitHub Actions secret ISSUE_REPORT_TOKEN, embedded in the plugin DLL's metadata):
/// it is never in the code or the repository. A token embedded in a public DLL CAN be extracted by anyone, so it must
/// be a fine-grained token restricted to this repository with the Issues permission only (read/write) and nothing
/// else. It is never logged, shown on a page, returned by the API or put in an error message.
/// </remarks>
public sealed class FailureReporter
{
    public const string Owner = "djdmg";
    public const string Repository = "EmbyWorkerTranscodePlugin";
    public const int DailyLimit = 5;
    /// <summary>Conversions already reported, remembered so a conversion is never reported twice.</summary>
    private const int RememberedConversions = 200;

    private readonly CoordinatorOptions options;
    private readonly ILogger logger;
    private readonly HttpClient http;
    private readonly Func<DateTimeOffset> clock;
    private readonly object gate = new();
    private string? installId;

    public FailureReporter(CoordinatorOptions options, ILogger<FailureReporter> logger)
        : this(options, logger, null, null)
    {
    }

    /// <param name="handler">HTTP handler (tests replace GitHub with a fake); null: the real network.</param>
    public FailureReporter(CoordinatorOptions options, ILogger logger, HttpMessageHandler? handler, Func<DateTimeOffset>? clock)
    {
        this.options = options;
        this.logger = logger;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(15);
        http.BaseAddress = new Uri("https://api.github.com/");
    }

    /// <summary>Plugin version, three parts ("0.7.24").</summary>
    public static string Version { get; } = typeof(FailureReporter).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}" : "?";

    public FailureReportState State =>
        string.IsNullOrWhiteSpace(options.IssueReportToken) && options.IssueReportFile is null ? FailureReportState.NoToken
        : !options.IssueReporting ? FailureReportState.Disabled
        : FailureReportState.Active;

    private string StatePath => Path.Combine(options.DataDir, "issue-reports.json");

    /// <summary>Random id of this installation (GUID, kept in the data folder); reports show its first 8 characters.</summary>
    public string InstallId
    {
        get
        {
            lock (gate)
            {
                if (installId != null)
                    return installId;
                string path = Path.Combine(options.DataDir, "install-id");
                try
                {
                    if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var known))
                        return installId = known.ToString("N");
                    installId = Guid.NewGuid().ToString("N");
                    Directory.CreateDirectory(options.DataDir);
                    File.WriteAllText(path, installId);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    installId ??= Guid.NewGuid().ToString("N");
                }
                return installId;
            }
        }
    }

    /// <summary>
    /// Takes one of today's reports for this conversion: false (with the reason logged) when reporting is off or
    /// impossible, the daily limit is reached, or this conversion was already reported. Persistent, so a restart
    /// neither resets the limit nor reports a conversion again. Cheap when inactive: nothing is read or written.
    /// </summary>
    public bool TryClaim(string conversionId)
    {
        switch (State)
        {
            case FailureReportState.NoToken:
                logger.LogInformation("Conversion {Id}: automatic GitHub report not sent (no token in this build)", conversionId);
                return false;
            case FailureReportState.Disabled:
                logger.LogDebug("Conversion {Id}: automatic GitHub report disabled", conversionId);
                return false;
        }
        lock (gate)
        {
            var state = LoadState();
            string today = clock().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (state.Day != today)
            {
                state.Day = today;
                state.Count = 0;
            }
            if (state.Conversions.Contains(conversionId))
            {
                logger.LogDebug("Conversion {Id}: already reported to GitHub", conversionId);
                return false;
            }
            if (state.Count >= DailyLimit)
            {
                logger.LogInformation("Conversion {Id}: automatic GitHub report not sent (limit of {Limit} reports a day reached)", conversionId, DailyLimit);
                return false;
            }
            state.Count++;
            state.Conversions.Add(conversionId);
            if (state.Conversions.Count > RememberedConversions)
                state.Conversions.RemoveRange(0, state.Conversions.Count - RememberedConversions);
            SaveState(state);
            return true;
        }
    }

    /// <summary>What happened to a report (for the tests and the Debug log).</summary>
    public enum Outcome { NotSent, Created, Commented, Failed }

    /// <summary>
    /// Sends a claimed report: comments the open auto-report issue with the same signature, or opens a new one.
    /// Never throws; errors are logged at Debug without the token or the response body.
    /// </summary>
    public async Task<Outcome> SendAsync(FailureReport report, CancellationToken token = default)
    {
        string? secret = options.IssueReportToken;
        if (State != FailureReportState.Active)
            return Outcome.NotSent;
        if (options.IssueReportFile is { } file)
        {
            string local = FailureReportFormatter.Signature(report.Kind, FailureReportFormatter.Sanitize(report.Error, report.Sensitive), report.Source?.VideoCodec, report.Diagnostics?.Verdict.Name);
            await File.WriteAllTextAsync(file, FailureReportFormatter.Title(report, local) + Environment.NewLine + Environment.NewLine + FailureReportFormatter.Body(report, local), token);
            return Outcome.NotSent;
        }
        if (string.IsNullOrWhiteSpace(secret))
            return Outcome.NotSent;
        string signature = FailureReportFormatter.Signature(report.Kind, FailureReportFormatter.Sanitize(report.Error, report.Sensitive), report.Source?.VideoCodec, report.Diagnostics?.Verdict.Name);
        string title = FailureReportFormatter.Title(report, signature);
        string body = FailureReportFormatter.Body(report, signature);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            int? existing = await FindOpenIssueAsync(secret, signature, timeout.Token);
            if (existing is { } number)
            {
                using var comment = Request(HttpMethod.Post, $"repos/{Owner}/{Repository}/issues/{number}/comments", secret,
                    JsonSerializer.Serialize(new { body }));
                using var answer = await http.SendAsync(comment, timeout.Token);
                if (!answer.IsSuccessStatusCode)
                    return Failed($"comment refused (HTTP {(int)answer.StatusCode})");
                logger.LogInformation("Failure report sig:{Signature} added to GitHub issue #{Number}", signature, number);
                return Outcome.Commented;
            }
            using var create = Request(HttpMethod.Post, $"repos/{Owner}/{Repository}/issues", secret,
                JsonSerializer.Serialize(new { title, body, labels = new[] { FailureReportFormatter.Marker, "bug" } }));
            using var created = await http.SendAsync(create, timeout.Token);
            if (!created.IsSuccessStatusCode)
                return Failed($"issue refused (HTTP {(int)created.StatusCode})");
            logger.LogInformation("Failure report sig:{Signature} sent to GitHub as a new issue", signature);
            return Outcome.Created;
        }
        catch (Exception ex)
        {
            // Network, timeout, unexpected JSON: the message never contains the token (headers are not in it).
            return Failed($"{ex.GetType().Name}: {ex.Message}");
        }

        Outcome Failed(string why)
        {
            logger.LogDebug("Failure report sig:{Signature} not sent to GitHub: {Why}", signature, why);
            return Outcome.Failed;
        }
    }

    /// <summary>Number of the open auto-report issue whose title or body carries this signature, null if none.</summary>
    private async Task<int?> FindOpenIssueAsync(string secret, string signature, CancellationToken token)
    {
        string mark = "sig:" + signature;
        for (int page = 1; page <= 3; page++)
        {
            using var request = Request(HttpMethod.Get, $"repos/{Owner}/{Repository}/issues?labels={FailureReportFormatter.Marker}&state=open&per_page=100&page={page}", secret, null);
            using var response = await http.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"issue list refused (HTTP {(int)response.StatusCode})");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            int count = 0;
            foreach (var issue in doc.RootElement.EnumerateArray())
            {
                count++;
                string text = (issue.TryGetProperty("title", out var t) ? t.GetString() : null) + "\n"
                    + (issue.TryGetProperty("body", out var b) ? b.GetString() : null);
                if (text.Contains(mark, StringComparison.Ordinal) && issue.TryGetProperty("number", out var n))
                    return n.GetInt32();
            }
            if (count < 100)
                break;
        }
        return null;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string secret, string? json)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd($"EmbyDistributedTranscoding/{Version}");
        if (json != null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>issue-reports.json: today's count and the conversions already reported.</summary>
    public sealed class ReportState
    {
        public string Day { get; set; } = "";
        public int Count { get; set; }
        public List<string> Conversions { get; set; } = [];
    }

    private ReportState LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
                return JsonSerializer.Deserialize<ReportState>(File.ReadAllText(StatePath)) ?? new ReportState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogDebug("Cannot read {File}: {Error}", StatePath, ex.Message);
        }
        return new ReportState();
    }

    private void SaveState(ReportState state)
    {
        try
        {
            Directory.CreateDirectory(options.DataDir);
            File.WriteAllText(StatePath + ".tmp", JsonSerializer.Serialize(state));
            File.Move(StatePath + ".tmp", StatePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug("Cannot save {File}: {Error}", StatePath, ex.Message);
        }
    }

    /// <summary>
    /// The token embedded in the plugin at build time (assembly metadata "IssueReportToken"), null when absent. The
    /// build stores it reversed and in base64 behind "b64r:" so that a plain search of the DLL for a GitHub token
    /// pattern does not find it; this is no protection (see the class remarks), only less noise.
    /// </summary>
    public static string? EmbeddedToken(Assembly assembly)
    {
        string? value = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "IssueReportToken")?.Value;
        return DecodeToken(value);
    }

    public static string? DecodeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim();
        if (!value.StartsWith("b64r:", StringComparison.Ordinal))
            return value;
        try
        {
            char[] chars = Encoding.UTF8.GetString(Convert.FromBase64String(value[5..])).ToCharArray();
            Array.Reverse(chars);
            string token = new string(chars).Trim();
            return token.Length == 0 ? null : token;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
