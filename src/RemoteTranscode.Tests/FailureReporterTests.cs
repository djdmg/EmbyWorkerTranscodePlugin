using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteTranscode.Coordinator;

namespace RemoteTranscode.Tests;

/// <summary>
/// Automatic GitHub reports of failed conversions: anonymisation, duplicate signature, daily limit, and nothing at all
/// without a token. GitHub is never called: a fake HTTP handler stands in for it.
/// </summary>
public class FailureReporterTests : IDisposable
{
    private const string Input = "/volume1/Séries/Dr House/Saison 1/Dr House - S01E01 - Les Symptômes de Rebecca Adler.mkv";
    private const string Output = "/volume1/Emby/sync/7f3c2a9b1d4e4f6a8b0c1d2e3f4a5b6c/Dr House - S01E01.mp4";
    // A fake token, built at run time so the source holds nothing that looks like a real one (secret scanning).
    private const string Token = "github" + "_pat_" + "11TESTTOKEN0123456789_abcdefABCDEF";

    private readonly string dataDir = Path.Combine(Path.GetTempPath(), "rt-report-tests-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset now = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(dataDir, true); } catch (IOException) { }
    }

    private static FailureReport SampleReport(FailureKind kind = FailureKind.WorkerFailed, string? error = null) => new()
    {
        Kind = kind,
        Error = error ?? $"ffmpeg failed: [matroska,webm @ 000001d2c3b4a590] Read error at pos. 1234567 | Error opening input file {Input}. | " +
            @"C:\Users\SomeUser\AppData\Local\EmbyRemoteWorker\cache\Dr House - S01E01.mkv: Invalid data found | worker GAMING-PC (192.168.1.42) via https://emby.example.org:8097/jobs",
        WorkerErrorTail =
        [
            "[matroska,webm @ 000001d2c3b4a590] Read error at pos. 1234567",
            $"Error opening input file {Input}.",
            @"C:\Users\SomeUser\AppData\Local\EmbyRemoteWorker\cache\Dr House - S01E01.mkv: Invalid data found",
            "contact dimitri@example.fr on nas.local or fe80::1c2b:3a4d:5e6f:7081",
        ],
        FailedWorker = FailureReportFormatter.DescribeWorker("0.7.14", ["NVIDIA GeForce RTX 4080"], ["libx264", "h264_nvenc", "hevc_nvenc"], "libx264"),
        PluginVersion = "0.7.24",
        Server = "Linux x64, Emby 4.10.2",
        Installation = "3f9a1c2e",
        SourceCode = FailureReportFormatter.SourceCode(1_234_567_890, 2589.4, "3f9a1c2e5b7d4e6f8a9b0c1d2e3f4a5b"),
        Source = new SourceDescription("matroska,webm", "hevc", "Main 10", "yuv420p10le", 10, 1920, 1080, "23.976 (24000/1001)", 0.007, 2589.4,
            "smpte2084", "bt2020", ["eac3", "ac3"]),
        RequestedEncoder = "libx264",
        Filters = ["setparams", "scale", "tonemapx"],
        SliceMachines = ["worker 0.7.14, NVIDIA GeForce RTX 4080, libx264 or h264_nvenc", "worker 0.7.14, NVIDIA GeForce RTX 4080, libx264 or h264_nvenc", "server (Linux x64, Emby 4.10.2)", null],
        Machines = ["worker 0.7.14, NVIDIA GeForce RTX 4080, libx264 or h264_nvenc", "server (Linux x64, Emby 4.10.2)"],
        AssemblySummary = " 1:20@100.000/100.000 2:20@200.000/100.000",
        Check = "vidéo source 0.007-2589.407s, résultat 0.000-2589.317s",
        Sensitive = [Input, Output, "Dr House - S01E01 - Les Symptômes de Rebecca Adler", "Saison 1", "Dr House", "GAMING-PC", "Salon", "mon-nas", "emby.example.org"],
    };

