using System.Globalization;
using RemoteTranscode.Common;
using RemoteTranscode.Coordinator;

namespace RemoteTranscode.Tests;

/// <summary>Issue #6: a source with frames of 10 to 160 ms and Emby's -r, converted by slices.</summary>
public class VariableRateTests
{
    private const double Tick = 0.04;
    private const double Length = 300;

    // The pattern of the issue's first packets (80, 80, 20, 20, 20, 20 ms), with a frame of 10 ms and one of 160 ms now and then.
    private static List<double> IrregularFrames()
    {
        var times = new List<double>();
        double t = 0;
        for (int n = 0; t < Length; n++)
        {
            times.Add(t);
            t += n % 97 == 0 ? 0.16 : n % 89 == 0 ? 0.01 : n % 6 < 2 ? 0.08 : 0.02;
        }
        return times;
    }

    private static string F(double v) => v.ToString("0.000000", CultureInfo.InvariantCulture);

    // The whole check, on what ffprobe prints for the two files.
    private static Task<SyncVerdict> Check(IReadOnlyList<double> source, IReadOnlyList<double> result)
    {
        var sound = Enumerable.Range(0, (int)(Length / 0.032)).Select(k => k * 0.032).ToList();
        string Json(bool isSource, bool packets)
        {
            var list = new List<string>();
            if (packets)
            {
                var video = isSource ? source : result;
                for (int k = 0; k < video.Count; k++)
                {
                    double length = isSource ? (k + 1 < video.Count ? video[k + 1] - video[k] : 0.04) : Tick;
                    list.Add($$"""{ "stream_index": 0, "pts_time": "{{F(video[k])}}", "dts_time": "{{F(video[k])}}", "duration_time": "{{F(length)}}", "pos": "{{5_000_000 + k}}" }""");
                }
                list.AddRange(sound.Select((t, k) => $$"""{ "stream_index": 1, "pts_time": "{{F(t)}}", "dts_time": "{{F(t)}}", "duration_time": "0.032000", "pos": "{{1000 + k}}" }"""));
            }
            string rates = isSource ? """ "r_frame_rate": "300/1", "avg_frame_rate": "1923675/76948" """ : """ "r_frame_rate": "25/1", "avg_frame_rate": "25/1" """;
            return $$"""
                { "packets": [{{string.Join(",", list)}}],
                  "streams": [ { "index": 0, "codec_type": "video", "codec_name": "h264", {{rates}}, "start_time": "0.000000", "height": 720 },
                               { "index": 1, "codec_type": "audio", "codec_name": "aac", "sample_rate": "48000", "start_time": "0.000000" } ],
                  "format": { "format_name": "mov,mp4", "start_time": "0.000000", "duration": "{{F(Length)}}" } }
                """;
        }
        return SyncCheck.RunAsync(a => a.Any(x => x.StartsWith("frame=", StringComparison.Ordinal))
                ? Task.FromResult("""{ "frames": [ { "best_effort_timestamp_time": "0.000000" } ], "format": { "start_time": "0.000000" } }""")
                : Task.FromResult(Json(a[^1] == "s", a.Contains("-read_intervals") || a.Contains("-select_streams"))),
            "s", "r", new SyncExpectation([1], ["copy"], Tick), [60, 120, 180, 240]);
    }

    private static List<double> Grid(double end) =>
        Enumerable.Range(0, (int)Math.Round(end / Tick)).Select(k => k * Tick).ToList();

    [Fact]
    public async Task The_file_the_filter_gives_passes_the_real_reading()
    {
        var source = IrregularFrames();
        var verdict = await Check(source, Grid(EncodeTask.OnGrid(source[^1] + 0.04, Tick)));
        Assert.Null(verdict.Problem);
    }

    [Fact]
    public async Task Frames_too_many_or_too_few_still_fail_on_a_variable_rate()
    {
        var source = IrregularFrames();
        double end = EncodeTask.OnGrid(source[^1] + 0.04, Tick);
        // The issue's result: 10 frames too long.
        Assert.NotNull((await Check(source, Grid(end + 10 * Tick))).Problem);
        Assert.NotNull((await Check(source, Grid(end - 10 * Tick))).Problem);
        // Right length, but 2 frames more in a slice: all that follows is 80 ms late until a slice 2 frames short brings it back.
        var late = Grid(end).Select(t => t is >= 120 and < 180 ? t + 2 * Tick : t).ToList();
        Assert.Contains("décalée", (await Check(source, late)).Problem);
    }

    [Fact]
    public void Only_irregular_frames_count_as_a_variable_rate()
    {
        Assert.True(ServerMedia.IsVariableRate(IrregularFrames()));
        Assert.False(ServerMedia.IsVariableRate(Grid(100)));
        Assert.False(ServerMedia.IsVariableRate(Enumerable.Range(0, 2000).Select(k => k * 1001 / 24000.0).ToList()));
        // A few holes are not a variable rate, nor the jump from a read window to the next.
        var holes = Grid(100).Where((_, k) => k % 100 != 50).Concat(Grid(10).Select(t => t + 500)).OrderBy(t => t).ToList();
        Assert.False(ServerMedia.IsVariableRate(holes));
        Assert.False(ServerMedia.IsVariableRate([0, 0.01, 0.2]));
    }

