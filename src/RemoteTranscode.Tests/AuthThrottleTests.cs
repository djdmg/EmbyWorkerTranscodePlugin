using RemoteTranscode.Common;

namespace RemoteTranscode.Tests;

public class AuthThrottleTests
{
    private DateTimeOffset t = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private AuthThrottle Throttle() => new(() => t) { MaxFailures = 3, Window = TimeSpan.FromMinutes(10), BlockFor = TimeSpan.FromMinutes(15) };

    [Fact]
    public void BlocksAfterTooManyFailuresThenReleases()
    {
        var th = Throttle();
        Assert.False(th.Failure("1.2.3.4"));
        Assert.False(th.Failure("1.2.3.4"));
        Assert.True(th.Failure("1.2.3.4"));
        Assert.True(th.IsBlocked("1.2.3.4"));
        Assert.False(th.IsBlocked("5.6.7.8"));
        t += TimeSpan.FromMinutes(16);
        Assert.False(th.IsBlocked("1.2.3.4"));
    }

    [Fact]
    public void FailuresOutsideTheWindowDoNotAccumulate()
    {
        var th = Throttle();
        th.Failure("a");
        th.Failure("a");
        t += TimeSpan.FromMinutes(11);
        Assert.False(th.Failure("a"));
        Assert.False(th.IsBlocked("a"));
    }

    [Fact]
    public void SuccessClearsFailuresButNotABlock()
    {
        var th = Throttle();
        th.Failure("a");
        th.Failure("a");
        th.Success("a");
        Assert.False(th.Failure("a"));
        th.Failure("a");
        Assert.True(th.Failure("a"));
        th.Success("a");
        Assert.True(th.IsBlocked("a"));
    }

    [Fact]
    public void MemoryStaysBounded()
    {
        var th = new AuthThrottle(() => t) { MaxClients = 100 };
        for (int i = 0; i < 1000; i++)
        {
            t += TimeSpan.FromMilliseconds(10);
            th.Failure("client" + i);
        }
        // Pruning never drops the most recent clients: client999 keeps its first failure and is blocked at the tenth.
        for (int i = 0; i < 9; i++)
            th.Failure("client999");
        Assert.True(th.IsBlocked("client999"));
    }
}