    // Everything the sample carries that must never be published.
    private static readonly string[] Forbidden =
    [
        "volume1", "Séries", "House", "Saison", "Symptômes", "Rebecca", "Adler", "S01E01", "sync", "7f3c2a9b1d4e4f6a8b0c1d2e3f4a5b6c",
        "Users", "SomeUser", "AppData", "EmbyRemoteWorker", "GAMING-PC", "192.168.1.42", "emby.example.org", "example.org", "dimitri@",
        "nas.local", "fe80", ".mkv", ".mp4", "3f9a1c2e5b7d4e6f8a9b0c1d2e3f4a5b",
    ];

    [Fact]
    public void TitleAndBodyCarryTheDiagnosisButNothingPersonal()
    {
        var report = SampleReport();
        string sig = FailureReportFormatter.Signature(report.Kind, FailureReportFormatter.Sanitize(report.Error, report.Sensitive), "hevc");
        string title = FailureReportFormatter.Title(report, sig);
        string body = FailureReportFormatter.Body(report, sig);

        foreach (string text in new[] { title, body })
            foreach (string bad in Forbidden)
                Assert.DoesNotContain(bad, text, StringComparison.OrdinalIgnoreCase);

        Assert.StartsWith("[auto-report] Worker failed on a slice", title);
        Assert.Contains("sig:" + sig, title);
        Assert.Contains("(hevc)", title);
        Assert.Contains("<!-- auto-report sig:" + sig + " -->", body);
        // What is needed to find the cause.
        Assert.Contains("- Plugin: 0.7.24", body);
        Assert.Contains("server (Linux x64, Emby 4.10.2)", body);
        Assert.Contains("worker 0.7.14, NVIDIA GeForce RTX 4080, libx264 or h264_nvenc", body);
        Assert.Contains("hevc (Main 10), 10 bits (yuv420p10le), 1920x1080, 23.976 (24000/1001) fps", body);
        Assert.Contains("start_time: 0.007 s", body);
        Assert.Contains("HDR: yes (transfer smpte2084", body);
        Assert.Contains("2 track(s) (eac3, ac3)", body);
        Assert.Contains("Filters requested by Emby: setparams, scale, tonemapx", body);
        Assert.Contains("slices 1-2: worker 0.7.14", body);
        Assert.Contains("slice 3: server", body);
        Assert.Contains("slice 4: not done", body);
        Assert.Contains("1:20@100.000/100.000", body);
        Assert.Contains("Check: vidéo source 0.007-2589.407s", body);
        Assert.Contains("Read error at pos. 1234567", body);
        Assert.Contains("<path>", body);
        Assert.Contains("`" + report.SourceCode + "`", body);
    }

