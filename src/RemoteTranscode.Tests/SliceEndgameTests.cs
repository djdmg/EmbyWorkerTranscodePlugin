using RemoteTranscode.Coordinator;
using static RemoteTranscode.Coordinator.SliceEndgame;

namespace RemoteTranscode.Tests;

/// <summary>Who a waiting slice is not offered to, so a slow machine does not hold the end of a conversion.</summary>
public class SliceEndgameTests
{
    // The end of a long conversion, as EmbyConversionPlanner.Slices cuts it.
    private static readonly double[] Tail = [60, 60, 30, 30];

    private static Machine Free(string id, double speed) => new(id, speed, 0, true, true);
    private static Machine Busy(string id, double speed, double left) => new(id, speed, left, false, false);
    private static Machine Ending(string id, double speed, double left) => new(id, speed, left, false, true);

    [Fact]
    public void Last_slice_goes_to_the_fast_free_machine_first()
    {
        var o = Decide([Free("fast", 4), Free("slow", 0.5)], [30], 30);
        Assert.Equal(["slow"], o.Preferred);
        Assert.Empty(o.Firm);   // offered to it right after, if the fast one does not take the slice
    }

    [Fact]
    public void The_tail_goes_to_the_fast_machine_even_with_more_slices_than_machines()
    {
        // 180 s at 4x: 45 s. One 60 s slice at 0.5x: 120 s, so the slow machine could only delay the end.
        var o = Decide([Free("fast", 4), Free("slow", 0.5)], Tail, 60);
        Assert.Equal(["slow"], o.Preferred);
    }

    [Fact]
    public void Slow_machine_keeps_working_through_the_body()
    {
        double[] body = [.. Enumerable.Repeat(120.0, 20), .. Tail];
        var o = Decide([Free("fast", 4), Free("slow", 0.5)], body, 120);
        Assert.Empty(o.Preferred);
        Assert.Empty(o.Firm);
    }

    [Theory]
    [InlineData(7, false)]   // 840 s + the tail at 4x: 255 s, not clearly before its own 240 s
    [InlineData(4, true)]    // 480 s + the tail at 4x: 165 s, clearly before
    public void The_body_ends_where_the_slow_machine_cannot_help_any_more(int bodySlices, bool passedOver)
    {
        double[] waiting = [.. Enumerable.Repeat(120.0, bodySlices), .. Tail];
        var o = Decide([Free("fast", 4), Free("slow", 0.5)], waiting, 120);
        Assert.Equal(passedOver, o.Preferred.Contains("slow"));
    }

    [Fact]
    public void Close_speeds_are_left_alone()
    {
        var o = Decide([Free("a", 2), Free("b", 1.7)], [60], 60);
        Assert.Empty(o.Preferred);
    }

    [Fact]
    public void Busy_fast_machine_finishing_soon_is_waited_for()
    {
        var o = Decide([Busy("fast", 4, 20), Free("slow", 0.5)], [60], 60);
        Assert.Equal(["slow"], o.Firm);
        Assert.Equal(["slow"], o.Preferred);
    }

    [Fact]
    public void Busy_fast_machine_with_too_much_left_is_not_waited_for()
    {
        // 300 s left at 1x, then 60 s: 360 s. The slower machine is done in 80 s.
        var o = Decide([Busy("faster", 1, 300), Free("other", 0.75)], [60], 60);
        Assert.Empty(o.Firm);
        Assert.Empty(o.Preferred);
    }

    [Fact]
    public void A_fast_machine_that_left_is_no_longer_waited_for()
    {
        // The queue only lists machines still connected: the next pass decides without it.
        var o = Decide([Free("slow", 0.5)], [60], 60);
        Assert.Empty(o.Firm);
        Assert.Empty(o.Preferred);
    }

    [Fact]
    public void Free_fast_machine_is_never_a_reason_to_wait()
    {
        // It may refuse or be paused: the slow one is second in line, never kept away.
        var o = Decide([Free("fast", 4), Free("slow", 0.5), Busy("medium", 1, 500)], [60], 60);
        Assert.Contains("slow", o.Preferred);
        Assert.DoesNotContain("slow", o.Firm);
    }

    [Fact]
    public void Slow_machine_ending_its_slice_does_not_take_the_next_one_from_a_fast_one()
    {
        var o = Decide([Ending("slow", 0.5, 2), Free("fast", 4)], [60], 60);
        Assert.Equal(["slow"], o.Preferred);
        Assert.Empty(o.Firm);
    }

    [Fact]
    public void Fastest_machine_is_never_passed_over()
    {
        var o = Decide([Free("fast", 4), Free("slow", 0.5), Ending("medium", 1, 1)], Tail, 60);
        Assert.DoesNotContain("fast", o.Preferred);
        Assert.DoesNotContain("fast", o.Firm);
    }

    [Fact]
    public void Busy_machines_are_not_candidates()
    {
        var o = Decide([Busy("slow", 0.5, 100), Free("fast", 4)], [30], 30);
        Assert.Empty(o.Preferred);
    }

    [Fact]
    public void Unmeasured_machine_gets_its_first_slice_in_the_body()
    {
        double[] body = [.. Enumerable.Repeat(120.0, 20), .. Tail];
        var o = Decide([Free("known", 4), Free("new", 0)], body, 120);
        Assert.Empty(o.Preferred);
    }

    [Fact]
    public void Unmeasured_machine_goes_after_free_measured_ones_at_the_very_end()
    {
        var o = Decide([Free("known", 0.5), Free("new", 0)], [30], 30);
        Assert.Equal(["new"], o.Preferred);
        Assert.Empty(o.Firm);   // and takes the slice if the measured one does not
    }

    [Fact]
    public void Unmeasured_machine_is_used_when_slices_outnumber_the_free_measured_ones()
    {
        var o = Decide([Free("known", 4), Free("new", 0)], [30, 30], 30);
        Assert.Empty(o.Preferred);
    }

    [Fact]
    public void Unmeasured_machine_is_not_kept_waiting_for_a_busy_one()
    {
        var o = Decide([Busy("fast", 4, 20), Free("new", 0)], [30], 30);
        Assert.Empty(o.Preferred);
        Assert.Empty(o.Firm);
    }

    [Fact]
    public void Without_any_measure_nobody_is_compared()
    {
        var o = Decide([Free("a", 0), Busy("b", 0, 30)], [30, 30], 30);
        Assert.Empty(o.Preferred);
        Assert.Empty(o.Firm);
    }

    [Fact]
    public void Hand_over_skips_a_worker_clearly_slower_than_the_server()
    {
        // The server (not a candidate: already on the slice) has 60 s left at 1x.
        Machine server = new("server", 1, 0, false, false);
        Assert.Equal(["slow"], Decide([server, Free("slow", 0.3)], [60], 60).Firm);
        Assert.Empty(Decide([server, Free("fast", 3)], [60], 60).Firm);
        Assert.Empty(Decide([server, Free("new", 0)], [60], 60).Firm);
    }
}
