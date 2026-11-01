using System.IO.Compression;
using System.Text.RegularExpressions;

namespace RemoteTranscode.Worker;

/// <summary>
/// Daily log files plus an in-memory tail for the UI. Anything that looks like a bearer token or a long hex secret is
/// masked before it is written, so logs can be exported and shared.
/// </summary>
public sealed partial class Logger
{
    private readonly object gate = new();
    private readonly LinkedList<string> tail = new();
    private const int TailSize = 2000;

    /// <summary>Worker and job logs older than this many days are deleted (at the first line written each day).</summary>
    public const int RetentionDays = 7;
    private DateTime prunedOn;

    /// <summary>Raised on the logging thread (any thread); subscribers marshal to the UI themselves.</summary>
    public event Action<string>? LineWritten;

    [GeneratedRegex(@"(Bearer\s+)[A-Za-z0-9._\-]+|\b[0-9a-fA-F]{40,}\b")]
    private static partial Regex Secrets();

    public static string Mask(string text) => Secrets().Replace(text, m => m.Groups[1].Success ? m.Groups[1].Value + "***" : "***");

    /// <summary>
    /// Thread-safe. The file append happens under the lock so lines from concurrent jobs are not interleaved. Never
    /// throws on I/O: logging is called from job, upload and network loops that must not die because the disk is full
    /// or the file is briefly locked (the line still reaches the in-memory tail).
    /// </summary>
    public void Log(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Mask(message)}";
        lock (gate)
        {
            if (prunedOn != DateTime.Today)
            {
                prunedOn = DateTime.Today;
                Prune(WorkerConfig.LogsDirectory, DateTime.Now.AddDays(-RetentionDays));
            }
            try
            {
                Directory.CreateDirectory(WorkerConfig.LogsDirectory);
                File.AppendAllText(Path.Combine(WorkerConfig.LogsDirectory, $"worker-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            tail.AddLast(line);
            if (tail.Count > TailSize)
                tail.RemoveFirst();
        }
        // In tray mode there is usually no console (WinExe); writing may fail and is not important.
        try { Console.WriteLine(line); } catch (IOException) { }
        // A window being closed on the UI thread may refuse the BeginInvoke: not the caller's problem.
        try { LineWritten?.Invoke(line); } catch (InvalidOperationException) { }
    }

    /// <summary>
    /// Deletes the worker and job logs last written before <paramref name="before"/> (a busy worker writes thousands
    /// of job logs a week). Files in use or locked are skipped; never throws.
    /// </summary>
    public static int Prune(string directory, DateTime before)
    {
        int deleted = 0;
        try
        {
            if (!Directory.Exists(directory))
                return 0;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.log")
                .Where(f => (f.Name.StartsWith("job-", StringComparison.OrdinalIgnoreCase) || f.Name.StartsWith("worker-", StringComparison.OrdinalIgnoreCase))
                    && f.LastWriteTime < before))
            {
                try
                {
                    file.Delete();
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return deleted;
    }

    public string[] Tail()
    {
        lock (gate)
            return tail.ToArray();
    }

    /// <summary>Zips the worker and job logs of the last <paramref name="days"/> days (already masked).</summary>
    public static void Export(string zipPath, int days = 7)
    {
        if (File.Exists(zipPath))
            File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var since = DateTime.Now.AddDays(-days);
        foreach (var file in new DirectoryInfo(WorkerConfig.LogsDirectory).EnumerateFiles("*.log").Where(f => f.LastWriteTime >= since))
        {
            // Files may be open for writing: copy through a shared read.
            using var src = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = zip.CreateEntry(file.Name).Open();
            src.CopyTo(dst);
        }
    }
}