    [Theory]
    [InlineData(@"\\SERVEUR\partage\Films\Le Film (2019)\film.mkv: No such file")]
    [InlineData("Could not open '/mnt/user/media/Movies/Inception (2010)/Inception.mkv'")]
    [InlineData("http://10.0.0.5:8096/emby/videos/1234/stream.mkv?api_key=abcdef refused")]
    [InlineData("connexion à 2a01:e0a:1f2:3c40::12 refusée")]
    [InlineData("id 0f8fad5b-d9cb-469f-a165-70867728950e inconnu")]
    public void GenericSanitizingRemovesPathsAddressesAndIds(string error)
    {
        string clean = FailureReportFormatter.Sanitize(error, []);
        foreach (string bad in new[] { "SERVEUR", "Films", "mnt", "Movies", "Inception", "10.0.0.5", "api_key", "2a01", "0f8fad5b", ".mkv" })
            Assert.DoesNotContain(bad, clean, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizingKeepsTechnicalNumbers()
    {
        string text = "vidéo de 2589.317s au lieu de 2589.400s (-2 images), 24000/1001 fps, 00:12:34.56, chunk_00012.ts, N/A, 1:20@100.000/100.000 2:20@200.000/100.000";
        Assert.Equal(text, FailureReportFormatter.Sanitize(text, ["Salon"]));
    }

    [Fact]
    public void VersionsShapedLikeAddressesAreKeptRealAddressesAreNot()
    {
        // Issue #2: "worker <ip>" and "Meta Virtual Monitor (<ip>)" instead of the worker and driver versions.
        string text = "worker 0.7.15.0, Meta Virtual Monitor (11.1.45.729) + NVIDIA GeForce RTX 4080 (32.0.16.1088)";
        Assert.Equal(text, FailureReportFormatter.Sanitize(text, []));
        Assert.Equal("from <ip> and <ip>", FailureReportFormatter.Sanitize("from 192.0.2.10 and 203.0.113.7:443", []));
    }

    [Fact]
    public void WorkerDescriptionNeverHasItsName()
    {
        string d = FailureReportFormatter.DescribeWorker("0.7.14", ["NVIDIA GeForce RTX 4080"], ["libx264", "h264_nvenc", "hevc_nvenc", "libx265"], "libx264");
        Assert.Equal("worker 0.7.14, NVIDIA GeForce RTX 4080, libx264 or h264_nvenc", d);
        Assert.Equal("worker ?, no GPU listed", FailureReportFormatter.DescribeWorker(null, null, null, "libx265"));
    }

    [Fact]
    public void SignatureIgnoresWhatVariesBetweenOccurrences()
    {
        string a = FailureReportFormatter.Signature(FailureKind.AssemblyRejected, "vidéo de 2589.317s au lieu de 2589.400s (-2 images)", "hevc");
        string b = FailureReportFormatter.Signature(FailureKind.AssemblyRejected, "vidéo de 1310.020s au lieu de 1310.104s (-3 images)", "HEVC");
        Assert.Equal(a, b);
        Assert.Matches("^[0-9a-f]{8}$", a);
        // Paths, addresses and hexadecimal addresses do not count either.
        Assert.Equal(
            FailureReportFormatter.Signature(FailureKind.WorkerFailed, "ffmpeg failed: [h264 @ 0x55d1c2] error at /media/a/b.mkv from 192.168.1.2", "h264"),
            FailureReportFormatter.Signature(FailureKind.WorkerFailed, "ffmpeg failed: [h264 @ 0x7fe0aa] error at /data/x/yz.mkv from 10.0.0.7", "h264"));
        // The kind of event, the message and the codec do.
        Assert.NotEqual(a, FailureReportFormatter.Signature(FailureKind.GivenBack, "vidéo de 2589.317s au lieu de 2589.400s (-2 images)", "hevc"));
        Assert.NotEqual(a, FailureReportFormatter.Signature(FailureKind.AssemblyRejected, "pas de piste vidéo", "hevc"));
        Assert.NotEqual(a, FailureReportFormatter.Signature(FailureKind.AssemblyRejected, "vidéo de 2589.317s au lieu de 2589.400s (-2 images)", "h264"));
        Assert.Equal("vidéo de #s au lieu de #s (-# images)", FailureReportFormatter.Normalize("vidéo de 2589.317s au lieu de 2589.400s (-2 images)"));
    }

    [Fact]
    public void SourceCodeIsStableAndPerInstallation()
    {
        string a = FailureReportFormatter.SourceCode(1_234_567_890, 2589.4, "install-a");
        Assert.Equal(a, FailureReportFormatter.SourceCode(1_234_567_890, 2589.4, "install-a"));
        Assert.NotEqual(a, FailureReportFormatter.SourceCode(1_234_567_890, 2589.4, "install-b"));
        Assert.NotEqual(a, FailureReportFormatter.SourceCode(1_234_567_891, 2589.4, "install-a"));
        Assert.Matches("^[0-9a-f]{8}$", a);
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body, HttpRequestMessage Request)> Calls { get; } = [];
        public string IssuesJson { get; set; } = "[]";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool Throw { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.Method, request.RequestUri!.PathAndQuery, body, request));
            if (Throw)
                throw new HttpRequestException("network down");
            if (Status != HttpStatusCode.OK)
                return new HttpResponseMessage(Status) { Content = new StringContent("{\"message\":\"Bad credentials\"}") };
            return request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(IssuesJson, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{\"number\":12}") };
        }
    }

    private FailureReporter Reporter(FakeGitHub github, string? token = Token, bool enabled = true) =>
        new(new CoordinatorOptions { DataDir = dataDir, IssueReportToken = token, IssueReporting = enabled }, NullLogger.Instance, github, () => now);

    [Fact]
    public async Task WithoutTokenNothingIsEverSent()
    {
        var github = new FakeGitHub();
        var reporter = Reporter(github, token: null);
        Assert.Equal(FailureReportState.NoToken, reporter.State);
        Assert.False(reporter.TryClaim("conv1"));
        Assert.Equal(FailureReporter.Outcome.NotSent, await reporter.SendAsync(SampleReport()));
        Assert.Empty(github.Calls);
        // Not even the state file is written.
        Assert.False(File.Exists(Path.Combine(dataDir, "issue-reports.json")));
    }

    [Fact]
    public async Task DisabledSendsNothing()
    {
        var github = new FakeGitHub();
        var reporter = Reporter(github, enabled: false);
        Assert.Equal(FailureReportState.Disabled, reporter.State);
        Assert.False(reporter.TryClaim("conv1"));
        Assert.Equal(FailureReporter.Outcome.NotSent, await reporter.SendAsync(SampleReport()));
        Assert.Empty(github.Calls);
    }

    [Fact]
    public void AtMostFivePerDayAndOncePerConversion()
    {
        var reporter = Reporter(new FakeGitHub());
        Assert.Equal(FailureReportState.Active, reporter.State);
        for (int i = 1; i <= FailureReporter.DailyLimit; i++)
            Assert.True(reporter.TryClaim("conv" + i));
        Assert.False(reporter.TryClaim("conv6"));
        // Kept across a restart.
        var again = Reporter(new FakeGitHub());
        Assert.False(again.TryClaim("conv7"));
        // The next day: allowed again, but a conversion already reported never is.
        now = now.AddDays(1);
        Assert.False(again.TryClaim("conv1"));
        Assert.True(again.TryClaim("conv8"));
    }

    [Fact]
    public async Task OpensAnIssueWithLabelsWhenNoneMatches()
    {
        var github = new FakeGitHub { IssuesJson = "[{\"number\":3,\"title\":\"[auto-report] other sig:00000000\",\"body\":\"x\"}]" };
        var outcome = await Reporter(github).SendAsync(SampleReport());
        Assert.Equal(FailureReporter.Outcome.Created, outcome);
        Assert.Equal(2, github.Calls.Count);
        Assert.Equal(HttpMethod.Get, github.Calls[0].Method);
        Assert.Contains("/repos/djdmg/EmbyWorkerTranscodePlugin/issues?labels=auto-report&state=open", github.Calls[0].Path);
        var create = github.Calls[1];
        Assert.Equal(HttpMethod.Post, create.Method);
        Assert.Equal("/repos/djdmg/EmbyWorkerTranscodePlugin/issues", create.Path);
        Assert.Contains("\"labels\":[\"auto-report\",\"bug\"]", create.Body);
        Assert.Equal("EmbyDistributedTranscoding/" + FailureReporter.Version, create.Request.Headers.UserAgent.ToString());
        Assert.Equal(Token, create.Request.Headers.Authorization?.Parameter);
        // The token goes in the header only.
        Assert.DoesNotContain(Token, create.Body);
        foreach (string bad in Forbidden)
            Assert.DoesNotContain(bad, System.Text.Json.JsonDocument.Parse(create.Body!).RootElement.GetProperty("body").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CommentsTheOpenIssueWithTheSameSignature()
    {
        var report = SampleReport();
        string sig = FailureReportFormatter.Signature(report.Kind, FailureReportFormatter.Sanitize(report.Error, report.Sensitive), "hevc");
        var github = new FakeGitHub { IssuesJson = $"[{{\"number\":7,\"title\":\"something\",\"body\":\"<!-- auto-report sig:{sig} -->\"}}]" };
        Assert.Equal(FailureReporter.Outcome.Commented, await Reporter(github).SendAsync(report));
        Assert.Equal("/repos/djdmg/EmbyWorkerTranscodePlugin/issues/7/comments", github.Calls[1].Path);
    }

    [Fact]
    public async Task NetworkAndApiErrorsAreSwallowed()
    {
        Assert.Equal(FailureReporter.Outcome.Failed, await Reporter(new FakeGitHub { Throw = true }).SendAsync(SampleReport()));
        Assert.Equal(FailureReporter.Outcome.Failed, await Reporter(new FakeGitHub { Status = HttpStatusCode.Unauthorized }).SendAsync(SampleReport()));
    }

    [Fact]
    public void TitleInAnErrorDumpIsNotPublished()
    {
        var report = new FailureReport
        {
            Kind = FailureKind.WorkerFailed,
            Error = "ffmpeg failed: Input #0, matroska,webm, from 'x': | Metadata: | title           : Une Chose Sans Forme | Chapter #0:1: start 60 | Read error at pos. 12",
            WorkerErrorTail = ["title           : Une Chose Sans Forme", "comment         : pour SomeUser", "Read error at pos. 12"],
        };
        string sig = FailureReportFormatter.Signature(report.Kind, report.Error, null);
        foreach (string text in new[] { FailureReportFormatter.Title(report, sig), FailureReportFormatter.Body(report, sig) })
        {
            Assert.DoesNotContain("Chose", text);
            Assert.DoesNotContain("SomeUser", text);
            Assert.DoesNotContain("Chapter #", text);
        }
        Assert.Contains("Read error at pos. 12", FailureReportFormatter.Body(report, sig));
    }

    [Fact]
    public async Task TheVerdictIsInTheSignatureAndTheBodySentStaysUnderTheLimit()
    {
        // The same error with two causes opens two issues (#2 and #3 shared one signature).
        var github = new FakeGitHub();
        string Sent() => System.Text.Json.JsonDocument.Parse(github.Calls[^1].Body!).RootElement.GetProperty("body").GetString()!;
        var late = new DiagnosticsResult { Verdict = new Verdict(VerdictClass.ListingLate, "frames right, listed late by a uniform +36.7 ms.") };
        var extra = new DiagnosticsResult { Verdict = new Verdict(VerdictClass.ExtraFrames, "real extra frames in slice 3.") };
        var report = (DiagnosticsResult d) => new FailureReport { Kind = FailureKind.AssemblyRejected, Error = "vidéo de 10.5s au lieu de 10.1s (+9 images)", Diagnostics = d };
        Assert.Equal(FailureReporter.Outcome.Created, await Reporter(github).SendAsync(report(late)));
        string first = Sent();
        Assert.Equal(FailureReporter.Outcome.Created, await Reporter(github).SendAsync(report(extra)));
        string second = Sent();
        Assert.NotEqual(first.Split("-->")[0], second.Split("-->")[0]);
        Assert.Contains("**Verdict:** `listing-late`", first);
        Assert.Contains("**Verdict:** `extra-frames`", second);
        Assert.Contains("listing-late", System.Text.Json.JsonDocument.Parse(github.Calls[1].Body!).RootElement.GetProperty("title").GetString());
        Assert.True(first.Length <= 65536);
    }

    [Fact]
    public void InstallIdIsRandomAndKept()
    {
        var a = Reporter(new FakeGitHub()).InstallId;
        Assert.Matches("^[0-9a-f]{32}$", a);
        Assert.Equal(a, Reporter(new FakeGitHub()).InstallId);
    }

    [Fact]
    public void EmbeddedTokenIsDecoded()
    {
        char[] reversed = Token.ToCharArray();
        Array.Reverse(reversed);
        string encoded = "b64r:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(new string(reversed)));
        Assert.Equal(Token, FailureReporter.DecodeToken(encoded));
        Assert.Equal(Token, FailureReporter.DecodeToken(Token));
        Assert.Null(FailureReporter.DecodeToken(""));
        Assert.Null(FailureReporter.DecodeToken(null));
        Assert.Null(FailureReporter.DecodeToken("b64r:%%%"));
        // The test assembly has no token: inactive.
        Assert.Null(FailureReporter.EmbeddedToken(typeof(FailureReporterTests).Assembly));
    }
}
