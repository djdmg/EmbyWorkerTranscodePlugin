using System.Text.Json;
using System.Text.RegularExpressions;

namespace RemoteTranscode.Common;

/// <summary>A published release as the GitHub API describes it; only what the update decision needs.</summary>
public sealed record UpdateRelease(string Tag, bool Draft, bool Prerelease, IReadOnlyList<UpdateAsset> Assets);

public sealed record UpdateAsset(string Name, long Size, string? State);

/// <summary>The setup a worker may install: every field has been validated, the URLs are rebuilt from the constants.</summary>
public sealed record UpdateCandidate(Version Version, string Tag, string SetupName, long SetupSize, string HashName)
{
    public string SetupUrl => WorkerUpdatePolicy.AssetUrl(Tag, SetupName);
    public string HashUrl => WorkerUpdatePolicy.AssetUrl(Tag, HashName);
}

/// <summary>
/// Pure decisions of the worker's automatic update. The security rule they serve: the binaries are not code-signed and
/// the worker does not fully trust its coordinator (see <see cref="ArgTemplateValidator"/>), so what a worker runs is
/// decided here, from the fixed repository below, and nothing received from the coordinator reaches these inputs except
/// <see cref="HintTriggersCheck"/>, whose only possible effect is an earlier check.
/// </summary>
public static partial class WorkerUpdatePolicy
{
    /// <summary>Compiled in, never received from the network.</summary>
    public const string Repository = "djdmg/EmbyWorkerTranscodePlugin";

    public const string LatestReleaseUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";

    /// <summary>
    /// Oldest worker this coordinator wants: an older one (with an updater, and AutoUpdate on) is told to check now. To be
    /// raised by hand when a plugin needs a newer worker. It carries no location: the worker still takes what GitHub says.
    /// </summary>
    public const string MinimumWorkerVersion = "0.7.19";

    public const string UpToDateReason = "à jour";

    public const long MaxSetupBytes = 1L << 30;
    public const int MaxHashFileBytes = 4 * 1024;
    public const int MaxApiResponseBytes = 4 * 1024 * 1024;

    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan CheckJitter = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan RetryWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromHours(24);
    public static readonly TimeSpan HintInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// No job for this long before an update starts: the gap between two slices of a conversion or a playback about to
    /// move here is not a free moment.
    /// </summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromMinutes(2);

    private const string VersionPattern = "(?:0|[1-9][0-9]{0,8})\\.(?:0|[1-9][0-9]{0,8})\\.(?:0|[1-9][0-9]{0,8})";