    [Fact]
    public void The_filter_ends_every_form_of_emby_s_command()
    {
        string[] Filtered(params string[] a) => EmbyConversionPlanner.WithFrameRateFilter(a)!;
        var vf = Filtered("-i", "x", "-map", "0:0", "-vf", "scale=w=1280:h=720", "-r:v:0", "25");
        Assert.Equal("scale=w=1280:h=720,fps=fps=25", vf[5]);
        Assert.Contains("-r:v:0", vf);
        var complex = Filtered("-i", "x", "-map", "[f2_out0]", "-filter_complex", "[0:0]scale@f1=width=1280:height=720[f2_out0]", "-r:v:0", "24000/1001");
        Assert.Equal("[0:0]scale@f1=width=1280:height=720,fps=fps=24000/1001[f2_out0]", complex[5]);
        var none = Filtered("-i", "x", "-map", "0:0", "-r:v:0", "25", "-crf", "23");
        Assert.Equal(["-i", "x", "-map", "0:0", "-vf", "fps=fps=25", "-r:v:0", "25", "-crf", "23"], none);
        // No rate, or a graph whose end is not the mapped stream: left alone.
        Assert.Null(EmbyConversionPlanner.WithFrameRateFilter(["-i", "x", "-map", "0:0", "-vf", "scale=1"]));
        Assert.Null(EmbyConversionPlanner.WithFrameRateFilter(["-i", "x", "-map", "[a]", "-filter_complex", "[0:0]scale=1[a];[0:1]anull[b]", "-r", "25"]));
    }

    private static EncodeTask TaskOf(params EncodeSlice[] slices) => new()
    {
        Id = "t", Input = "/in.mp4", Output = "/out.mp4", ChunkTemplate = ["-i", "x", "-r:v:0", "25"], FrameRateFiltered = true,
        Requirements = new JobRequirements("libx264", []), Source = new SourceInfo(420, 640, 360, null, 1, 0), Slices = [.. slices],
    };

    [Fact]
    public void Filtered_slices_last_whole_frames_and_meet_on_the_grid()
    {
        // A slice from source frame 220.03 s to source frame 390.02 s: the frames of the grid from 220.04 s up to 390.04 s, halfway rounded up as the filter does.
        var slice = new EncodeSlice { Index = 2, Start = 220.03, End = 390.02 };
        var task = TaskOf(slice);
        int first = slice.FirstChunk;
        task.Chunks.Add(new EncodeChunk(first, 220.03, 250.04, 1));
        task.Chunks.Add(new EncodeChunk(first + 1, 250.04, 390.04, 1));
        var durations = task.ConcatDurations(slice);
        Assert.Equal(30, durations[0].Duration, 6);
        Assert.Equal(140, durations[1].Duration, 6);
        Assert.Equal(390.04, EncodeTask.OnGrid(390.02, Tick), 6);
    }

    [Fact]
    public void A_slice_one_frame_off_the_grid_is_rejected_on_its_count()
    {
        Assert.Null(SliceShiftCheck.GridFramesProblem([(1, 3000, 3000), (2, 2751, 2751)]));
        Assert.Contains("tranche 2 de 2752 images au lieu de 2751", SliceShiftCheck.GridFramesProblem([(1, 3000, 3000), (2, 2752, 2751)]));
        // An unread chunk counts nothing: rejected, not passed.
        Assert.NotNull(SliceShiftCheck.GridFramesProblem([(1, 0, 3000)]));
    }

    [Fact]
    public void Only_a_first_frame_on_the_grid_gets_the_filter()
    {
        string[] template = ["-i", "x", "-r:v:0", "25"];
        Assert.True(EncodeTask.FirstFrameOnGrid(0, template));
        Assert.True(EncodeTask.FirstFrameOnGrid(0.08, template));
        Assert.False(EncodeTask.FirstFrameOnGrid(0.03, template));
        Assert.False(EncodeTask.FirstFrameOnGrid(0, ["-i", "x"]));
    }

    [Fact]
    public void The_filter_is_one_the_workers_accept()
    {
        var chunk = EmbyConversionPlanner.WithFrameRateFilter(["-i", "{input:0}", "-map", "[f2_out0]", "-filter_complex", "[0:0]scale@f1=width=1280:height=720[f2_out0]", "-r:v:0", "25", "-f", "segment", "-segment_list", "{list}", "{out}"])!;
        Assert.True(ArgTemplateValidator.Validate(chunk, out string? error), error);
    }

    [Fact]
    public void A_slice_frames_short_is_not_hidden_by_the_grid()
    {
        var slice = new EncodeSlice { Index = 2, Start = 220.03, End = 390.02 };
        var task = TaskOf(slice);
        // Two frames short: listed as it is, the check sees it.
        task.Chunks.Add(new EncodeChunk(slice.FirstChunk, 220.03, 389.96, 1));
        Assert.Equal(389.96 - 220.04, task.ConcatDurations(slice)[0].Duration, 6);
    }

    [Fact]
    public void A_filtered_last_slice_ending_at_the_video_s_end_is_done_after_runs_ending_there_again()
    {
        var slice = new EncodeSlice { Index = 0, Start = 0, End = 420, Last = true };
        var task = TaskOf(slice);
        task.Chunks.Add(new EncodeChunk(slice.FirstChunk, 0, 300, 1));
        for (int run = 0; run < 3; run++)
        {
            Assert.False(task.EndsAtVideoEnd(slice));
            slice.IdleRuns = task.DropIdleRun(slice) ? slice.IdleRuns + 1 : 0;
            Assert.Single(task.Chunks);
        }
        Assert.True(task.EndsAtVideoEnd(slice));
    }
}
