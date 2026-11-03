using RemoteTranscode.Common;
using RemoteTranscode.Coordinator;

namespace RemoteTranscode.Tests;

public class SliceShiftCheckTests
{
    private const double Fps25 = 0.04, Fps2398 = 1001.0 / 24000, Fps2997 = 1001.0 / 30000, Fps60 = 1.0 / 60;

    private static double? Shift(double start, double machine, double server, double? first) =>
        SliceShiftCheck.Shift(new RunStart(2, 100, start, machine, server), first);

    // Measured with ffmpeg 9.0.2 (-copyts -start_at_zero, -r, MPEG-TS chunks) on a source whose streams start at 0.021 s, the
    // server reading 0: -ss half a frame below the slice's frame misses it, the same -ss moved by 21 ms keeps it.
    [Theory]
    [InlineData(40.021, 41.4, Fps25, false)]        // the frame asked for
    [InlineData(40.021, 41.44, Fps25, true)]        // one frame late, the same 750 frames counted
    [InlineData(40.028, 41.406633, Fps2997, false)]
    [InlineData(40.028, 41.44, Fps2997, true)]
    [InlineData(40.004, 41.383333, Fps60, false)]
    [InlineData(40.004, 41.4, Fps60, true)]
    [InlineData(40.019, 41.398289, Fps2398, false)]
    public void A_run_late_by_a_frame_is_told_from_a_run_on_its_frame(double start, double first, double frame, bool shifted)
    {
        double shift = Shift(start, 0.021, 0, first)!.Value;
        Assert.Equal(shifted, SliceShiftCheck.IsShifted(shift, frame, null));
    }

    [Fact]
    public void Without_the_file_starts_the_two_runs_look_the_same()
    {
        // +19 and -21 ms: under half a frame both. This is why the run's numbering matters.
        Assert.False(SliceShiftCheck.IsShifted(Shift(40.021, 0, 0, 41.44)!.Value, Fps25, null));
        Assert.False(SliceShiftCheck.IsShifted(Shift(40.021, 0, 0, 41.4)!.Value, Fps25, null));
    }

    // Right frames, measured: the -r grid moves encoded times up to half a frame from the frame's own time, with the same
    // file start on both sides. At a tie the sign changes from one slice to the next (23.976 fps, video at 0.021 s).
    [Theory]
    [InlineData(11.02, 12.44, Fps25)]             // video at 0.020 s: +20 ms
    [InlineData(11.019, 12.4, Fps25)]             // video at 0.019 s: -19 ms
    [InlineData(23.0, 24.420708, Fps2398)]        // +20.7 ms
    [InlineData(37.0, 38.379292, Fps2398)]        // -20.7 ms
    [InlineData(37.03, 38.4217, Fps2997)]         // -8.3 ms
    [InlineData(37.03, 38.43835, Fps60)]          // +8.35 ms
    public void Half_a_frame_of_grid_is_no_shift(double start, double first, double frame)
    {
        double shift = Shift(start, 0, 0, first)!.Value;
        Assert.True(Math.Abs(shift) <= frame / 2 + 0.001);
        Assert.False(SliceShiftCheck.IsShifted(shift, frame, null));
    }

    [Theory]
    [InlineData(Fps2398)]
    [InlineData(Fps25)]
    [InlineData(Fps2997)]
    [InlineData(Fps60)]
    public void The_threshold_lies_between_half_a_frame_and_a_frame(double frame)
    {
        Assert.False(SliceShiftCheck.IsShifted(frame * 0.5 + 0.001, frame, null));
        Assert.False(SliceShiftCheck.IsShifted(-frame * 0.5 - 0.001, frame, null));
        Assert.True(SliceShiftCheck.IsShifted(frame - 0.001, frame, null));
        Assert.True(SliceShiftCheck.IsShifted(-frame + 0.001, frame, null));
    }

    [Fact]
    public void The_frame_set_by_the_command_comes_first_then_the_measured_one_and_without_any_nothing_is_judged()
    {
        Assert.False(SliceShiftCheck.IsShifted(0.025, 0.04, 0.02));
        Assert.True(SliceShiftCheck.IsShifted(0.025, null, 0.02));
        Assert.False(SliceShiftCheck.IsShifted(5, null, null));
        Assert.False(SliceShiftCheck.IsShifted(5, null, 0));
    }

