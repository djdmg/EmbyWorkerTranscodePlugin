using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteTranscode.Common;
using RemoteTranscode.Coordinator;

namespace RemoteTranscode.Tests;

/// <summary>
/// What the monitoring page reads: the verdicts of the shim's check in the status, the history across a coordinator
/// restart, the audio-ahead setting; and the page's own script, under Node when it is on the PATH.
/// </summary>
public class MonitoringStatusTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rt-monitor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly CoordinatorOptions options;

    public MonitoringStatusTests()
    {
        Directory.CreateDirectory(root);
        options = new CoordinatorOptions { DataDir = Path.Combine(root, "data"), EncodeSpoolDir = Path.Combine(root, "encode") };
        Directory.CreateDirectory(options.DataDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private EncodeQueue NewQueue() => new(options, new Dispatcher(options, NullLogger<Dispatcher>.Instance), new ServerMedia(options),
        NullLogger<EncodeQueue>.Instance, new FailureReporter(options, NullLogger<FailureReporter>.Instance));

    private static EncodeTask NewTask(string id, EncodeStatus status) => new()
    {
        Id = id,
        Input = "/media/sample-movie.mkv",
        Output = "/transcoding/sample-movie.mp4",
        ChunkTemplate = ["-i", "input"],
        Requirements = new JobRequirements("libx264", ["scale"]),
        Status = status,
        Participants = ["machine-a", "machine-b"],
        Source = new SourceInfo(600, 0, 1080, null, 1, 0),
        Slices = [new EncodeSlice { Index = 0, Start = 0, End = 600, Last = true }],
        CreatedAt = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero),
    };

    private void Seed(params EncodeTask[] tasks) => File.WriteAllText(options.EncodeQueuePath,
        JsonSerializer.Serialize(tasks.ToList(), new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }));

    // As the status answers: Results.Json uses the web defaults.
    private static JsonElement Summary(EncodeQueue queue, string id)
    {
        string json = JsonSerializer.Serialize(queue.Summaries(100), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return JsonDocument.Parse(json).RootElement.EnumerateArray().Single(t => t.GetProperty("id").GetString() == id);
    }

    [Fact]
    public void Rejected_assembly_shows_its_reason_and_check_line()
    {
        Seed(NewTask("t1", EncodeStatus.Assembling));
        var queue = NewQueue();
        // The seeded task was active: a restart closes it. Back to assembling is what a real verdict finds.
        queue.Snapshot().Single().Status = EncodeStatus.Assembling;

        queue.OnConversionDone(new ConversionDone("t1", false, "durée trop courte de 2 s", "vidéo source 0-600, résultat 0-598"));

        var summary = Summary(queue, "t1");
        Assert.Equal("Failed", summary.GetProperty("status").GetString());
        Assert.Equal("durée trop courte de 2 s", summary.GetProperty("error").GetString());
        Assert.Equal("vidéo source 0-600, résultat 0-598", summary.GetProperty("check").GetString());
        Assert.Equal("machine-a, machine-b", summary.GetProperty("worker").GetString());
    }

    [Fact]
    public void Audio_ahead_rejection_is_kept_although_the_usual_assembly_passed()
    {
        Seed(NewTask("t1", EncodeStatus.Assembling));
        var queue = NewQueue();
        queue.Snapshot().Single().Status = EncodeStatus.Assembling;

        queue.OnConversionDone(new ConversionDone("t1", true, null, "vidéo source 0-600, résultat 0-600", "0 piste(s) audio au lieu de 1"));

        var summary = Summary(queue, "t1");
        Assert.Equal("Completed", summary.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("error").ValueKind);
        Assert.Contains("0 piste(s) audio au lieu de 1", summary.GetProperty("audio").GetString());
    }

    [Fact]
    public void Verdict_without_audio_rejection_leaves_the_audio_state_alone()
    {
        var task = NewTask("t1", EncodeStatus.Assembling);
        task.Audio = "prêt";
        Seed(task);
        var queue = NewQueue();
        queue.Snapshot().Single().Status = EncodeStatus.Assembling;

        queue.OnConversionDone(new ConversionDone("t1", true, null));

        Assert.Equal("prêt", Summary(queue, "t1").GetProperty("audio").GetString());
    }

    [Fact]
    public void The_check_of_a_file_made_by_emby_is_shown_and_leaves_the_outcome_alone()
    {
        Seed(NewTask("t1", EncodeStatus.Assembling));
        var queue = NewQueue();
        queue.Snapshot().Single().Status = EncodeStatus.Assembling;
        queue.OnConversionDone(new ConversionDone("t1", false, "durée trop courte de 2 s"));

        queue.OnEmbyResultChecked(new EmbyResultChecked("t1", "vidéo finissant 41 ms après la source", "vidéo source 0-600, résultat 0-600"));
        queue.OnEmbyResultChecked(new EmbyResultChecked("unknown", "ignored", null));

        var summary = Summary(queue, "t1");
        Assert.Equal("Failed", summary.GetProperty("status").GetString());
        Assert.Equal("durée trop courte de 2 s", summary.GetProperty("error").GetString());
        Assert.Equal("vidéo finissant 41 ms après la source", summary.GetProperty("embyResultProblem").GetString());
        Assert.Equal("vidéo source 0-600, résultat 0-600", summary.GetProperty("embyResult").GetString());
    }

    [Fact]
    public void A_file_made_by_emby_that_could_not_be_measured_still_says_so()
    {
        Seed(NewTask("t1", EncodeStatus.Failed));
        var queue = NewQueue();

        queue.OnEmbyResultChecked(new EmbyResultChecked("t1", "contrôle non terminé après 180 s", null));

        var summary = Summary(queue, "t1");
        Assert.Equal("contrôle non terminé après 180 s", summary.GetProperty("embyResultProblem").GetString());
        Assert.Equal("non mesuré", summary.GetProperty("embyResult").GetString());
    }

    [Fact]
    public void The_check_of_a_file_made_by_emby_travels_as_a_message()
    {
        var back = MessageJson.Deserialize(MessageJson.Serialize(new EmbyResultChecked("t1", null, "ligne")));
        Assert.Equal(new EmbyResultChecked("t1", null, "ligne"), back);
    }

    /// <summary>The page says the history survives restarts and what happens to a conversion in progress.</summary>
    [Fact]
    public void History_survives_a_restart()
    {
        Seed(NewTask("rejected", EncodeStatus.Assembling), NewTask("running", EncodeStatus.Running));
        var queue = NewQueue();
        queue.Snapshot().Single(t => t.Id == "rejected").Status = EncodeStatus.Assembling;
        queue.Snapshot().Single(t => t.Id == "running").Status = EncodeStatus.Running;
        queue.OnConversionDone(new ConversionDone("rejected", false, "durée trop courte", "vidéo source 0-600, résultat 0-598"));
        string updatedAt = Summary(queue, "rejected").GetProperty("updatedAt").GetString()!;

        var restarted = NewQueue();

        var rejected = Summary(restarted, "rejected");
        Assert.Equal("Failed", rejected.GetProperty("status").GetString());
        Assert.Equal("durée trop courte", rejected.GetProperty("error").GetString());
        Assert.Equal("vidéo source 0-600, résultat 0-598", rejected.GetProperty("check").GetString());
        Assert.Equal("échec de l'assemblage", rejected.GetProperty("note").GetString());
        Assert.Equal("machine-a, machine-b", rejected.GetProperty("worker").GetString());
        Assert.Equal("2026-01-01T10:00:00+00:00", rejected.GetProperty("createdAt").GetString());
        Assert.Equal(updatedAt, rejected.GetProperty("updatedAt").GetString());

        var running = Summary(restarted, "running");
        Assert.Equal("Cancelled", running.GetProperty("status").GetString());
        Assert.Contains("redémarré", running.GetProperty("note").GetString());
    }

    [Fact]
    public void Verdict_message_round_trips_and_older_shims_still_parse()
    {
        var done = new ConversionDone("t1", true, null, "check", "rejet");
        Assert.Equal(done, MessageJson.Deserialize(MessageJson.Serialize(done)));
        var older = Assert.IsType<ConversionDone>(MessageJson.Deserialize("""{"type":"conversionDone","taskId":"t1","success":false,"error":"e"}"""));
        Assert.Null(older.AudioAheadRejected);
        Assert.Null(older.Check);
    }

    [Fact]
    public void Status_settings_give_the_current_audio_ahead_value()
    {
        static bool Shown(CoordinatorOptions o) => JsonDocument.Parse(JsonSerializer.Serialize(o.StatusSettings(), new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            .RootElement.GetProperty("encodeAudioAhead").GetBoolean();

        Assert.True(Shown(options));
        // What Runtime.SetAudioAhead does to the running coordinator's options.
        options.EncodeAudioAhead = false;
        Assert.False(Shown(options));
    }

    [Fact]
    public void Status_settings_keep_their_other_fields_and_no_secret()
    {
        options.IssueReportToken = "secret-token-value";
        string json = JsonSerializer.Serialize(options.StatusSettings(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var settings = JsonDocument.Parse(json).RootElement;
        foreach (string field in new[] { "publicUrl", "offloadInputPattern", "encodeLocalFallback", "playbackPriority" })
            Assert.True(settings.TryGetProperty(field, out _), field);
        Assert.DoesNotContain("secret-token-value", json);
    }

    /// <summary>The page's script against a fake status (Web/monitor.test.js); nothing to run it with: nothing checked, as with ffmpeg elsewhere.</summary>
    [Fact]
    public async Task Page_script_passes_under_node()
    {
        string tests = Path.Combine(ThisDirectory(), "Web", "monitor.test.js");
        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("--test");
        start.ArgumentList.Add(tests);
        Process? node;
        try
        {
            node = Process.Start(start);
        }
        catch (Win32Exception)
        {
            return;
        }
        using (node)
        {
            var error = node!.StandardError.ReadToEndAsync();
            var output = node.StandardOutput.ReadToEndAsync();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await node.WaitForExitAsync(limit.Token);
            Assert.True(node.ExitCode == 0, await output + await error);
        }
    }

    private static string ThisDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