    // [0-9], not \d (any Unicode digit); \z, not $ (accepts a final line feed). No leading zero: one name per version.
    [GeneratedRegex("^EmbyRemoteWorker-Setup-(" + VersionPattern + ")\\.exe\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SetupName();

    [GeneratedRegex("^v" + VersionPattern + "\\z", RegexOptions.CultureInvariant)]
    private static partial Regex TagFormat();

    [GeneratedRegex("^" + VersionPattern + "\\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionFormat();

    [GeneratedRegex("^([0-9a-fA-F]{64})[ \\t]+\\*?([^\\r\\n]+)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex HashLine();

    /// <summary>"1.2.3" exactly, or null.</summary>
    public static Version? ParseVersion(string? text) =>
        text != null && VersionFormat().IsMatch(text) ? Version.Parse(text) : null;

    /// <summary>
    /// Strictly newer on major.minor.build. A 4th part is ignored: the assembly reports "0.7.19.0", and System.Version
    /// itself ranks 0.7.19 below 0.7.19.0.
    /// </summary>
    public static bool IsNewer(Version candidate, Version current) =>
        Normalize(candidate).CompareTo(Normalize(current)) > 0;

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>The version in an asset named exactly EmbyRemoteWorker-Setup-X.Y.Z.exe, or null.</summary>
    public static Version? ParseSetupAssetName(string? name)
    {
        if (name is null)
            return null;
        var match = SetupName().Match(name);
        return match.Success ? Version.Parse(match.Groups[1].Value) : null;
    }

    public static string HashAssetName(string setupName) => setupName + ".sha256";

    /// <summary>
    /// The lower-case hash in a one-line "hash  name" file, only if it names <paramref name="expectedAssetName"/>; null
    /// for anything else (a hash for another file, several lines, a bare hash).
    /// </summary>
    public static string? ParseSha256File(string? content, string expectedAssetName)
    {
        if (content is null)
            return null;
        var match = HashLine().Match(content.TrimStart('﻿').Trim());
        return match.Success && string.Equals(match.Groups[2].Value, expectedAssetName, StringComparison.Ordinal)
            ? match.Groups[1].Value.ToLowerInvariant()
            : null;
    }

    /// <summary>The body of GET /repos/{repo}/releases/latest; throws <see cref="FormatException"/> on anything else.</summary>
    public static UpdateRelease ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                throw new FormatException("réponse de GitHub inattendue");
            var list = new List<UpdateAsset>();
            foreach (var a in assets.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                    && a.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out long bytes))
                    list.Add(new UpdateAsset(name.GetString()!, bytes,
                        a.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String ? state.GetString() : null));
            }
            return new UpdateRelease(tag.GetString()!, Flag(root, "draft"), Flag(root, "prerelease"), list);
        }
        catch (JsonException e)
        {
            throw new FormatException("réponse de GitHub illisible", e);
        }

        // Absent means false; anything but a plain false counts as provisional.
        static bool Flag(JsonElement root, string name) => root.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.False;
    }

    /// <summary>
    /// The setup to install from this release, or null with the reason. Refused unless published (no draft or
    /// pre-release), with a well-formed tag, exactly one setup of the exact name form, its .sha256 next to it, both fully
    /// uploaded, and a version strictly newer than <paramref name="current"/>: a worker never goes back.
    /// </summary>
    public static UpdateCandidate? SelectUpdate(Version current, UpdateRelease release, out string reason)
    {
        reason = "";
        if (release.Draft || release.Prerelease)
        {
            reason = "publication provisoire ignorée";
            return null;
        }
        if (!TagFormat().IsMatch(release.Tag))
        {
            reason = "étiquette de publication invalide";
            return null;
        }
        var setups = release.Assets.Where(a => ParseSetupAssetName(a.Name) != null).ToList();
        if (setups.Count != 1)
        {
            reason = setups.Count == 0 ? "aucun programme d'installation dans la publication" : "plusieurs programmes d'installation dans la publication";
            return null;
        }
        var setup = setups[0];
        var version = ParseSetupAssetName(setup.Name)!;
        if (!IsNewer(version, current))
        {
            reason = UpToDateReason;
            return null;
        }
        string hashName = HashAssetName(setup.Name);
        var hashes = release.Assets.Where(a => a.Name == hashName).ToList();
        if (hashes.Count != 1)
        {
            reason = "empreinte absente de la publication";
            return null;
        }
        if (setup.Size <= 0 || setup.Size > MaxSetupBytes || hashes[0].Size <= 0 || hashes[0].Size > MaxHashFileBytes)
        {
            reason = "taille de fichier invalide dans la publication";
            return null;
        }
        if (!Uploaded(setup) || !Uploaded(hashes[0]))
        {
            reason = "publication incomplète";
            return null;
        }
        return new UpdateCandidate(version, release.Tag, setup.Name, setup.Size, hashName);

        static bool Uploaded(UpdateAsset a) => a.State is null or "uploaded";
    }

    /// <summary>Built from the constant repository and validated parts: no URL given by the API response is ever used.</summary>
    public static string AssetUrl(string tag, string assetName) =>
        $"https://github.com/{Repository}/releases/download/{tag}/{assetName}";

    /// <summary>
    /// Where a download (including each redirect) may go: HTTPS on the standard port, GitHub or its release storage. The
    /// redirects themselves come from github.com over TLS; this stops the unexpected, the hash covers the content.
    /// </summary>
    public static bool IsAllowedDownloadUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
            return false;
        string host = uri.IdnHost;
        return string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// False while <paramref name="version"/> is the one last attempted, less than <see cref="RetryWindow"/> ago. Another
    /// version goes ahead at once. A date in the future (clock put back) does not block: the attempt rewrites it.
    /// </summary>
    public static bool MayAttempt(Version version, Version? lastAttempted, DateTime lastAttemptUtc, DateTime nowUtc)
    {
        if (lastAttempted is null || Normalize(version) != Normalize(lastAttempted))
            return true;
        var elapsed = nowUtc - lastAttemptUtc;
        return elapsed < TimeSpan.Zero || elapsed >= RetryWindow;
    }

    /// <summary>Time to the next check; <paramref name="random01"/> in [0, 1) spreads the workers around the 6 h.</summary>
    public static TimeSpan NextCheckDelay(double random01) => CheckInterval - CheckJitter + 2 * CheckJitter * Math.Clamp(random01, 0, 1);

    /// <summary>First check after a start: a little later, once the setup that relaunched this worker has exited.</summary>
    public static TimeSpan StartDelay(double random01) => TimeSpan.FromMinutes(1 + 4 * Math.Clamp(random01, 0, 1));

    /// <summary>
    /// How long GitHub asks to wait, or null: Retry-After on a refusal, otherwise the reset of an exhausted quota. Bounded,
    /// so a wrong header cannot silence the updater for good.
    /// </summary>
    public static TimeSpan? RateLimitWait(int status, TimeSpan? retryAfter, long? remaining, long? resetUnixSeconds, DateTime nowUtc)
    {
        TimeSpan? wait = null;
        if (status is 403 or 429 && retryAfter is { } after)
            wait = after;
        else if (remaining == 0 && resetUnixSeconds is { } reset)
            wait = reset >= 0 && reset <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
                ? DateTimeOffset.FromUnixTimeSeconds(reset).UtcDateTime - nowUtc
                : MaxRateLimitWait;
        else if (status == 429)
            wait = CheckInterval;
        if (wait is not { } w)
            return null;
        return w < TimeSpan.Zero ? TimeSpan.Zero : w > MaxRateLimitWait ? MaxRateLimitWait : w;
    }

    /// <summary>
    /// A coordinator's "minimum version" only moves the next check forward: when it names a version newer than this
    /// worker, and at most once per <see cref="HintInterval"/> so that a coordinator cannot use the worker to exhaust its
    /// GitHub quota. Anything malformed is ignored.
    /// </summary>
    public static bool HintTriggersCheck(string? minimumVersion, Version current, DateTime? lastHintUtc, DateTime nowUtc)
    {
        if (ParseVersion(minimumVersion) is not { } minimum || !IsNewer(minimum, current))
            return false;
        return lastHintUtc is not { } last || nowUtc - last >= HintInterval || nowUtc < last;
    }

    /// <summary>Coordinator side: true for a worker that reports a version older than <see cref="MinimumWorkerVersion"/>.</summary>
    public static bool NeedsUpdateHint(string? workerVersion) =>
        Version.TryParse(workerVersion, out var version) && IsNewer(ParseVersion(MinimumWorkerVersion)!, version);

    /// <summary>Free to update: no job at all (even one stopping) and none for <see cref="QuietPeriod"/>.</summary>
    public static bool IsIdle(int jobs, DateTime lastJobActivityUtc, DateTime nowUtc) =>
        jobs == 0 && nowUtc - lastJobActivityUtc >= QuietPeriod;

    /// <summary>
    /// The setup updates the registered installation, not "this program": a copy run from anywhere else (build output,
    /// unpacked package) must not update itself, or it would install or replace another copy.
    /// </summary>
    public static bool IsInstalledCopy(string? processFolder, string? registeredFolder)
    {
        if (string.IsNullOrEmpty(processFolder) || string.IsNullOrEmpty(registeredFolder))
            return false;
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(processFolder)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(registeredFolder)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