    [Theory]
    [InlineData(100, 1.4)]        // a build counting from its run's start
    [InlineData(100, 200)]        // anything else that is not a source time
    [InlineData(9.9, 1.4)]        // too close to the file's start to tell
    public void Times_that_are_not_source_times_say_nothing(double start, double first) =>
        Assert.Null(Shift(start, 0, 0, first));

    [Fact]
    public void No_measure_says_nothing() => Assert.Null(Shift(100, 0, 0, null));

    [Fact]
    public void The_first_slices_first_run_is_not_kept_it_does_not_seek()
    {
        var slice = new EncodeSlice { Index = 0, Start = 0, End = 120, RunStartIndex = 0, RunStartSeconds = 0 };
        slice.NoteWorkerRun(0.021);
        Assert.Empty(slice.WorkerRuns);
    }

    [Fact]
    public void A_run_is_kept_when_it_starts_on_a_known_frame()
    {
        var slice = new EncodeSlice { Index = 1, Start = 120, End = 240, RunStartIndex = 100, RunStartSeconds = 120 };
        slice.NoteWorkerRun(0.021);
        Assert.Equal((120d, 0.021), slice.WorkerRuns[100]);

        // Resumed from the listed time, no frame found: its first frame is not known.
        slice.RunStartIndex = 102;
        slice.RunStartSeconds = 180.01;
        slice.NoteWorkerRun(0.021);
        Assert.False(slice.WorkerRuns.ContainsKey(102));

        // Resumed from the frame found.
        slice.ResumeFrame = 180.021;
        slice.RunStartSeconds = 180.021;
        slice.NoteWorkerRun(0.021);
        Assert.Equal((180.021, 0.021), slice.WorkerRuns[102]);
    }

    [Fact]
    public void A_new_run_forgets_the_runs_whose_chunks_it_replaces()
    {
        var slice = new EncodeSlice { Index = 1, Start = 120, End = 240, RunStartIndex = 100, RunStartSeconds = 120 };
        slice.NoteWorkerRun(0.021);
        slice.ForgetRunsFrom(101);
        Assert.Contains(100, slice.WorkerRuns.Keys);
        slice.ForgetRunsFrom(100);
        Assert.Empty(slice.WorkerRuns);
    }

    // A TS packet carrying the start of a PES packet with this PTS; with an adaptation field when asked, as a keyframe has.
    private static byte[] PesPacket(double seconds, int adaptation = 0)
    {
        var ts = new byte[188];
        ts[0] = 0x47;
        ts[1] = 0x41;   // payload start, PID 0x100
        ts[2] = 0x00;
        ts[3] = (byte)(adaptation > 0 ? 0x30 : 0x10);
        int p = 4;
        if (adaptation > 0)
        {
            ts[4] = (byte)adaptation;
            p = 5 + adaptation;
        }
        long pts = (long)Math.Round(seconds * 90000);
        byte[] pes = [0, 0, 1, 0xE0, 0, 0, 0x80, 0x80, 5,
            (byte)(0x21 | (pts >> 30 & 7) << 1), (byte)(pts >> 22), (byte)(pts >> 15 << 1 | 1), (byte)(pts >> 7), (byte)(pts << 1 | 1)];
        pes.CopyTo(ts, p);
        return ts;
    }

    // Packets that must not count: the PAT, a video packet going on from the previous one.
    private static byte[] Pat()
    {
        var ts = new byte[188];
        ts[0] = 0x47; ts[1] = 0x40; ts[3] = 0x10; ts[5] = 0; ts[6] = 0xB0;
        return ts;
    }

    private static byte[] Continuation()
    {
        var ts = new byte[188];
        ts[0] = 0x47; ts[1] = 0x01; ts[3] = 0x10;
        return ts;
    }

