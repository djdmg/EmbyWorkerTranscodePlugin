using System.Diagnostics;
using System.Globalization;
using System.Text;
using RemoteTranscode.Coordinator;

namespace RemoteTranscode.Tests;

/// <summary>
/// The automatic diagnostics behind a failure report: canned ffprobe output through pure functions, the collector with
/// fake tools, the size budget. One test round-trips through a real ffmpeg when one is on the PATH.
/// </summary>
public class FailureDiagnosticsTests : IDisposable
{
    private readonly string spool = Path.Combine(Path.GetTempPath(), "rt-diag-tests-" + Guid.NewGuid().ToString("N"));

    public FailureDiagnosticsTests() => Directory.CreateDirectory(spool);

    public void Dispose()
    {
        try { Directory.Delete(spool, true); } catch (IOException) { }
    }

    private const double Frame = 1001.0 / 24000;
    private const string SecretPath = "/volume1/Séries/Dr House/Saison 1/Dr House - S01E01 - Rebecca Adler.mkv";

    // ffprobe's answer for a file full of names, as if it had printed more than it was asked for.
    private const string FingerprintJson = """
        {
          "streams": [
            { "index": 0, "codec_name": "hevc", "codec_long_name": "SECRET-LONGNAME", "profile": "Main", "codec_type": "video", "width": 1920, "height": 960, "has_b_frames": 2,
              "pix_fmt": "yuv420p", "level": 120, "color_range": "tv", "color_space": "bt709", "color_transfer": "bt709", "color_primaries": "bt709", "field_order": "progressive",
              "r_frame_rate": "24000/1001", "avg_frame_rate": "24000/1001", "time_base": "1/1000", "start_time": "0.005000",
              "disposition": { "attached_pic": 0 }, "tags": { "title": "Piste Secrete Rebecca", "language": "fre", "BPS": "1234" } },
            { "index": 1, "codec_name": "eac3", "codec_type": "audio", "sample_rate": "48000", "channels": 6, "channel_layout": "5.1(side)", "time_base": "1/1000", "start_time": "0.005000",
              "r_frame_rate": "0/0", "avg_frame_rate": "0/0", "disposition": { "attached_pic": 0 }, "tags": { "title": "Commentaire de Dimitri", "handler_name": "/volume1/secret/handler" } },
            { "index": 2, "codec_name": "subrip", "codec_type": "subtitle", "time_base": "1/1000", "start_time": "0.000000", "tags": { "title": "Sous-titres Secret" } }
          ],
          "chapters": [
            { "id": 1, "start_time": "0.000000", "tags": { "title": "Chapitre Rebecca" } },
            { "id": 2, "start_time": "60.000000", "tags": { "title": "Chapitre Final" } }
          ],
          "format": { "filename": "/volume1/Séries/Dr House/S01E01.mkv", "format_name": "matroska,webm", "format_long_name": "SECRET-FORMAT", "start_time": "0.005000", "duration": "700.000000", "bit_rate": "4000000",
            "tags": { "title": "Dr House - Rebecca Adler", "encoder": "SECRET-ENCODER", "comment": "contact 192.0.2.10 nas.local" } }
        }
        """;

    private static readonly string[] SecretWords =
    [
        "Rebecca", "Adler", "House", "S01E01", "volume1", "Secret", "SECRET", "Commentaire", "Dimitri", "Chapitre", "Sous-titres", "192.0.2.10", "nas.local", "Séries", "handler",
    ];

    private static string Pts(double v) => v.ToString("0.000000", CultureInfo.InvariantCulture);

    /// <summary>A chunk or sample as ffprobe prints it: frames every <see cref="Frame"/> from a start.</summary>
    private static string PacketsJson(double start, int count, int gop = 24)
    {
        var b = new StringBuilder("{\"packets\":[");
        for (int i = 0; i < count; i++)
            b.Append(i > 0 ? "," : "").Append(CultureInfo.InvariantCulture,
                $"{{\"pts_time\":\"{Pts(start + i * Frame)}\",\"dts_time\":\"{Pts(start + (i - 2) * Frame)}\",\"duration_time\":\"{Pts(Frame)}\",\"size\":\"{1000 + i}\",\"flags\":\"{(i % gop == 0 ? "K__" : "___")}\"}}");
        return b.Append("]}").ToString();
    }

    private const int FramesPerSlice = 2821;
    private const double SliceLength = FramesPerSlice * Frame;

