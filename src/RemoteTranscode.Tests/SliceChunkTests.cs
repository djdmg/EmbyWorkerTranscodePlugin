using RemoteTranscode.Common;
using RemoteTranscode.Coordinator;

namespace RemoteTranscode.Tests;

/// <summary>Chunk times of conversion slices, as listed by the two kinds of runs, and the checks that guard assembly.</summary>
public class SliceChunkTests
{
    private static EncodeTask Task(params EncodeSlice[] slices) => new()
    {
        Id = "t",
        Input = "/in.mkv",
        Output = "/out.mp4",
        ChunkTemplate = ["-copyts", "-i", "{input:0}"],
        Requirements = new JobRequirements("libx264", []),
        Source = new SourceInfo(600, 1920, 1080, null, 1, 0),
        Slices = [.. slices],
    };

    // A slice from 120 to 240 s whose current run starts at 150 s (resume after its first chunk).
    private static (EncodeTask, EncodeSlice) Resumed()
    {
        var slice = new EncodeSlice { Index = 1, Start = 120, End = 240 };
        var task = Task(slice);
        task.Chunks.Add(new EncodeChunk(100, 120, 150, 1));
        slice.RunStartSeconds = 150;
        slice.RunStartIndex = 101;
        return (task, slice);
    }

    [Fact]
    public void Source_time_listing_is_kept()
    {
        // Upstream ffmpeg with -copyts (workers): times in the source, first chunk listed from 0.
        var (task, slice) = Resumed();
        task.AddChunk(slice, 101, 0, 180, 1);
        task.AddChunk(slice, 102, 180, 210, 1);
        Assert.False(slice.RunTimesRelative);
        Assert.Equal(210, task.EncodedEnd(slice));
        Assert.True(task.Consistent(slice));
    }

    [Fact]
    public void Run_relative_listing_is_shifted_to_source_time()
    {
        // Emby's ffmpeg (server's local runs): times from the run's start.
        var (task, slice) = Resumed();
        task.AddChunk(slice, 101, 0, 30, 1);
        task.AddChunk(slice, 102, 30, 60, 1);
        Assert.True(slice.RunTimesRelative);
        Assert.Equal(210, task.EncodedEnd(slice));
        Assert.True(task.Consistent(slice));
    }

    [Fact]
    public void Chunks_going_back_in_time_are_inconsistent()
    {
        // What a misread resume produced: a run starting at the beginning of the film inside slice 18.
        var slice = new EncodeSlice { Index = 1, Start = 120, End = 240 };
        var task = Task(slice);
        task.Chunks.Add(new EncodeChunk(100, 120, 150, 1));
        task.Chunks.Add(new EncodeChunk(101, 0, 30, 1));
        Assert.False(task.Consistent(slice));
        Assert.False(task.Covers(slice));
    }

    [Fact]
    public void A_slice_overrunning_its_end_is_not_covered()
    {
        var slice = new EncodeSlice { Index = 0, Start = 0, End = 113.259 };
        var task = Task(slice, new EncodeSlice { Index = 1, Start = 113.259, End = 600, Last = true });
        task.Chunks.Add(new EncodeChunk(0, 0, 60, 1));
        task.Chunks.Add(new EncodeChunk(1, 60, 119.328, 1));
        Assert.False(task.Covers(slice));
        task.Chunks[1] = new EncodeChunk(1, 60, 113.28, 1);
        Assert.True(task.Covers(slice));
    }
    // Emby's ffmpeg with -start_at_zero on a source whose video starts at 0.005 s: the run's times are 5 ms early.
    private static (EncodeTask, EncodeSlice) ShiftedRun(double shift)
    {
        var slice = new EncodeSlice { Index = 1, Start = 118.118, End = 236.236, RunStartSeconds = 118.118, RunStartIndex = 100 };
        var task = Task(slice);
        task.AddChunk(slice, 100, 118.118 - shift, 148.118 - shift, 1);
        task.AddChunk(slice, 101, 148.118 - shift, 178.118 - shift, 1);
        task.AddChunk(slice, 102, 178.118 - shift, 236.236 - shift, 1);
        return (task, slice);
    }

    [Fact]
    public void Run_shifted_by_the_source_start_is_aligned_on_the_slice_end()
    {
        var (task, slice) = ShiftedRun(0.005);
        task.AlignRunEnd(slice);
        Assert.Equal(236.236, task.EncodedEnd(slice), 6);
        // The concat list's durations add up to the slice's length exactly.
        Assert.Equal(118.118, task.Chunks.Sum(c => c.End - c.Start), 6);
        Assert.Equal(118.118, task.Chunks.Single(c => c.Index == 100).Start, 6);
        Assert.Equal(148.118, task.Chunks.Single(c => c.Index == 100).End, 6);
        Assert.True(task.Consistent(slice));
    }

