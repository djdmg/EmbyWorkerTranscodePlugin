namespace RemoteTranscode.Installer;

/// <summary>
/// Retries a file operation while a file stays locked. A process that has just exited can hold its exe for a moment
/// longer, and an antivirus can hold a file it is scanning, so one failed attempt does not mean the file is really in
/// use. The budget counts the time spent waiting, is shared by every <see cref="Run"/> of an instance, and once it is
/// spent the last exception goes to the caller. No Windows dependency: the tests compile this file too.
/// </summary>
internal sealed class LockedFileRetry(TimeSpan budget, TimeSpan delay, Action<TimeSpan>? sleep = null)
{
    private readonly Action<TimeSpan> _sleep = sleep ?? Thread.Sleep;

    public static LockedFileRetry ForInstall() => new(TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(250));

    public TimeSpan Waited { get; private set; }

    public void Run(Action attempt)
    {
        while (true)
        {
            try
            {
                attempt();
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && Waited < budget)
            {
                _sleep(delay);
                Waited += delay;
            }
        }
    }

    /// <summary>Throws if the file exists and cannot be overwritten; a missing file is fine, it will just be created.</summary>
    public static void CheckWritable(string target)
    {
        if (!File.Exists(target))
            return;
        // Exclusive write access is exactly what the overwrite needs (fails on a running exe or a loaded DLL).
        using var _ = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
}
