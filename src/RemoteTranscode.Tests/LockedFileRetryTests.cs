using RemoteTranscode.Installer;

namespace RemoteTranscode.Tests;

public class LockedFileRetryTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(250);

    private readonly List<TimeSpan> _sleeps = [];

    private LockedFileRetry Retry(int seconds = 20) => new(TimeSpan.FromSeconds(seconds), Delay, _sleeps.Add);

    [Fact]
    public void FreeFileIsNotDelayed()
    {
        int attempts = 0;
        var retry = Retry();
        retry.Run(() => attempts++);
        Assert.Equal(1, attempts);
        Assert.Empty(_sleeps);
        Assert.Equal(TimeSpan.Zero, retry.Waited);
    }

    [Fact]
    public void LockReleasedInTimeSucceeds()
    {
        int attempts = 0;
        var retry = Retry();
        retry.Run(() =>
        {
            if (++attempts <= 3)
                throw new IOException("being used by another process");
        });
        Assert.Equal(4, attempts);
        Assert.Equal([Delay, Delay, Delay], _sleeps);
        Assert.Equal(TimeSpan.FromMilliseconds(750), retry.Waited);
    }

    [Fact]
    public void AccessDeniedIsRetriedToo()
    {
        int attempts = 0;
        Retry().Run(() =>
        {
            if (++attempts == 1)
                throw new UnauthorizedAccessException();
        });
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void GivesUpAfterTheBudgetWithTheLastException()
    {
        int attempts = 0;
        var retry = Retry();
        var thrown = Assert.Throws<IOException>(() => retry.Run(() => throw new IOException("attempt " + ++attempts)));
        // 20 s of 250 ms waits, plus the first attempt.
        Assert.Equal(80, _sleeps.Count);
        Assert.Equal(81, attempts);
        Assert.Equal("attempt 81", thrown.Message);
        Assert.Equal(TimeSpan.FromSeconds(20), retry.Waited);
    }

    [Fact]
    public void TheBudgetIsSharedByAllTheFiles()
    {
        var retry = Retry(seconds: 1);
        int attempts = 0;
        retry.Run(() =>
        {
            if (++attempts <= 3)
                throw new IOException();
        });
        // 750 ms used: one wait left for the next file, then it fails instead of waiting another second.
        Assert.Throws<IOException>(() => retry.Run(() => throw new IOException()));
        Assert.Equal(4, _sleeps.Count);
    }

    [Fact]
    public void OtherExceptionsAreNotRetried()
    {
        int attempts = 0;
        Assert.Throws<InvalidDataException>(() => Retry().Run(() =>
        {
            attempts++;
            throw new InvalidDataException();
        }));
        Assert.Equal(1, attempts);
        Assert.Empty(_sleeps);
    }

    [Fact]
    public void MissingFileIsWritable()
    {
        LockedFileRetry.CheckWritable(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void RealLockIsWaitedForUntilReleased()
    {
        string path = Path.GetTempFileName();
        try
        {
            var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var retry = new LockedFileRetry(TimeSpan.FromSeconds(20), Delay, _ => holder.Dispose());
            retry.Run(() => LockedFileRetry.CheckWritable(path));
            Assert.Equal(Delay, retry.Waited);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RealLockNeverReleasedFails()
    {
        string path = Path.GetTempFileName();
        try
        {
            using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Throws<IOException>(() => Retry(seconds: 1).Run(() => LockedFileRetry.CheckWritable(path)));
            Assert.Equal(4, _sleeps.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