    [Fact]
    public void The_first_frames_are_read_from_the_chunk_itself()
    {
        // Decoding order with B-frames: the first frame comes first and holds the lowest time.
        byte[][] packets = [Pat(), PesPacket(41.44, 100), Continuation(), PesPacket(41.6), PesPacket(41.52, 7), PesPacket(41.48), PesPacket(41.56), PesPacket(41.76), PesPacket(41.68), PesPacket(41.64), PesPacket(99)];
        using var chunk = new MemoryStream(packets.SelectMany(p => p).ToArray());
        var times = SliceShiftCheck.FirstFrameTimes(chunk);
        Assert.Equal(SliceShiftCheck.Frames, times.Count);
        Assert.Equal(41.44, times[0], 6);
        Assert.DoesNotContain(times, t => t > 90);
        var measure = SliceShiftCheck.MeasureStart(100, times);
        Assert.Equal(41.44, measure.FirstPts!.Value, 6);
        Assert.Equal(0.04, measure.FrameSeconds!.Value, 3);
    }

    [Fact]
    public void Large_times_keep_their_bits()
    {
        using var chunk = new MemoryStream(PesPacket(90000));
        Assert.Equal(90000, SliceShiftCheck.FirstFrameTimes(chunk)[0], 6);
    }

    [Fact]
    public void A_file_that_is_not_mpeg_ts_gives_no_time()
    {
        using var other = new MemoryStream(new byte[4096]);
        Assert.Empty(SliceShiftCheck.FirstFrameTimes(other));
        using var empty = new MemoryStream();
        Assert.Empty(SliceShiftCheck.FirstFrameTimes(empty));
        Assert.Null(SliceShiftCheck.MeasureStart(1, []).FirstPts);
    }

    [Fact]
    public void The_problem_lists_numbers_only_and_nothing_when_no_slice_is_shifted()
    {
        Assert.Null(SliceShiftCheck.Problem([]));
        string problem = SliceShiftCheck.Problem([(3, 0.04), (2, -0.04)])!;
        Assert.StartsWith(SliceShiftCheck.ErrorPrefix, problem);
        Assert.Contains("2 (-40 ms), 3 (+40 ms)", problem);
        // Words the verdict reads otherwise.
        Assert.DoesNotContain("audio", problem);
        Assert.DoesNotContain("durée", problem);
    }

    [Fact]
    public void Frames_of_a_chunk_are_counted_over_the_whole_file()
    {
        static byte[] Packet(bool pes)
        {
            var p = new byte[188];
            p[0] = 0x47;
            if (pes)
            {
                p[1] = 0x40; p[3] = 0x10; p[6] = 1; p[11] = 0x80;
            }
            else
                p[3] = 0x10;
            return p;
        }
        var stream = new MemoryStream(new[] { true, false, false, true, false, true }.SelectMany(Packet).ToArray());
        Assert.Equal(3, SliceShiftCheck.CountFrames(stream));
        Assert.Equal(0, SliceShiftCheck.CountFrames(new MemoryStream(new byte[188])));
    }

    [Fact]
    public void A_seeking_run_that_cannot_be_placed_rejects_the_conversion()
    {
        Assert.True(SliceShiftCheck.MustBeJudged(new RunStart(2, 100, 120, 0, 0)));
        Assert.False(SliceShiftCheck.MustBeJudged(new RunStart(1, 0, 4, 0, 0)));   // near the start the times say nothing
        Assert.Null(SliceShiftCheck.Unread([]));
        string problem = SliceShiftCheck.Unread([3, 2, 3])!;
        Assert.Contains("2, 3", problem);
        Assert.StartsWith(SliceShiftCheck.ErrorPrefix, problem);
    }

    [Fact]
    public void The_verdict_names_the_shift_and_the_disagreement_on_the_file_start()
    {
        string problem = SliceShiftCheck.Problem([(2, 0.04)])!;
        var verdict = DiagnosticsAnalysis.Decide(FailureKind.AssemblyRejected, problem, null, [], null, null, null, true, true);
        Assert.Equal(VerdictClass.ShiftedFrames, verdict.Class);
        var fingerprint = DiagnosticsAnalysis.ParseFingerprint("""{"format":{"start_time":"0.000000"},"streams":[]}""");
        var both = DiagnosticsAnalysis.Decide(FailureKind.AssemblyRejected, problem, null, [], fingerprint, null, null, true, true, new Dictionary<string, double> { ["worker"] = 0.021 });
        Assert.Equal(VerdictClass.FileStartDisagreement, both.Class);
        Assert.Contains("shifted-frames", both.Text);
    }
}
