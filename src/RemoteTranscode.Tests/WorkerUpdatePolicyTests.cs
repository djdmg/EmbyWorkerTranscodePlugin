using System.Text.Json;
using RemoteTranscode.Common;

namespace RemoteTranscode.Tests;

public class WorkerUpdatePolicyTests
{
    private static readonly Version Current = new(0, 7, 19, 0);
    private static readonly DateTime Now = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string Hash = new('a', 64);

    private static UpdateRelease Release(string tag = "v0.7.40", bool draft = false, bool prerelease = false, params UpdateAsset[] extra)
    {
        var assets = new List<UpdateAsset>
        {
            new("RemoteTranscode.EmbyPlugin.dll", 1000, "uploaded"),
            new("RemoteTranscode.EmbyPlugin.dll.sha256", 100, "uploaded"),
            new("EmbyRemoteWorker-Setup-0.7.20.exe", 200_000_000, "uploaded"),
            new("EmbyRemoteWorker-Setup-0.7.20.exe.sha256", 100, "uploaded"),
        };
        assets.AddRange(extra);
        return new UpdateRelease(tag, draft, prerelease, assets);
    }

    // ---- Versions ------------------------------------------------------------------------------

    [Theory]
    [InlineData("0.7.20", true)]
    [InlineData("0.8.0", true)]
    [InlineData("1.0.0", true)]
    [InlineData("0.7.100", true)]   // numeric, not alphabetical
    [InlineData("0.7.19", false)]   // same version: System.Version alone would rank 0.7.19.0 above 0.7.19
    [InlineData("0.7.18", false)]
    [InlineData("0.6.99", false)]
    public void Only_a_strictly_newer_version_counts(string candidate, bool newer) =>
        Assert.Equal(newer, WorkerUpdatePolicy.IsNewer(Version.Parse(candidate), Current));

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("0.0.0", true)]
    [InlineData("1.2", false)]
    [InlineData("1.2.3.4", false)]
    [InlineData("v1.2.3", false)]
    [InlineData("1.2.3\n", false)]
    [InlineData("1.02.3", false)]
    [InlineData("1.2.3-beta", false)]
    [InlineData("١.٢.٣", false)]    // Unicode digits
    [InlineData("99999999999.0.0", false)]
    [InlineData("", false)]
    public void Version_text_is_strict(string text, bool valid) =>
        Assert.Equal(valid, WorkerUpdatePolicy.ParseVersion(text) != null);

    // ---- Asset names ---------------------------------------------------------------------------

    [Fact]
    public void The_expected_setup_name_gives_its_version() =>
        Assert.Equal(new Version(0, 7, 20), WorkerUpdatePolicy.ParseSetupAssetName("EmbyRemoteWorker-Setup-0.7.20.exe"));

    [Theory]
    [InlineData("EmbyRemoteWorker-Setup-0.7.20.exe.sha256")]
    [InlineData("EmbyRemoteWorker-Setup-0.7.20.exe\n")]
    [InlineData("embyremoteworker-setup-0.7.20.exe")]
    [InlineData("EmbyRemoteWorker-Setup-0.7.20.EXE")]
    [InlineData("EmbyRemoteWorker-Setup-0.7.exe")]
    [InlineData("EmbyRemoteWorker-Setup-0.7.20.1.exe")]
    [InlineData("EmbyRemoteWorker-Setup-0.7.20-rc1.exe")]
    [InlineData("EmbyRemoteWorker-Setup-.exe")]
    [InlineData("xEmbyRemoteWorker-Setup-0.7.20.exe")]
    [InlineData("EmbyRemoteWorker-Setup-0.7.20.exe.exe")]
    [InlineData("..\\EmbyRemoteWorker-Setup-0.7.20.exe")]
    [InlineData("../EmbyRemoteWorker-Setup-0.7.20.exe")]
    [InlineData("EmbyRemoteWorker-Setup-0.7.20.cmd")]
    [InlineData("EmbyRemoteWorker-Setup-٠.7.20.exe")]
    [InlineData("RemoteTranscode.EmbyPlugin.dll")]
    [InlineData("")]
    public void Any_other_asset_name_is_refused(string name) =>
        Assert.Null(WorkerUpdatePolicy.ParseSetupAssetName(name));

    // ---- SHA-256 file --------------------------------------------------------------------------

    private const string Setup = "EmbyRemoteWorker-Setup-0.7.20.exe";

    [Theory]
    [InlineData("  ")]              // what the release workflow writes
    [InlineData(" *")]              // sha256sum binary mode
    [InlineData("\t")]
    public void A_hash_file_for_the_expected_name_gives_the_hash(string separator) =>
        Assert.Equal(Hash, WorkerUpdatePolicy.ParseSha256File(Hash + separator + Setup, Setup));

    [Fact]
    public void Hash_file_tolerates_case_final_line_feed_and_bom()
    {
        Assert.Equal(Hash, WorkerUpdatePolicy.ParseSha256File(Hash.ToUpperInvariant() + "  " + Setup + "\r\n", Setup));
        Assert.Equal(Hash, WorkerUpdatePolicy.ParseSha256File("\uFEFF" + Hash + "  " + Setup, Setup));
    }

    [Theory]
    [InlineData("{0}")]                                             // bare hash: says nothing of the file it is for
    [InlineData("{0}  EmbyRemoteWorker-Setup-0.7.21.exe")]          // another file
    [InlineData("{0}  embyremoteworker-setup-0.7.20.exe")]
    [InlineData("{0}  EmbyRemoteWorker-Setup-0.7.20.exe\nextra")]   // several lines
    [InlineData("{0}{0}  EmbyRemoteWorker-Setup-0.7.20.exe")]
    [InlineData("xyz  EmbyRemoteWorker-Setup-0.7.20.exe")]
    [InlineData("")]
    public void Any_other_hash_file_is_refused(string format) =>
        Assert.Null(WorkerUpdatePolicy.ParseSha256File(string.Format(format, Hash), Setup));

    [Fact]
    public void A_hash_too_short_or_not_hexadecimal_is_refused()
    {
        Assert.Null(WorkerUpdatePolicy.ParseSha256File(new string('a', 63) + "  " + Setup, Setup));
        Assert.Null(WorkerUpdatePolicy.ParseSha256File(new string('g', 64) + "  " + Setup, Setup));
    }

    // ---- GitHub answer -------------------------------------------------------------------------

    [Fact]
    public void A_github_answer_is_read_and_its_urls_are_not_used()
    {
        const string json = """
            {
              "tag_name": "v0.7.40", "draft": false, "prerelease": false, "body": "notes",
              "assets": [
                { "name": "EmbyRemoteWorker-Setup-0.7.20.exe", "size": 123, "state": "uploaded",
                  "browser_download_url": "https://192.0.2.1/evil.exe" },
                { "name": "EmbyRemoteWorker-Setup-0.7.20.exe.sha256", "size": 99, "state": "uploaded",
                  "browser_download_url": "https://192.0.2.1/evil.sha256" }
              ]
            }
            """;
        var candidate = WorkerUpdatePolicy.SelectUpdate(Current, WorkerUpdatePolicy.ParseRelease(json), out _);
        Assert.NotNull(candidate);
        Assert.Equal(new Version(0, 7, 20), candidate.Version);
        Assert.Equal(123, candidate.SetupSize);
        Assert.Equal("https://github.com/djdmg/EmbyWorkerTranscodePlugin/releases/download/v0.7.40/EmbyRemoteWorker-Setup-0.7.20.exe", candidate.SetupUrl);
        Assert.Equal("https://github.com/djdmg/EmbyWorkerTranscodePlugin/releases/download/v0.7.40/EmbyRemoteWorker-Setup-0.7.20.exe.sha256", candidate.HashUrl);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "tag_name": 3, "assets": [] }""")]
    [InlineData("""{ "tag_name": "v1.0.0" }""")]
    [InlineData("""{ "tag_name": "v1.0.0", "assets": {} }""")]
    public void An_unexpected_github_answer_is_an_error(string json) =>
        Assert.Throws<FormatException>(() => WorkerUpdatePolicy.ParseRelease(json));

    [Fact]
    public void Provisional_flags_count_unless_plainly_false()
    {
        var release = WorkerUpdatePolicy.ParseRelease("""{ "tag_name": "v1.0.0", "assets": [], "draft": true, "prerelease": "x" }""");
        Assert.True(release.Draft);
        Assert.True(release.Prerelease);
    }

    // ---- Which release ---------------------------------------------------------------------------

    [Fact]
    public void A_newer_setup_in_a_published_release_is_selected() =>
        Assert.NotNull(WorkerUpdatePolicy.SelectUpdate(Current, Release(), out _));

    [Fact]
    public void Plugin_tag_and_worker_version_are_independent()
    {
        // A much newer plugin tag with the same worker is no update; the version is the asset's.
        var sameWorker = new UpdateRelease("v9.9.9", false, false,
            [new(Setup.Replace("0.7.20", "0.7.19"), 100, "uploaded"), new(Setup.Replace("0.7.20", "0.7.19") + ".sha256", 100, "uploaded")]);
        Assert.Null(WorkerUpdatePolicy.SelectUpdate(Current, sameWorker, out string reason));
        Assert.Equal(WorkerUpdatePolicy.UpToDateReason, reason);
    }

    [Theory]
    [InlineData("0.7.19")]
    [InlineData("0.7.18")]
    [InlineData("0.1.0")]
    public void A_worker_never_goes_back(string version)
    {
        string name = $"EmbyRemoteWorker-Setup-{version}.exe";
        var release = new UpdateRelease("v0.7.40", false, false, [new(name, 100, "uploaded"), new(name + ".sha256", 100, "uploaded")]);
        Assert.Null(WorkerUpdatePolicy.SelectUpdate(Current, release, out _));
    }

    [Fact]
    public void Drafts_and_prereleases_are_refused()
    {
        Assert.Null(WorkerUpdatePolicy.SelectUpdate(Current, Release(draft: true), out _));
        Assert.Null(WorkerUpdatePolicy.SelectUpdate(Current, Release(prerelease: true), out _));
    }

    [Theory]
    [InlineData("0.7.40")]
    [InlineData("v0.7")]
    [InlineData("v0.7.40/../../../other")]
    [InlineData("v0.7.40?x=1")]
    [InlineData("v0.7.40\n")]
    [InlineData("")]
    public void A_tag_of_another_form_is_refused_as_it_would_enter_the_url(string tag) =>
        Assert.Null(WorkerUpdatePolicy.SelectUpdate(Current, Release(tag), out _));

    [Fact]
    public void Two_setups_in_a_release_are_ambiguous() =>
        Assert.Null(WorkerUpdatePolicy.SelectUpdate(Current, Release(extra: new UpdateAsset("EmbyRemoteWorker-Setup-0.7.21.exe", 100, "uploaded")), out _));

    [Fact]
    public void A_setup_without_its_hash_is_refused()
    {
        var release = new UpdateRelease("v0.7.40", false, false, [new(Setup, 100, "uploaded")]);
        Assert.Null(WorkerUpdatePolicy.SelectUpdate(Current, release, out _));
    }

    [Fact]
    public void A_look_alike_setup_is_ignored_not_selected()
    {
        var release = new UpdateRelease("v0.7.40", false, false,
            [new("EmbyRemoteWorker-Setup-0.7.20.exe.exe", 100, "uploaded"), new("EmbyRemoteWorker-Setup-0.7.20.exe.exe.sha256", 100, "uploaded")]);
        Assert.Null(WorkerUpdatePolicy.SelectUpdate(Current, release, out _));
    }

    [Theory]
    [InlineData(0L, 100L)]
    [InlineData(-1L, 100L)]
    [InlineData(WorkerUpdatePolicy.MaxSetupBytes + 1, 100L)]
    [InlineData(100L, 0L)]
    [InlineData(100L, WorkerUpdatePolicy.MaxHashFileBytes + 1L)]
    public void Absurd_sizes_are_refused(long setup, long hash)
    {
        var release = new UpdateRelease("v0.7.40", false, false, [new(Setup, setup, "uploaded"), new(Setup + ".sha256", hash, "uploaded")]);
        Assert.Null(WorkerUpdatePolicy.SelectUpdate(Current, release, out _));
    }

    [Fact]
    public void An_asset_still_uploading_is_refused()
    {
        var release = new UpdateRelease("v0.7.40", false, false, [new(Setup, 100, "starter"), new(Setup + ".sha256", 100, "uploaded")]);
        Assert.Null(WorkerUpdatePolicy.SelectUpdate(Current, release, out _));
    }

    // ---- Where a download may go -----------------------------------------------------------------

    [Theory]
    [InlineData("https://github.com/djdmg/EmbyWorkerTranscodePlugin/releases/download/v1.0.0/a.exe")]
    [InlineData("https://release-assets.githubusercontent.com/x/y")]
    [InlineData("https://objects.githubusercontent.com/x/y")]
    public void GitHub_and_its_storage_are_allowed(string url) =>
        Assert.True(WorkerUpdatePolicy.IsAllowedDownloadUri(new Uri(url)));

    [Theory]
    [InlineData("http://github.com/a")]
    [InlineData("http://objects.githubusercontent.com/a")]
    [InlineData("https://github.com:8443/a")]
    [InlineData("https://github.com.example.org/a")]
    [InlineData("https://evilgithubusercontent.com/a")]
    [InlineData("https://githubusercontent.com.example.org/a")]
    [InlineData("https://user:secret@github.com/a")]
    [InlineData("https://192.0.2.1/a")]
    [InlineData("https://example.org/github.com")]
    [InlineData("file:///C:/a.exe")]
    public void Anything_else_is_refused(string url) =>
        Assert.False(WorkerUpdatePolicy.IsAllowedDownloadUri(new Uri(url)));

    // ---- Retry window --------------------------------------------------------------------------

    private static readonly Version Target = new(0, 7, 20);

    [Fact]
    public void No_attempt_yet_goes_ahead() =>
        Assert.True(WorkerUpdatePolicy.MayAttempt(Target, null, default, Now));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(23, false)]
    [InlineData(24, true)]
    [InlineData(100, true)]
    public void A_failed_version_waits_24_hours(int hoursAgo, bool allowed) =>
        Assert.Equal(allowed, WorkerUpdatePolicy.MayAttempt(Target, Target, Now.AddHours(-hoursAgo), Now));

    [Fact]
    public void Another_version_does_not_wait() =>
        Assert.True(WorkerUpdatePolicy.MayAttempt(new Version(0, 7, 21), Target, Now, Now));

    [Fact]
    public void A_clock_put_back_does_not_block_for_good() =>
        Assert.True(WorkerUpdatePolicy.MayAttempt(Target, Target, Now.AddDays(30), Now));

    // ---- Schedule and GitHub's limits ------------------------------------------------------------

    [Theory]
    [InlineData(0.0, 5.5)]
    [InlineData(0.5, 6.0)]
    [InlineData(1.0, 6.5)]
    public void Checks_are_six_hours_apart_with_jitter(double random, double hours) =>
        Assert.Equal(TimeSpan.FromHours(hours), WorkerUpdatePolicy.NextCheckDelay(random));

    [Fact]
    public void First_check_waits_at_least_a_minute() =>
        Assert.True(WorkerUpdatePolicy.StartDelay(0) >= TimeSpan.FromMinutes(1) && WorkerUpdatePolicy.StartDelay(1) <= TimeSpan.FromMinutes(5));

    [Fact]
    public void Rate_limit_uses_retry_after_then_the_quota_reset()
    {
        Assert.Equal(TimeSpan.FromSeconds(90), WorkerUpdatePolicy.RateLimitWait(403, TimeSpan.FromSeconds(90), 10, null, Now));
        long reset = new DateTimeOffset(Now.AddMinutes(20)).ToUnixTimeSeconds();
        Assert.Equal(TimeSpan.FromMinutes(20), WorkerUpdatePolicy.RateLimitWait(403, null, 0, reset, Now));
        Assert.Equal(TimeSpan.FromMinutes(20), WorkerUpdatePolicy.RateLimitWait(200, null, 0, reset, Now));
        Assert.Equal(WorkerUpdatePolicy.CheckInterval, WorkerUpdatePolicy.RateLimitWait(429, null, null, null, Now));
    }

    [Fact]
    public void No_limit_no_wait()
    {
        Assert.Null(WorkerUpdatePolicy.RateLimitWait(200, null, 10, 0, Now));
        Assert.Null(WorkerUpdatePolicy.RateLimitWait(304, null, null, null, Now));
        Assert.Null(WorkerUpdatePolicy.RateLimitWait(500, TimeSpan.FromSeconds(5), null, null, Now));
    }

    [Fact]
    public void A_wrong_header_cannot_silence_the_updater()
    {
        Assert.Equal(WorkerUpdatePolicy.MaxRateLimitWait, WorkerUpdatePolicy.RateLimitWait(403, TimeSpan.FromDays(3650), null, null, Now));
        Assert.Equal(WorkerUpdatePolicy.MaxRateLimitWait, WorkerUpdatePolicy.RateLimitWait(403, null, 0, long.MaxValue, Now));
        Assert.Equal(TimeSpan.Zero, WorkerUpdatePolicy.RateLimitWait(403, null, 0, 0, Now));
    }

    // ---- Coordinator's hint ----------------------------------------------------------------------

    [Fact]
    public void A_hint_for_a_newer_version_brings_the_check_forward() =>
        Assert.True(WorkerUpdatePolicy.HintTriggersCheck("0.7.20", Current, null, Now));

    [Theory]
    [InlineData("0.7.19")]
    [InlineData("0.7.18")]
    [InlineData("https://192.0.2.1/evil.exe")]
    [InlineData("0.7.20; calc.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void Any_other_hint_is_ignored(string? hint) =>
        Assert.False(WorkerUpdatePolicy.HintTriggersCheck(hint, Current, null, Now));

    [Fact]
    public void Hints_cannot_exhaust_the_github_quota()
    {
        Assert.False(WorkerUpdatePolicy.HintTriggersCheck("0.7.20", Current, Now.AddMinutes(-59), Now));
        Assert.True(WorkerUpdatePolicy.HintTriggersCheck("0.7.20", Current, Now.AddMinutes(-60), Now));
    }

    [Fact]
    public void The_hint_message_carries_a_version_and_nothing_else()
    {
        var properties = typeof(WorkerUpdateHint).GetProperties().Where(p => p.DeclaringType == typeof(WorkerUpdateHint) && p.Name != "EqualityContract").ToList();
        Assert.Equal("MinimumVersion", Assert.Single(properties).Name);
        string json = MessageJson.Serialize(new WorkerUpdateHint("0.7.20"));
        var read = Assert.IsType<WorkerUpdateHint>(MessageJson.Deserialize(json));
        Assert.Equal("0.7.20", read.MinimumVersion);
        // Fields a hostile coordinator might add have nowhere to go.
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void Coordinator_hints_only_a_worker_older_than_the_minimum()
    {
        var minimum = WorkerUpdatePolicy.ParseVersion(WorkerUpdatePolicy.MinimumWorkerVersion);
        Assert.NotNull(minimum);
        Assert.True(WorkerUpdatePolicy.NeedsUpdateHint("0.0.1.0"));
        Assert.False(WorkerUpdatePolicy.NeedsUpdateHint(minimum + ".0"));
        Assert.False(WorkerUpdatePolicy.NeedsUpdateHint($"{minimum.Major + 1}.0.0.0"));
        Assert.False(WorkerUpdatePolicy.NeedsUpdateHint("not a version"));
        Assert.False(WorkerUpdatePolicy.NeedsUpdateHint(""));
        Assert.False(WorkerUpdatePolicy.NeedsUpdateHint(null));
    }

    // ---- When it may apply -----------------------------------------------------------------------

    [Theory]
    [InlineData(0, 2, true)]
    [InlineData(0, 60, true)]
    [InlineData(0, 1, false)]   // gap between two slices of a conversion
    [InlineData(1, 60, false)]  // a job, even one stopping
    public void Applies_only_with_no_job_and_none_lately(int jobs, int minutesSinceActivity, bool idle) =>
        Assert.Equal(idle, WorkerUpdatePolicy.IsIdle(jobs, Now.AddMinutes(-minutesSinceActivity), Now));

    [Theory]
    [InlineData(@"C:\Programs\EmbyRemoteWorker", @"C:\Programs\EmbyRemoteWorker", true)]
    [InlineData(@"C:\Programs\EmbyRemoteWorker\", @"c:\programs\embyremoteworker", true)]
    [InlineData(@"C:\Build\bin\Release", @"C:\Programs\EmbyRemoteWorker", false)]
    [InlineData(@"C:\Programs\EmbyRemoteWorker2", @"C:\Programs\EmbyRemoteWorker", false)]
    [InlineData(@"C:\Programs\EmbyRemoteWorker", null, false)]
    [InlineData(null, @"C:\Programs\EmbyRemoteWorker", false)]
    public void Only_the_registered_installation_updates_itself(string? process, string? registered, bool installed) =>
        Assert.Equal(installed, WorkerUpdatePolicy.IsInstalledCopy(process, registered));
}
