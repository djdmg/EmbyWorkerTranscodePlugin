namespace RemoteTranscode.Coordinator;

/// <summary>
/// Which workers may encode conversion slices. Since 0.7.19 a worker moves its -ss when its ffmpeg reads another file
/// start than the server's (JobOffer.ServerFileStart). An older one ignores that: on such a file each of its slices
/// starts one frame late, with a repeated or skipped frame at joints with server slices, and the assembled file still
/// passes the check. Which files are at risk is only known once a worker has run, so older workers get no slice at all.
/// A version that is missing or unreadable is refused too. Playback jobs are not concerned.
/// </summary>
public static class ConversionWorkers
{
    public const string MinimumVersion = "0.7.19";

    private static readonly Version Minimum = Version.Parse(MinimumVersion);

    public static bool Allowed(string? version) => Version.TryParse(version, out var v) && v >= Minimum;

    /// <summary>Why this version gets no conversion slice, null when it may: for the status API and the logs.</summary>
    public static string? Refusal(string? version) =>
        Allowed(version) ? null : $"version {Shown(version) ?? "unknown"}, {MinimumVersion} required for conversions";

    /// <summary><see cref="Refusal"/> in French: for the conversions' notes and the plugin page.</summary>
    public static string? RefusalLabel(string? version) =>
        Allowed(version) ? null : $"conversions refusées : version {Shown(version) ?? "inconnue"}, {MinimumVersion} requise";

    /// <summary>A readable version as usually written (no fourth part when 0); anything else a worker sent is not repeated.</summary>
    private static string? Shown(string? version) =>
        !Version.TryParse(version, out var v) ? null : v.Revision == 0 ? v.ToString(3) : v.ToString();
}
