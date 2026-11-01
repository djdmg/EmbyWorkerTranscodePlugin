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
}