    [Fact]
    public void Run_short_of_frames_is_not_stretched()
    {
        // 0.1 s short is missing frames, not a listing shift: the times stay as listed.
        var (task, slice) = ShiftedRun(0.1);
        task.AlignRunEnd(slice);
        Assert.Equal(236.136, task.EncodedEnd(slice), 6);
    }

    [Fact]
    public void Last_slice_is_not_aligned()
    {
        var slice = new EncodeSlice { Index = 1, Start = 118.118, End = 236.236, Last = true, RunStartSeconds = 118.118, RunStartIndex = 100 };
        var task = Task(slice);
        task.AddChunk(slice, 100, 118.113, 236.231, 1);
        task.AlignRunEnd(slice);
        Assert.Equal(236.231, task.EncodedEnd(slice), 6);
    }

    // Narcos: Mexico S02E08 (4K Dolby Vision): the file lasts 600 s here, its video stops at 566 s. The last slice's
    // chunks reach 566 s, then every resumed run ends at once without a chunk.
    private static (EncodeTask, EncodeSlice) VideoEndsEarly()
    {
        var slice = new EncodeSlice { Index = 4, Start = 480, End = 600, Last = true };
        var task = Task(slice);
        task.Chunks.Add(new EncodeChunk(400, 480, 510, 1));
        task.Chunks.Add(new EncodeChunk(401, 510, 540, 1));
        task.Chunks.Add(new EncodeChunk(402, 540, 566.108, 1));
        slice.RunStartSeconds = 566.107;
        slice.RunStartIndex = 403;
        return (task, slice);
    }

    [Fact]
    public void Last_slice_ends_where_the_video_ends_after_two_idle_runs()
    {
        var (task, slice) = VideoEndsEarly();
        Assert.False(task.Covers(slice));
        Assert.True(task.DropIdleRun(slice));
        slice.IdleRuns = 1;
        Assert.False(task.EndsAtVideoEnd(slice));
        slice.IdleRuns = 2;
        Assert.True(task.EndsAtVideoEnd(slice));
    }

    [Fact]
    public void A_one_frame_chunk_of_the_last_frame_is_an_idle_run_and_is_dropped()
    {
        // Worker 0.7.15 resumed on the last frame: a chunk of that frame again, every run.
        var (task, slice) = VideoEndsEarly();
        task.Chunks.Add(new EncodeChunk(403, 566.107, 566.149, 1));
        Assert.True(task.DropIdleRun(slice));
        Assert.DoesNotContain(task.Chunks, c => c.Index == 403);
        Assert.Equal(566.108, task.EncodedEnd(slice), 6);
        slice.IdleRuns = 2;
        Assert.True(task.EndsAtVideoEnd(slice));
    }

    [Fact]
    public void A_run_with_new_video_is_not_idle_and_other_slices_never_end_early()
    {
        var (task, slice) = VideoEndsEarly();
        task.Chunks.Add(new EncodeChunk(403, 566.107, 570, 1));
        Assert.False(task.DropIdleRun(slice));
        Assert.Contains(task.Chunks, c => c.Index == 403);

        var middle = new EncodeSlice { Index = 1, Start = 120, End = 240, RunStartSeconds = 150, RunStartIndex = 101, IdleRuns = 5 };
        var t2 = Task(middle);
        t2.Chunks.Add(new EncodeChunk(100, 120, 150, 1));
        Assert.True(t2.DropIdleRun(middle));
        Assert.False(t2.EndsAtVideoEnd(middle));
    }

    [Fact]
    public void A_last_slice_with_no_chunk_or_a_gap_never_ends_early()
    {
        // Nothing encoded yet: the first run itself was idle.
        var fresh = new EncodeSlice { Index = 4, Start = 480, End = 600, Last = true, RunStartSeconds = 480, RunStartIndex = 400, IdleRuns = 3 };
        Assert.False(Task(fresh).EndsAtVideoEnd(fresh));
        // A gap before the resume point.
        var (task, slice) = VideoEndsEarly();
        task.Chunks.RemoveAll(c => c.Index == 401);
        slice.IdleRuns = 2;
        Assert.False(task.EndsAtVideoEnd(slice));
    }

