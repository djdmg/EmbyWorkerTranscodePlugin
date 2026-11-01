using System.Text.RegularExpressions;

namespace RemoteTranscode.Common;

/// <summary>
/// Filters that only exist in Emby's ffmpeg fork, and their equivalent with standard FFmpeg filters, so workers running
/// a regular FFmpeg build can execute Emby's filter graphs.
/// </summary>
public static partial class EmbyFilters
{
    /// <summary>Standard filters a worker needs to run each Emby-only filter.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Equivalents = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["supertonemap"] = ["zscale", "format", "tonemap"],
    };

    // supertonemap[@instance][=options] up to the next filter separator or output label.
    [GeneratedRegex(@"supertonemap(?<inst>@[A-Za-z0-9_]+)?(?:=(?<opts>[^,;\[]*))?")]
    private static partial Regex SuperToneMap();

    // Algorithms of the standard tonemap filter; bt2390 (supertonemap's default) has no standard counterpart.
    private static readonly HashSet<string> StandardAlgorithms = new(StringComparer.Ordinal) { "none", "linear", "gamma", "clip", "reinhard", "hable", "mobius" };

    /// <summary>
    /// Replaces Emby's HDR→SDR "supertonemap" with zscale (linearise, BT.2020→BT.709 primaries) + tonemap + zscale
    /// (BT.709 transfer/matrix). Emby surrounds it with setparams/format filters that stay valid around the chain.
    /// </summary>
    public static string Translate(string graph) => SuperToneMap().Replace(graph, m =>
    {
        string inst = m.Groups["inst"].Success ? m.Groups["inst"].Value[1..] : "stm";
        var options = new List<string>();
        string algorithm = "hable";
        foreach (string pair in (m.Groups["opts"].Value ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = pair.Split('=', 2);
            if (kv.Length == 1)
                algorithm = kv[0];   // positional first option: tonemap algorithm
            else if (kv[0] == "tonemap")
                algorithm = kv[1];
            else if (kv[0] is "param" or "desat" or "peak")
                options.Add(pair);
        }
        if (!StandardAlgorithms.Contains(algorithm))
            algorithm = "hable";
        // tonemap's default desat=2 washes the highlights out to near white (measured on an HDR10 test pattern against
        // libplacebo); without a desat from Emby, none.
        if (!options.Any(o => o.StartsWith("desat=", StringComparison.Ordinal)))
            options.Add("desat=0");
        options.Insert(0, "tonemap=" + algorithm);
        return $"zscale@{inst}_lin=t=linear:npl=100,format@{inst}_f32=gbrpf32le,zscale@{inst}_p709=p=bt709," +
               $"tonemap@{inst}={string.Join(':', options)},zscale@{inst}_t709=t=bt709:m=bt709:r=tv";
    });

    /// <summary>
    /// An ffmpeg argument list with the value of every filter option (-filter_complex, -filter, -vf, -af) translated.
    /// The coordinator sends conversion slices translated this way, so every slice of a film gets the same graph
    /// whatever the version of the machine that encodes it (a worker's own translation then finds nothing to do).
    /// </summary>
    public static string[] TranslateArgs(IReadOnlyList<string> args) =>
        args.Select((a, i) => i > 0 && args[i - 1] is "-filter_complex" or "-filter" or "-vf" or "-af" ? Translate(a) : a).ToArray();

    /// <summary>Required filters with Emby-only ones replaced by the standard filters they translate to.</summary>
    public static IEnumerable<string> ExpandRequirements(IEnumerable<string> filters) =>
        filters.SelectMany(f => Equivalents.TryGetValue(f, out var eq) ? eq : [f]).Distinct();
}
