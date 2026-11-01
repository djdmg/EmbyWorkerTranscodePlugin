using RemoteTranscode.Common;

namespace RemoteTranscode.Tests;

public class FfmpegOutputTests
{
    [Fact]
    public void SegmentCompleteLineRoundTrips()
    {
        string line = EmbyFfmpegOutput.SegmentComplete(603, 1806.594, 1809.599, "abc603.ts") + "\n";
        Assert.True(EmbyFfmpegOutput.TryParseSegmentComplete(line, out int index, out double start, out double end, out string? file));
        Assert.Equal(603, index);
        Assert.Equal(1806.594, start, 3);
        Assert.Equal(1809.599, end, 3);
        Assert.Equal("abc603.ts", file);
    }

    [Fact]
    public void ProgressLineIsNotASegment() =>
        Assert.False(EmbyFfmpegOutput.TryParseSegmentComplete("frame=  100 fps=25 time=00:00:04.00 speed=1.5x", out _, out _, out _, out _));
}