    /// <summary>
    /// What a conversion's spool holds: the source starts at 0.005 s; <paramref name="late"/> slices (numbers) are listed
    /// 36.7 ms late; <paramref name="frames"/> overrides a slice's real frame count; <paramref name="shifted"/> slices hold
    /// the same number of frames, one frame later (issue #3: first frame skipped, first frame of the next slice kept).
    /// </summary>
    private (DiagnosticsRequest Request, Dictionary<int, string> Chunks) Scenario(int[] late, Dictionary<int, int>? frames = null, string? error = null, string? check = null, int[]? shifted = null)
    {
        var slices = new List<DiagnosticSlice>();
        var chunks = new Dictionary<int, string>();
        const int Count = 5;
        for (int s = 0; s < Count; s++)
        {
            bool last = s == Count - 1;
            double start = s * SliceLength, end = (s + 1) * SliceLength;
            int total = frames?.GetValueOrDefault(s + 1, FramesPerSlice) ?? FramesPerSlice;
            double listing = late.Contains(s + 1) ? 0.0367 : 0;
            var list = new List<DiagnosticChunk>();
            for (int c = 0; c < 2; c++)
            {
                int index = s * 100 + c;
                int first = c == 0 ? 0 : total / 2;
                int n = c == 0 ? total / 2 : total - total / 2;
                double a = start + first * Frame, b = a + n * Frame;
                list.Add(new DiagnosticChunk(index, a, b + (c == 1 ? listing : 0), n * Frame + (c == 1 ? listing : 0), 1000 + index));
                chunks[index] = PacketsJson(a + 0.005 + (shifted?.Contains(s + 1) == true ? Frame : 0), n);
                File.WriteAllText(Path.Combine(spool, $"chunk_{index:D5}.ts"), "x");
            }
            slices.Add(new DiagnosticSlice(s + 1, start, end, last, true, s % 2 == 1 ? "worker 0.7.17, NVIDIA GeForce RTX 4080, libx264 or h264_nvenc" : "server (Linux x64, Emby 4.10.2)", 1, 0, list));
        }
        return (new DiagnosticsRequest
        {
            Kind = FailureKind.AssemblyRejected,
            Error = error ?? "vidéo de 588.442s au lieu de 588.296s (+4 images)",
            Check = check ?? "vidéo source 0.005-588.301s, résultat 0.000-588.442s",
            SourcePath = SecretPath,
            SpoolId = "abc",
            SpoolDir = spool,
            Slices = slices,
            Commands =
            [
                new("Run of slice 2 (worker and server; the server lists chunks as CSV)",
                    ["-copyts", "-ss", "117.65", "-t", "117.65", "-i", "{input:0}", "-map", "0:0", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2,subtitles=/volume1/Rebecca.srt", "-f", "segment", "-segment_list", "{list}", "{out}"]),
                new("Assembly by the shim", ["-i", "/volume1/Rebecca.mkv", "-c:v", "copy", "-metadata", "title=Dr House Rebecca", "/volume1/out/Rebecca.mp4"]),
            ],
            MachineFfmpeg = new Dictionary<string, string?> { ["worker 0.7.17, NVIDIA GeForce RTX 4080, libx264 or h264_nvenc"] = "ffmpeg version 9.0.2-full_build Copyright (c) 2000-2026 the FFmpeg developers" },
        }, chunks);
    }

    private static ToolRunner FakeFfprobe(Dictionary<int, string> chunks, string? fingerprint = null) => (args, _) =>
    {
        string entries = args.SkipWhile(a => a != "-show_entries").Skip(1).FirstOrDefault() ?? "";
        string last = args[^1];
        string output;
        if (entries.StartsWith("format=", StringComparison.Ordinal))
            output = fingerprint ?? FingerprintJson;
        else if (Path.GetFileName(last).StartsWith("chunk_", StringComparison.Ordinal))
            output = chunks[int.Parse(Path.GetFileNameWithoutExtension(last)[6..], CultureInfo.InvariantCulture)];
        else if (entries.Contains("stream_index", StringComparison.Ordinal))
            output = "{\"packets\":[{\"stream_index\":0,\"pts_time\":\"" + Pts(0.005 + 5 * SliceLength - Frame) + "\",\"duration_time\":\"" + Pts(Frame) + "\"},{\"stream_index\":1,\"pts_time\":\"588.300000\",\"duration_time\":\"0.032000\"}]}";
        else
            output = PacketsJson(0.005, args.Contains("%+#600") ? 600 : 120);
        return Task.FromResult((0, output, ""));
    };

    private static readonly ToolRunner FakeFfmpeg = (_, _) => Task.FromResult((0, "ffmpeg version 5.1-emby_2023 Copyright (c) 2000-2023\nconfiguration: --prefix=/volume1/secret\n", ""));

    private async Task<DiagnosticsResult> Collect(int[] late, Dictionary<int, int>? frames = null, string? error = null, string? check = null, string? fingerprint = null, int[]? shifted = null)
    {
        var (request, chunks) = Scenario(late, frames, error, check, shifted);
        return await new FailureDiagnostics(FakeFfprobe(chunks, fingerprint), FakeFfmpeg).CollectAsync(request, default);
    }

    private static FailureReport Report(DiagnosticsResult d, bool withSensitive = true, FailureKind kind = FailureKind.AssemblyRejected) => new()
    {
        Kind = kind,
        Error = "vidéo de 588.442s au lieu de 588.296s (+4 images)",
        Check = "vidéo source 0.005-588.301s, résultat 0.000-588.442s",
        PluginVersion = "0.7.31",
        Server = "Linux x64, Emby 4.10.2",
        Installation = "3f9a1c2e",
        SourceCode = "8bea40cf",
        RequestedEncoder = "libx264",
        Filters = ["scale"],
        SliceMachines = ["server (Linux x64, Emby 4.10.2)"],
        Machines = ["server (Linux x64, Emby 4.10.2)"],
        Sensitive = withSensitive ? [SecretPath, "Dr House", "Rebecca"] : [],
        Diagnostics = d,
    };

    // ---- Issue #3: everything that took hours to find must be in the report --------------------------------------

    private const string WorkerKind = "worker 0.7.17, NVIDIA GeForce RTX 4080, libx264 or h264_nvenc";

    [Fact]
    public async Task IssueThreeCountsAreRightButFramesAreShifted()
    {
        // The count of each slice is right: only the first frame's place tells.
        var d = await Collect(late: [2, 4], shifted: [2, 4]);
        Assert.Equal(DiagnosticsStatus.Completed, d.Status);
        Assert.Empty(d.Notes);
        Assert.Equal(VerdictClass.ShiftedFrames, d.Verdict.Class);
        Assert.Equal("shifted-frames", d.Verdict.Name);
        Assert.Contains("frame counts match", d.Verdict.Text);
        Assert.Contains("2 of 4 slices compared", d.Verdict.Text);
        Assert.Contains("2 (+1), 4 (+1)", d.Verdict.Text);
        string body = FailureReportFormatter.Body(Report(d), "12345678");
        Assert.Contains("**Verdict:** `shifted-frames`", body);
        Assert.Contains("| 2 | M2 | 1 | 0 | 2 | +36.7 | +36.7 | 2821 | 2821 | 0 | +1 |", body);
        Assert.Contains("| 3 | M1 | 1 | 0 | 2 | 0.0 | 0.0 | 2821 | 2821 | 0 | 0 |", body);
        Assert.Contains("ffmpeg 5.1-emby_2023", body);
    }

    [Fact]
    public async Task TheServerAndAWorkerDisagreeingOnTheFileStartIsTheVerdict()
    {
        var (request, chunks) = Scenario(late: [2, 4], shifted: [2, 4]);
        var collected = await new FailureDiagnostics(FakeFfprobe(chunks, FingerprintJson.Replace("\"start_time\": \"0.005000\", \"duration\": \"700", "\"start_time\": \"0.000000\", \"duration\": \"700")),
            FakeFfmpeg).CollectAsync(request, default);
        var d = DiagnosticsAnalysis.WithFileStarts(collected, new Dictionary<string, double> { [WorkerKind] = 0.005 }, request);
        Assert.Equal(VerdictClass.FileStartDisagreement, d.Verdict.Class);
        Assert.Contains("server's ffprobe 0 s, machines' ffmpeg 0.005 s", d.Verdict.Text);
        Assert.Contains("`shifted-frames`", d.Verdict.Text);
        string body = FailureReportFormatter.Body(Report(d), "12345678");
        Assert.Contains($"{WorkerKind}: 0.005 s", body);
        Assert.Contains("test/local/fake-emby-ffprobe", body);
    }

    [Fact]
    public void TheShiftIsSeenOnARealFfmpegsNumbers()
    {
        // Measured with ffmpeg 9.0.2, -copyts -start_at_zero to MPEG-TS, a file starting at 0.005 s: no seek, first pts 1.4;
        // the frame the server places at 10.015 s, -ss 1 ms below: 11.451711 (the same 72 packets, one frame later);
        // -ss half a frame below: 11.41.
        DiagnosticChunk Listed(int index) => new(index, 0, 3, 3, 1);
        DiagnosticSlice Slice(int number, double start) => new(number, start, start + 72 * Frame, false, true, null, 1, 0, [Listed(number)]);
        ChunkMeasure Chunk(int index, double first) => new(index, 72, first, first + 71 * Frame, Frame);
        var slices = new[] { Slice(1, 0), Slice(2, 10.015), Slice(3, 20.03) };
        var measures = DiagnosticsAnalysis.Slices(slices, new Dictionary<int, ChunkMeasure> { [1] = Chunk(1, 1.4), [2] = Chunk(2, 11.451711), [3] = Chunk(3, 21.41) }, null, null, null, null);
        Assert.Equal(+1, measures[1].FirstFrameShift);
        Assert.Equal(0, measures[2].FirstFrameShift);
        Assert.Equal(72, measures[1].Frames);
    }

    [Fact]
    public void TheFirstSlicesFirstFrameMovedByTheMuxerIsNoShift()
    {
        // Measured on a real rejection (bench T25): first chunk of the first slice 2 frames later than the rest of the clock (B-frames),
        // every other slice one frame late. Measured from that first frame, they looked one frame early.
        DiagnosticChunk Listed(int index) => new(index, 0, 3, 3, 1);
        DiagnosticSlice Slice(int number, double start, double length) => new(number, start, start + length, false, true, null, 1, 0, [Listed(number)]);
        var slices = new[] { Slice(1, 0, 105 * Frame), Slice(2, 105 * Frame, 105 * Frame), Slice(3, 210 * Frame, 105 * Frame) };
        var chunks = new Dictionary<int, ChunkMeasure>
        {
            [1] = new(1, 105, 1.4 + 2 * Frame, 1.4 + 104 * Frame, Frame),
            [2] = new(2, 105, 1.4 + 106 * Frame, 1.4 + 210 * Frame, Frame),
            [3] = new(3, 105, 1.4 + 211 * Frame, 1.4 + 315 * Frame, Frame),
        };
        var measures = DiagnosticsAnalysis.Slices(slices, chunks, null, null, null, null);
        Assert.Equal(+1, measures[1].FirstFrameShift);
        Assert.Equal(+1, measures[2].FirstFrameShift);
    }

    [Fact]
    public void ShiftedSlicesWithOneFrameLostAreCalledShifted()
    {
        var v = Decide("vidéo de 480.126s au lieu de 480.021s (+3 images)",
            Measured(1), Measured(2) with { FirstFrameShift = 1 }, Measured(3) with { FirstFrameShift = 1 }, Measured(4, frames: 99) with { FirstFrameShift = 1 });
        Assert.Equal(VerdictClass.ShiftedFrames, v.Class);
        Assert.Contains("-1 in all", v.Text);
    }

    [Fact]
    public void ASampleCutInDecodingOrderIsNotAVariableRate()
    {
        // Sorted by time, a sample cut after N packets lacks a frame near its end when the file has B-frames.
        var packets = Enumerable.Range(0, 600).Where(i => i != 597).Select(i => new PacketInfo(i * Frame, i * Frame, Frame, 1000, "___")).ToList();
        var stats = DiagnosticsAnalysis.Stats(packets);
        Assert.True(stats.ConstantRate);
        var holed = Enumerable.Range(0, 600).Where(i => i != 300).Select(i => new PacketInfo(i * Frame, i * Frame, Frame, 1000, "___")).ToList();
        Assert.False(DiagnosticsAnalysis.Stats(holed).ConstantRate);
    }

    [Theory]
    [InlineData("-output_ts_offset 0.005, median, without", "out")]
    [InlineData("-output_ts_offset 0.005, median, without", "media")]
    [InlineData("worker 0.7.17", "work")]
    public void ALiteralDoesNotEatLongerWords(string text, string literal) =>
        Assert.Equal(text, FailureReportFormatter.ScrubLiterals(text, [literal]));

    [Theory]
    [InlineData("error in Mr Robot.mkv at", "Mr Robot")]
    [InlineData("error in mr_robot_S03 at", "mr_robot")]
    [InlineData("host PC-Alpha answered", "pc-alpha")]
    public void ALiteralIsStillRemovedBetweenSeparators(string text, string literal) =>
        Assert.DoesNotContain(literal, FailureReportFormatter.ScrubLiterals(text, [literal]), StringComparison.OrdinalIgnoreCase);


    [Fact]
    public async Task WithoutAWorkersViewTheReportSaysSo()
    {
        var d = await Collect(late: []);
        Assert.Contains("not reported (only the server's ffprobe is known here)", FailureReportFormatter.Body(Report(d), "12345678"));
    }

    [Fact]
    public async Task AnAgreementOrRealFrameCountKeepItsOwnClass()
    {
        var agreed = await Collect(late: [2, 4]);
        var (request, chunks) = Scenario(late: [2, 4]);
        var same = DiagnosticsAnalysis.WithFileStarts(agreed, new Dictionary<string, double> { [WorkerKind] = 0.005 }, request);
        Assert.Equal(VerdictClass.ListingLate, same.Verdict.Class);

        var (request2, chunks2) = Scenario(late: [], frames: new() { [2] = FramesPerSlice + 3 });
        var extra = await new FailureDiagnostics(FakeFfprobe(chunks2), FakeFfmpeg).CollectAsync(request2, default);
        var counted = DiagnosticsAnalysis.WithFileStarts(extra, new Dictionary<string, double> { [WorkerKind] = 0.02 }, request2);
        Assert.Equal(VerdictClass.ExtraFrames, counted.Verdict.Class);
        Assert.Contains("the machines disagree on the file's start", counted.Verdict.Text);
    }

    [Fact]
    public async Task ALateListingWithFirstFramesInPlaceIsOnlyThat()
    {
        var d = await Collect(late: [2, 4]);
        Assert.Equal(VerdictClass.ListingLate, d.Verdict.Class);
        Assert.Contains("frame counts match", d.Verdict.Text);
        Assert.Contains("first frames are in place on 4 slices compared", d.Verdict.Text);
        Assert.Contains("uniform +36.7 ms", d.Verdict.Text);
        Assert.Contains("2 of 4 slices (2, 4)", d.Verdict.Text);
        // A coincidence of numbers is shown as such, never as a cause.
        Assert.Contains("a measure, not a cause", d.Verdict.Text);
        Assert.DoesNotContain("frames right", d.Verdict.Text);
    }

    [Theory]
    [InlineData("[info]   Duration: 00:52:01.12, start: 0.005000, bitrate: 4000 kb/s", 0.005)]
    [InlineData("  Duration: N/A, start: 1.400000, bitrate: N/A", 1.4)]
    [InlineData("  Duration: 00:00:01.00, start: -0.021000, bitrate: 10 kb/s", -0.021)]
    public void TheInputStartIsReadFromFfmpegsLog(string line, double expected) =>
        Assert.Equal(expected, RemoteTranscode.Common.FfmpegLog.InputStart(line));

    [Theory]
    [InlineData("Input #0, matroska,webm, from '/volume1/start: 5.0 Rebecca.mkv':")]
    [InlineData("    title           : Duration: 12, start: 3.5")]
    [InlineData("frame=  100 fps= 25")]
    public void OtherLinesGiveNoInputStart(string line) => Assert.Null(RemoteTranscode.Common.FfmpegLog.InputStart(line));

    [Fact]
    public void JobEndedCarriesTheStartAndOlderWorkersStillRead()
    {
        var back = RemoteTranscode.Common.MessageJson.Deserialize(RemoteTranscode.Common.MessageJson.Serialize(new RemoteTranscode.Common.JobEnded("j", 0, null, 0.005)));
        Assert.Equal(0.005, Assert.IsType<RemoteTranscode.Common.JobEnded>(back).InputStart);
        var old = RemoteTranscode.Common.MessageJson.Deserialize("""{"type":"ended","jobId":"j","exitCode":0,"error":null}""");
        Assert.Null(Assert.IsType<RemoteTranscode.Common.JobEnded>(old).InputStart);
    }

    [Fact]
    public void EndOfStreamsAlwaysAsksForABoundedInterval()
    {
        // Open-ended without -select_streams, ffprobe 9.0.2 printed nothing in about 1 run out of 7, with exit code 0.
        var args = FailureDiagnostics.EndArgs("x", 12.5).ToList();
        Assert.Matches(@"^12\.5%\d+", args[args.IndexOf("-read_intervals") + 1]);
    }

    [Fact]
    public async Task AnEmptyEndOfStreamsIsTriedAgainThenSaid()
    {
        var (request, chunks) = Scenario(late: []);
        var fake = FakeFfprobe(chunks);
        int empties = 0;
        ToolRunner sometimes(int emptyCalls) => (args, token) =>
            args.Contains("-read_intervals") && !args.Contains("-select_streams") && empties++ < emptyCalls
                ? Task.FromResult((0, "{\"packets\":[]}", ""))
                : fake(args, token);
        var second = await new FailureDiagnostics(sometimes(1), FakeFfmpeg).CollectAsync(request, default);
        Assert.NotNull(second.Ends!.Video);
        Assert.Empty(second.Notes);
        empties = 0;
        var never = await new FailureDiagnostics(sometimes(99), FakeFfmpeg).CollectAsync(request, default);
        Assert.Contains("end of the streams: ffprobe read no packet", never.Notes);
    }

    [Fact]
    public async Task RecipeReproducesTheSourceWithoutNamingAnything()
    {
        var d = await Collect(late: [2]);
        string recipe = d.Recipe!.Command;
        Assert.StartsWith("ffmpeg -y -hide_banner -t 420 -f lavfi -i \"testsrc2=size=1920x960:rate=24000/1001\"", recipe);
        // Video and audio both start at 0.005 s: only the output moves.
        Assert.Contains("-t 420 -f lavfi -i \"anullsrc=r=48000:cl=5.1(side)\"", recipe);
        Assert.Contains("-output_ts_offset 0.005 synthetic.mkv", recipe);
        Assert.DoesNotContain("-itsoffset", recipe);
        Assert.Contains("-map 0:v -map 1:a -c:v libx265", recipe);
        Assert.Contains("keyint=24:min-keyint=24:scenecut=0:bframes=2", recipe);
        Assert.Contains("-pix_fmt yuv420p -profile:v main", recipe);
        Assert.Contains("-c:a:0 eac3", recipe);
        Assert.EndsWith(" synthetic.mkv", recipe);
        Assert.Contains(d.Recipe.Notes, n => n.Contains("1 subtitle track", StringComparison.Ordinal));
        Assert.Contains(d.Recipe.Notes, n => n.Contains("2 chapter(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void RecipeMovesAudioRelativeToTheVideo()
    {
        var source = new SourceFingerprint("matroska,webm", 0, 100, null, 0,
        [
            new StreamFingerprint { Index = 0, Type = "video", Codec = "h264", Width = 640, Height = 360, RFrameRate = "25/1", Start = 1.2 },
            new StreamFingerprint { Index = 1, Type = "audio", Codec = "ac3", Start = 0 },
        ]);
        var recipe = RecipeBuilder.Build(source, null, null)!;
        Assert.Contains("-itsoffset -1.2 -t 100 -f lavfi -i \"anullsrc", recipe.Command);
        Assert.Contains("-output_ts_offset 1.2 ", recipe.Command);
    }

    [Fact]
    public void RecipeKeepsVideoShorterThanAudioAndNotesVariableRate()
    {
        var source = DiagnosticsAnalysis.ParseFingerprint(FingerprintJson);
        var ends = new StreamEnds(600.0, new Dictionary<int, double> { [1] = 634.0 }, 580);
        var stats = new PacketStats(600, 0.04, 0.02, 0.08, false, 48, 96, 12, 0);
        var recipe = RecipeBuilder.Build(source, stats, ends)!;
        Assert.Contains("-t 386 -f lavfi -i \"testsrc2", recipe.Command);
        Assert.Contains("-t 420 -f lavfi -i \"anullsrc", recipe.Command);
        Assert.Contains(recipe.Notes, n => n.Contains("ends 34 s before its audio", StringComparison.Ordinal));
        Assert.Contains(recipe.Notes, n => n.Contains("variable frame rate", StringComparison.Ordinal));
    }

    // ---- Verdicts -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ExtraFramesAreNamedBySlice()
    {
        var d = await Collect(late: [], frames: new() { [2] = FramesPerSlice + 3, [4] = FramesPerSlice + 1 });
        Assert.Equal(VerdictClass.ExtraFrames, d.Verdict.Class);
        Assert.Contains("real extra frames", d.Verdict.Text);
        Assert.Contains("slices 2 (+3), 4 (+1), +4 in all", d.Verdict.Text);
    }

    [Fact]
    public async Task MissingFramesAreNamedBySlice()
    {
        var d = await Collect(late: [], frames: new() { [3] = FramesPerSlice - 2 });
        Assert.Equal(VerdictClass.MissingFrames, d.Verdict.Class);
        Assert.Contains("3 (-2)", d.Verdict.Text);
    }

    [Fact]
    public async Task ExtraAndMissingTogether()
    {
        var d = await Collect(late: [], frames: new() { [1] = FramesPerSlice + 1, [3] = FramesPerSlice - 1 });
        Assert.Equal(VerdictClass.ExtraAndMissingFrames, d.Verdict.Class);
    }

    private static SliceMeasure Measured(int number, int frames = 100, double? concat = 0, double? join = 0, bool done = true) => new()
    {
        Number = number,
        Done = done,
        Start = (number - 1) * 100,
        End = number * 100,
        Frames = frames,
        Expected = 100,
        ConcatMs = concat,
        JoinMs = join,
        ChunkCount = 1,
    };

    private static readonly SourceFingerprint Plain = new("matroska,webm", 0, 400, null, 0, [new StreamFingerprint { Index = 0, Type = "video", Codec = "h264", RFrameRate = "25/1", AvgFrameRate = "25/1" }]);

    private static Verdict Decide(string error, params SliceMeasure[] slices) =>
        DiagnosticsAnalysis.Decide(FailureKind.AssemblyRejected, error, null, slices, Plain, null, null, chunksMeasured: true, complete: true);

    [Fact]
    public void GapBetweenSlicesIsReported()
    {
        var v = Decide("vidéo de 300.2s au lieu de 300.0s (+5 images)", Measured(1, join: 200), Measured(2), Measured(3));
        Assert.Equal(VerdictClass.GapBetweenSlices, v.Class);
        Assert.Contains("1-2 (+200.0 ms)", v.Text);
    }

    [Fact]
    public void NonUniformListingIsDriftNotLateness()
    {
        var v = Decide("vidéo de 300.2s au lieu de 300.0s (+5 images)", Measured(1, concat: 30), Measured(2, concat: 12), Measured(3));
        Assert.Equal(VerdictClass.ListingDrift, v.Class);
        Assert.Contains("from +12.0 to +30.0 ms", v.Text);
    }

    [Fact]
    public void AudioIsNamedAndNothingExplainedIsUndetermined()
    {
        Assert.Equal(VerdictClass.Audio, Decide("1 piste(s) audio au lieu de 2", Measured(1)).Class);
        Assert.Equal(VerdictClass.Audio, Decide("durée 700.0s au lieu de 660.0s", Measured(1)).Class);
        var nothing = Decide("vidéo de 300.2s au lieu de 300.0s (+5 images)", Measured(1), Measured(2));
        Assert.Equal(VerdictClass.Undetermined, nothing.Class);
        Assert.Contains("nothing measured here explains", nothing.Text);
    }

    [Fact]
    public void NoMeasureNeverGivesAGuess()
    {
        var unmeasured = new SliceMeasure { Number = 1, Done = true, ChunkCount = 3 };
        Assert.Equal(VerdictClass.Undetermined, Decide("vidéo de 300.2s au lieu de 300.0s (+5 images)", unmeasured).Class);
        Assert.Equal(VerdictClass.Undetermined,
            DiagnosticsAnalysis.Decide(FailureKind.AssemblyRejected, "x", null, [Measured(1)], Plain, null, null, chunksMeasured: false, complete: true).Class);
    }

    [Theory]
    [InlineData("ffmpeg failed: [h264_nvenc @ 0x1] OpenEncodeSessionEx failed: incompatible client key", VerdictClass.EncoderStart)]
    [InlineData("ffmpeg failed: No such filter: 'tonemapx' | Error reinitializing filters!", VerdictClass.FilterError)]
    [InlineData("ffmpeg failed: [https @ 0x1] HTTP error 403 Forbidden", VerdictClass.NetworkInput)]
    [InlineData("ffmpeg failed: [matroska @ 0x1] Read error at pos. 1234", VerdictClass.SourceRead)]
    [InlineData("ffmpeg s'est arrêté 3 fois de suite sans rien encoder à partir de 12s (code 0)", VerdictClass.IdleRuns)]
    [InlineData("av_interleaved_write_frame(): No space left on device", VerdictClass.DiskFull)]
    [InlineData("something nobody has seen", VerdictClass.Undetermined)]
    public void OtherFailuresAreJudgedOnFixedPatterns(string error, VerdictClass expected)
    {
        var v = DiagnosticsAnalysis.Decide(FailureKind.WorkerFailed, error, null, [], null, null, null, false, true);
        Assert.Equal(expected, v.Class);
        Assert.DoesNotContain(error, v.Text);
    }

    [Fact]
    public void TheVerdictSeparatesSignatures()
    {
        string a = FailureReportFormatter.Signature(FailureKind.AssemblyRejected, "vidéo de 3121.992s au lieu de 3121.117s (+21 images)", "hevc", "listing-late");
        string b = FailureReportFormatter.Signature(FailureKind.AssemblyRejected, "vidéo de 2589.317s au lieu de 2589.400s (+2 images)", "hevc", "extra-frames");
        string c = FailureReportFormatter.Signature(FailureKind.AssemblyRejected, "vidéo de 1000.5s au lieu de 1000.1s (+9 images)", "hevc", "listing-late");
        Assert.NotEqual(a, b);
        Assert.Equal(a, c);
        Assert.NotEqual(a, FailureReportFormatter.Signature(FailureKind.AssemblyRejected, "vidéo de 3121.992s au lieu de 3121.117s (+21 images)", "hevc"));
    }

    // ---- Measures -------------------------------------------------------------------------------------------------

    [Fact]
    public void PacketStatsFindRateAndGop()
    {
        var stats = DiagnosticsAnalysis.Stats(DiagnosticsAnalysis.ParsePackets(PacketsJson(0.005, 240)));
        Assert.Equal(240, stats.Packets);
        Assert.True(stats.ConstantRate);
        Assert.Equal(24, stats.GopMedian);
        Assert.Equal(10, stats.Keyframes);
        Assert.Equal(Frame, stats.FrameSeconds!.Value, 4);
        var vfr = DiagnosticsAnalysis.Stats(DiagnosticsAnalysis.ParsePackets("""{"packets":[{"pts_time":"0","flags":"K__"},{"pts_time":"0.04"},{"pts_time":"0.08"},{"pts_time":"0.2"},{"pts_time":"0.24"}]}"""));
        Assert.False(vfr.ConstantRate);
        Assert.Null(vfr.GopMedian);
    }

    [Fact]
    public void SourceFramesAreCountedPerSliceForAVariableRate()
    {
        var slices = new List<DiagnosticSlice>
        {
            new(1, 0, 10, false, true, null, 1, 0, []),
            new(2, 10, 20, true, true, null, 1, 0, []),
        };
        var counts = DiagnosticsAnalysis.CountSourceFrames("1.5\n9.5,\n10.5\n25.5\nN/A\n", 0.5, slices);
        Assert.Equal(2, counts[1]);
        Assert.Equal(2, counts[2]);
    }

    [Fact]
    public async Task AMissingChunkGivesNoFrameCountForItsSlice()
    {
        var (request, chunks) = Scenario(late: []);
        File.Delete(Path.Combine(spool, "chunk_00101.ts"));
        var d = await new FailureDiagnostics(FakeFfprobe(chunks), FakeFfmpeg).CollectAsync(request, default);
        var slice = d.Slices.Single(s => s.Number == 2);
        Assert.Null(slice.Frames);
        Assert.Equal(1, slice.MissingChunks);
        Assert.Contains("(1 missing)", FailureReportFormatter.Body(Report(d), "12345678"));
    }

    // ---- Robustness ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATimeoutStillGivesAReportWithWhatWasCollected()
    {
        var (request, chunks) = Scenario(late: []);
        var fake = FakeFfprobe(chunks);
        ToolRunner hanging = async (args, token) =>
        {
            if (Path.GetFileName(args[^1]).StartsWith("chunk_", StringComparison.Ordinal))
                await Task.Delay(Timeout.Infinite, token);
            return await fake(args, token);
        };
        var clock = Stopwatch.StartNew();
        var d = await new FailureDiagnostics(hanging, FakeFfmpeg, TimeSpan.FromMilliseconds(300)).CollectAsync(request, default);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(DiagnosticsStatus.TimedOut, d.Status);
        Assert.NotNull(d.Fingerprint);
        Assert.NotNull(d.Recipe);
        Assert.Equal(VerdictClass.Undetermined, d.Verdict.Class);
        Assert.Contains("analysis cut short", d.Verdict.Text);
        string body = FailureReportFormatter.Body(Report(d), "12345678");
        Assert.Contains("stopped at the time limit", body);
        Assert.Contains("### Reproduce without the original file", body);
    }

    [Fact]
    public async Task ACancelledAnalysisEndsAtOnce()
    {
        var (request, _) = Scenario(late: []);
        using var stop = new CancellationTokenSource();
        ToolRunner hanging = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return (0, "", "");
        };
        var running = new FailureDiagnostics(hanging, hanging).CollectAsync(request, stop.Token);
        stop.Cancel();
        var d = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DiagnosticsStatus.Cancelled, d.Status);
        Assert.Contains("interrupted", FailureReportFormatter.Body(Report(d), "12345678"));
    }

    [Fact]
    public async Task FailingToolsNeverLeakTheirMessage()
    {
        var (request, _) = Scenario(late: []);
        ToolRunner broken = (_, _) => throw new InvalidOperationException("cannot open " + SecretPath);
        ToolRunner refusing = (_, _) => Task.FromResult((1, "", SecretPath + ": No such file"));
        foreach (var tool in new[] { broken, refusing })
        {
            var d = await new FailureDiagnostics(tool, tool).CollectAsync(request, default);
            Assert.Equal(DiagnosticsStatus.Completed, d.Status);
            Assert.NotEmpty(d.Notes);
            Assert.Null(d.Recipe);
            Assert.Equal(VerdictClass.Undetermined, d.Verdict.Class);
            string body = FailureReportFormatter.Body(Report(d), "12345678");
            foreach (string bad in new[] { "volume1", "House", "No such file" })
                Assert.DoesNotContain(bad, body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("no recipe", body);
        }
    }

    [Fact]
    public async Task AnalysesRunOneAtATime()
    {
        var (request, chunks) = Scenario(late: []);
        var fake = FakeFfprobe(chunks);
        int now = 0, most = 0;
        ToolRunner counting = async (args, token) =>
        {
            most = Math.Max(most, Interlocked.Increment(ref now));
            await Task.Delay(1, token);
            Interlocked.Decrement(ref now);
            return await fake(args, token);
        };
        var diagnostics = new FailureDiagnostics(counting, FakeFfmpeg);
        await Task.WhenAll(diagnostics.CollectAsync(request, default), diagnostics.CollectAsync(request, default));
        Assert.Equal(1, most);
    }

    [Fact]
    public void APinnedSpoolIsDeletedOnlyAfterItsAnalysis()
    {
        var pins = new SpoolPins();
        Assert.False(pins.DeferDelete("free"));
        pins.Pin("a");
        // The next rejection wants the kept spool gone: not while it is analysed.
        Assert.True(pins.DeferDelete("a"));
        Assert.True(pins.Unpin("a"));
        Assert.False(pins.DeferDelete("a"));
        // Nobody asked: the spool stays kept, as the latest rejected one.
        pins.Pin("b");
        Assert.False(pins.Unpin("b"));
    }

    // ---- Size budget -----------------------------------------------------------------------------------------------

    private static DiagnosticsResult Huge()
    {
        var slices = Enumerable.Range(1, 999).Select(i => new SliceMeasure { Number = i, Done = true, Machine = "m", Frames = 2821, Expected = 2821, ConcatMs = 36.7, ListedEndMs = 36.7, JoinMs = 0, ChunkCount = 4, Runs = 1 }).ToList();
        var chunks = Enumerable.Range(0, 4000).Select(i => new ChunkRow(i / 4 + 1, i, i * 30.0, i * 30.0 + 30, 30.0, 123456, new ChunkMeasure(i, 720, i * 30.0, i * 30.0 + 29.9, Frame))).ToList();
        return new DiagnosticsResult
        {
            Verdict = new Verdict(VerdictClass.ListingLate, "frames right, listed late by a uniform +36.7 ms."),
            Recipe = new Recipe("ffmpeg -y synthetic.mkv", []),
            Fingerprint = DiagnosticsAnalysis.ParseFingerprint(FingerprintJson),
            Slices = slices,
            Chunks = chunks,
            ChunksListed = 4000,
            ChunksMeasured = 4000,
            FirstFrames = Enumerable.Range(0, 24).Select(i => new PacketInfo(i * Frame, i * Frame, Frame, 1000, "K__")).ToList(),
            FailingRuns = Enumerable.Range(1, 10).Select(i => new FailingRun("m", i, Enumerable.Repeat("error line with many words " + new string('x', 300), 30).ToList())).ToList(),
            Commands = [new DiagnosticCommand("Run of slice 2", ["-vf", string.Join(",", Enumerable.Repeat("scale=1920:1080", 900))])],
        };
    }

    private static int Count(string text, string part) => (text.Length - text.Replace(part, "").Length) / part.Length;

    [Fact]
    public void AHugeReportStaysUnderGitHubsLimitAndKeepsWhatMatters()
    {
        var report = new FailureReport
        {
            Kind = FailureKind.AssemblyRejected,
            Error = new string('e', 100_000),
            WorkerErrorTail = Enumerable.Repeat(new string('t', 5000), 200).ToList(),
            AssemblySummary = string.Join(" ", Enumerable.Range(1, 999).Select(i => $"{i}:4@{i * 117.659:0.000}/117.696")),
            SliceMachines = Enumerable.Range(0, 999).Select(i => (string?)$"worker {i}").ToList(),
            Diagnostics = Huge(),
        };
        string body = FailureReportFormatter.Body(report, "12345678");
        Assert.True(body.Length <= 65536, $"{body.Length} characters");
        // The first things stay, whole.
        Assert.Contains("**Verdict:** `listing-late`", body);
        Assert.Contains("ffmpeg -y synthetic.mkv", body);
        Assert.Contains("### Slices: listed against measured", body);
        // The raw table goes first and says so; every block stays balanced.
        Assert.Contains("Report size limit reached", body);
        Assert.Equal(Count(body, "<details>"), Count(body, "</details>"));
        Assert.Equal(0, Count(body, "```") % 2);
    }

    [Fact]
    public void ANormalReportIsNotCut()
    {
        var d = Huge() with { Slices = Huge().Slices.Take(30).ToList(), Chunks = Huge().Chunks.Take(120).ToList(), FailingRuns = [], Commands = [] };
        string body = FailureReportFormatter.Body(Report(d), "12345678");
        Assert.DoesNotContain("Report size limit reached", body);
        Assert.Contains("<details><summary>Every chunk", body);
        Assert.Contains("</details>", body);
    }

    [Fact]
    public void BudgetCutsTheLeastImportantFirstAndNamesWhatIsGone()
    {
        var sections = new List<ReportSection>
        {
            ReportSection.Whole(0, "first", new string('a', 500)),
            new(5, "cuttable", "H\n", Enumerable.Repeat(new string('b', 99), 100).ToList(), "T\n"),
            ReportSection.Whole(9, "whole", new string('c', 3000)),
        };
        string text = ReportBudget.Compose(sections, 2500);
        Assert.True(text.Length <= 2500);
        Assert.Contains(new string('a', 500), text);
        Assert.StartsWith("aaa", text);
        Assert.Contains("more line(s) cut", text);
        Assert.Contains("T\n", text);
        Assert.DoesNotContain("ccc", text);
        Assert.Contains("cuttable (cut), whole", text);
    }

    // ---- Privacy -------------------------------------------------------------------------------------------------

    private static void AssertClean(string text)
    {
        foreach (string bad in SecretWords)
            Assert.DoesNotContain(bad, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoTagTitleChapterNameOrPathReachesTheReport()
    {
        var d = await Collect(late: [2, 4]);
        // Without the installation's list of its own strings: the allow-lists alone must hold.
        var report = Report(d, withSensitive: false);
        string body = FailureReportFormatter.Body(report, "12345678");
        AssertClean(body);
        AssertClean(FailureReportFormatter.Title(report, "12345678"));
        // Counted, not named.
        Assert.Contains("2 chapter(s)", body);
        Assert.Contains("hevc (Main)", body);
    }

    [Fact]
    public void OnlyNamedFieldsAreAskedOfFfprobe()
    {
        foreach (var args in new[] { FailureDiagnostics.FingerprintArgs("x"), FailureDiagnostics.SampleArgs("x"), FailureDiagnostics.EndArgs("x", 1), FailureDiagnostics.AllPtsArgs("x"), FailureDiagnostics.BoundaryArgs("x", [5]), FailureDiagnostics.ChunkArgs("x") })
        {
            Assert.Contains("-show_entries", args);
            string entries = args[args.ToList().IndexOf("-show_entries") + 1];
            Assert.DoesNotContain("tags", entries);
            Assert.DoesNotContain("filename", entries);
            Assert.DoesNotContain("title", entries);
            Assert.DoesNotContain("-show_format", args);
            Assert.DoesNotContain("-show_streams", args);
            Assert.DoesNotContain("-show_chapters", args);
        }
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("/volume1/x")]
    [InlineData("Le film de Dimitri, très long, avec des accents é")]
    [InlineData("it's")]
    [InlineData("x\ny")]
    public void TokensRefuseAnythingButCodecWords(string value) => Assert.Null(DiagnosticsAnalysis.Token(value));

    [Theory]
    [InlineData("hevc")]
    [InlineData("Main 10")]
    [InlineData("24000/1001")]
    [InlineData("5.1(side)")]
    [InlineData("matroska,webm")]
    public void TokensKeepCodecWords(string value) => Assert.Equal(value, DiagnosticsAnalysis.Token(value));

    [Fact]
    public void CommandsKeepSettingsAndLoseEverythingElse()
    {
        string line = CommandSanitizer.Line(
        [
            "-y", "-copyts", "-ss", "117.65", "-i", "/volume1/Séries/Rebecca.mkv", "-map", "0:0", "-c:v:0", "libx264", "-preset", "veryfast", "-crf", "20",
            "-x264opts", "keyint=48:bframes=3", "-force_key_frames", "expr:gte(t,n_forced*3)",
            "-vf", "setparams=color_primaries=bt709,scale=trunc(iw/2)*2:trunc(ih/2)*2,subtitles=/volume1/Rebecca.srt:fontsdir=/volume1/fonts,drawtext=text='Rebecca Adler':fontfile=/x/y.ttf,format=yuv420p",
            "-metadata", "title=Dr House", "-metadata:s:a:0", "title=Commentaire", "-attach", "/volume1/secret.ttf",
            "-user_agent", "nas.local", "-map_metadata", "-1", "-segment_list", "{list}", "/volume1/out/Rebecca Adler.mp4",
        ]);
        AssertClean(line);
        foreach (string kept in new[] { "-ss 117.65", "-map 0:0", "-c:v:0 libx264", "-preset veryfast", "-crf 20", "-x264opts keyint=48:bframes=3", "expr:gte(t,n_forced*3)", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "format=yuv420p", "subtitles=<removed>", "drawtext=<removed>", "-metadata <removed>", "-map_metadata -1", "-segment_list {list}", "<output>" })
            Assert.Contains(kept, line);
        Assert.DoesNotContain(".mkv", line);
        Assert.DoesNotContain(".mp4", line);
        Assert.DoesNotContain(".ttf", line);
    }

    [Fact]
    public void AKnownFilterStillLosesAFileOption()
    {
        Assert.Equal("libplacebo=<removed>", CommandSanitizer.Graph("libplacebo=custom_shader_path=/volume1/Rebecca.glsl"));
        Assert.Equal("[0:v]scale=<removed>[v]", CommandSanitizer.Graph("[0:v]scale=/volume1/x[v]"));
        Assert.Equal("[0:v]scale=1280:720[v]", CommandSanitizer.Graph("[0:v]scale=1280:720[v]"));
    }

    [Fact]
    public void FfmpegMetadataDumpsLeaveTheErrorLines()
    {
        var lines = FailureReportFormatter.ErrorLines("ffmpeg failed: Input #0, matroska,webm, from 'x': | Metadata: | title           : Une Chose Sans Forme | ENCODER : Rebecca | Chapter #0:0: start 0 | Error while decoding stream #0:0: Invalid data");
        Assert.Equal(["Error while decoding stream #0:0: Invalid data"], lines);
        string clean = FailureReportFormatter.WithoutMetadata("title : Une Chose Sans Forme | Read error");
        Assert.Equal("Read error", clean);
    }

    [Fact]
    public void VersionsKeepOnlyTheVersionWord()
    {
        Assert.Equal("5.1-emby_2023", DiagnosticsAnalysis.FfmpegVersionToken("ffmpeg version 5.1-emby_2023 Copyright (c) 2000\nconfiguration: --prefix=/volume1/secret"));
        Assert.Equal("7.1-full_build-<host>", DiagnosticsAnalysis.FfmpegVersionToken("ffmpeg version 7.1-full_build-www.gyan.dev Copyright"));
        Assert.Null(DiagnosticsAnalysis.FfmpegVersionToken("something else entirely"));
        Assert.Null(DiagnosticsAnalysis.FfmpegVersionToken(null));
    }

    // ---- With a real ffmpeg, when there is one -----------------------------------------------------------------------

    private static string? OnPath(string name)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (string candidate in new[] { name + ".exe", name })
                if (File.Exists(Path.Combine(dir, candidate)))
                    return Path.Combine(dir, candidate);
        return null;
    }

    private static ToolRunner Real(string exe) => async (args, token) =>
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync(token);
        var error = p.StandardError.ReadToEndAsync(token);
        await p.WaitForExitAsync(token);
        return (p.ExitCode, await output, await error);
    };

    private static async Task Run(string exe, string commandLine, string folder)
    {
        // Process start refused now and then on managed machines ("access denied"): retried, it is not what is tested here.
        var psi = new ProcessStartInfo(exe, commandLine) { WorkingDirectory = folder, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        Process? started = null;
        for (int attempt = 0; started is null; attempt++)
        {
            try
            {
                started = Process.Start(psi)!;
            }
            catch (System.ComponentModel.Win32Exception) when (attempt < 5)
            {
                await Task.Delay(500);
            }
        }
        using var p = started;
        var error = p.StandardError.ReadToEndAsync();
        await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, await error);
    }

    [Fact]
    public async Task RoundTripThroughARealFfmpeg()
    {
        if (OnPath("ffmpeg") is not { } ffmpeg || OnPath("ffprobe") is not { } ffprobe)
            return;
        // A source with titles everywhere, starting at 0.005 s, video 2 s shorter than its audio.
        File.WriteAllText(Path.Combine(spool, "chapters.txt"), ";FFMETADATA1\ntitle=Dr House Rebecca\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=1000\ntitle=Chapitre Rebecca\n");
        await Run(ffmpeg,
            "-y -hide_banner -t 6 -f lavfi -i testsrc2=size=320x160:rate=24000/1001 -t 8 -f lavfi -i anullsrc=r=48000:cl=stereo -i chapters.txt " +
            "-map 0:v -map 1:a -map_metadata 2 -map_chapters 2 -c:v libx264 -preset ultrafast -g 24 -keyint_min 24 -sc_threshold 0 -bf 2 -c:a aac -metadata:s:a:0 title=Commentaire_Dimitri -output_ts_offset 0.005 source.mkv", spool);
        string source = Path.Combine(spool, "source.mkv");
        var request = new DiagnosticsRequest { Kind = FailureKind.GivenBack, Error = "échec", SourcePath = source, SpoolId = "x" };
        var d = await new FailureDiagnostics(Real(ffprobe), Real(ffmpeg)).CollectAsync(request, default);

        Assert.Empty(d.Notes);
        Assert.NotNull(d.ServerFfmpeg);
        Assert.Equal(0.005, d.Fingerprint!.Video!.Start!.Value, 3);
        Assert.Equal(1, d.Fingerprint.Chapters);
        Assert.True(d.Stats!.ConstantRate);
        Assert.Equal(24, d.Stats.GopMedian);
        Assert.Equal(6.005, d.Ends!.Video!.Value, 1);
        string body = FailureReportFormatter.Body(new FailureReport { Kind = FailureKind.GivenBack, Error = "échec", Diagnostics = d }, "12345678");
        AssertClean(body);
        Assert.DoesNotContain(spool, body);
        Assert.DoesNotContain("source.mkv", body);

        // The recipe rebuilds a file with the same timing.
        await Run(ffmpeg, d.Recipe!.Command["ffmpeg ".Length..], spool);
        var again = await new FailureDiagnostics(Real(ffprobe), Real(ffmpeg)).CollectAsync(request with { SourcePath = Path.Combine(spool, "synthetic.mkv") }, default);
        Assert.Equal(d.Fingerprint.Video.Start!.Value, again.Fingerprint!.Video!.Start!.Value, 3);
        Assert.Equal(d.Fingerprint.Start!.Value, again.Fingerprint.Start!.Value, 3);
        Assert.Equal(d.Fingerprint.Video.RFrameRate, again.Fingerprint.Video.RFrameRate);
        Assert.Equal(d.Fingerprint.Video.Codec, again.Fingerprint.Video.Codec);
        Assert.Equal(d.Stats.GopMedian, again.Stats!.GopMedian);
        Assert.Equal(d.Ends.Audio.Values.Max() - d.Ends.Video!.Value, again.Ends!.Audio.Values.Max() - again.Ends.Video!.Value, 0);
    }
}