    // A server slice of Mr. Robot S03E09 (AV1, video from 0.007 s) as Emby's ffmpeg cut it: small chunks first, then
    // 30 s ones, every listed end past the next chunk's start, the last one past the slice's end.
    private static (EncodeTask, EncodeSlice) OverlappingRun(double overlap, bool last = false, double end = 357.065)
    {
        var slice = new EncodeSlice { Index = 2, Start = 238.113, End = 357.065, Last = last, RunStartSeconds = 238.113, RunStartIndex = 200 };
        var task = Task(slice);
        double[] starts = [238.113, 238.155, 238.197, 239.239, 240.240, 270.270, 300.300, 330.330];
        for (int i = 0; i < starts.Length; i++)
            task.AddChunk(slice, 200 + i, starts[i], (i + 1 < starts.Length ? starts[i + 1] : end) + overlap, 1);
        return (task, slice);
    }

    [Fact]
    public void Concat_durations_end_each_chunk_at_the_next_ones_start()
    {
        var (task, slice) = OverlappingRun(0.007);
        // Listed as is, the slice would last 8 × 7 ms more than its length: about one frame and a half.
        Assert.Equal(118.952 + 8 * 0.007, task.Chunks.Sum(c => c.End - c.Start), 6);
        var durations = task.ConcatDurations(slice);
        Assert.Equal(8, durations.Count);
        Assert.Equal(0.042, durations[0].Duration, 6);
        Assert.Equal(30.030, durations[5].Duration, 6);
        Assert.Equal(118.952, durations.Sum(d => d.Duration), 6);
    }

    [Fact]
    public void Concat_durations_fill_ends_listed_short()
    {
        // Every end listed 15 ms before the next start, the last one too (still a listing shift, under half a frame).
        var (task, slice) = OverlappingRun(-0.015);
        Assert.Equal(118.952, task.ConcatDurations(slice).Sum(d => d.Duration), 6);
    }

    [Fact]
    public void Concat_durations_do_not_stretch_a_run_short_of_frames()
    {
        // Ends 0.1 s before the slice's end: missing frames, which the check must see.
        var (task, slice) = OverlappingRun(0.007, end: 356.958);
        Assert.Equal(118.852, task.ConcatDurations(slice).Sum(d => d.Duration), 6);
    }

    [Fact]
    public void Concat_durations_of_the_last_slice_keep_its_listed_end()
    {
        var (task, slice) = OverlappingRun(0.007, last: true);
        Assert.Equal(118.959, task.ConcatDurations(slice).Sum(d => d.Duration), 6);
    }

    [Fact]
    public void Concat_durations_agree_with_an_aligned_run()
    {
        var (task, slice) = ShiftedRun(0.005);
        task.AlignRunEnd(slice);
        Assert.Equal(task.Chunks.Select(c => c.End - c.Start), task.ConcatDurations(slice).Select(d => d.Duration), new ToleranceComparer(1e-9));
    }

    [Fact]
    public void Concat_durations_join_two_runs_of_a_slice()
    {
        // First run (server) stopped after its first chunk, listing 7 ms late; the second (worker) resumes at its end.
        var (task, slice) = Resumed();
        task.Chunks[0] = task.Chunks[0] with { End = 150.007 };
        slice.RunStartSeconds = 150.007;
        task.AddChunk(slice, 101, 0, 180, 1);
        task.AddChunk(slice, 102, 180, 240, 1);
        var durations = task.ConcatDurations(slice);
        Assert.Equal([30.007, 29.993, 60], durations.Select(d => Math.Round(d.Duration, 6)));
        Assert.Equal(120, durations.Sum(d => d.Duration), 6);
    }

    [Fact]
    public void Resume_point_is_the_source_frame_nearest_to_the_listed_end()
    {
        // Mr. Robot S03E09: frames at 0.007 s + n / 23.976 (in milliseconds), listed on the n / 23.976 grid, 7 ms early.
        var frames = Enumerable.Range(2600, 10).Select(n => Math.Round(0.007 + n * 1001 / 24000.0, 3)).ToList();
        double listed = 2604 * 1001 / 24000.0;
        Assert.Equal(108.616, ServerMedia.NearestFrame(frames, listed));
        Assert.Equal(108.616, ServerMedia.NearestFrame(frames, listed + 0.014));
        Assert.Equal(frames[0], ServerMedia.NearestFrame(frames, 0));
        Assert.Equal(frames[^1], ServerMedia.NearestFrame(frames, 1000));
        Assert.Null(ServerMedia.NearestFrame([], listed));
    }

    private sealed class ToleranceComparer(double tolerance) : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) <= tolerance;
        public int GetHashCode(double obj) => 0;
    }
}
