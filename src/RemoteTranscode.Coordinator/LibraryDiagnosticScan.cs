using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace RemoteTranscode.Coordinator;

/// <summary>Reads a file's description and a few windows of packets with ffprobe, nothing else (no tags: they hold titles).</summary>
public sealed class LibraryDiagnosticProbe(Func<IReadOnlyList<string>, CancellationToken, Task<(int Code, string Output, string Error)>> ffprobe)
{
    /// <summary>Seconds read from the file's start.</summary>
    public const double StartSeconds = 8;
    /// <summary>Windows spread over the file, and their length: a film of 1.5 h is read for under 40 s of its video.</summary>
    public const int MiddleWindows = 5;
    public const double MiddleSeconds = 4;
    /// <summary>Seconds read before the file's end.</summary>
    public const double EndSeconds = 10;
    /// <summary>A file up to this long is read whole.</summary>
    public const double ReadWholeUpTo = 60;
    /// <summary>
    /// Read before the end, video only, when the end window holds no video: a little more than the conversion check
    /// looks at (<see cref="LibraryDiagnostic.CheckEndWindow"/>). Rare, as few files have a video stopping early.
    /// </summary>
    public const double LongEndSeconds = 630;

    private const string PacketFields = "packet=stream_index,pts_time,dts_time,duration_time";

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// The -read_intervals text and the windows it covers. Intervals are in file timestamps and all bounded: ffprobe 9
    /// sometimes prints no packet, without error, for an open-ended one.
    /// </summary>
    public static (string Intervals, List<ProbeWindow> Windows) Plan(double fileStart, double duration)
    {
        if (duration <= ReadWholeUpTo)
        {
            double whole = duration > 0 ? duration + 1 : StartSeconds;
            return ($"%+{F(whole)}", [new(fileStart, fileStart + whole)]);
        }
        var parts = new List<string> { $"%+{F(StartSeconds)}" };
        var windows = new List<ProbeWindow> { new(fileStart, fileStart + StartSeconds) };
        for (int i = 1; i <= MiddleWindows; i++)
        {
            double from = fileStart + duration * i / (MiddleWindows + 1);
            parts.Add($"{F(from)}%{F(from + MiddleSeconds)}");
            windows.Add(new(from, from + MiddleSeconds));
        }
        double endFrom = fileStart + duration - EndSeconds, endTo = fileStart + duration + 3600;
        parts.Add($"{F(endFrom)}%{F(endTo)}");
        windows.Add(new(endFrom, endTo));
        return (string.Join(",", parts), windows);
    }

    /// <summary>Throws with a message holding no path (<see cref="LibraryProbeFailed"/>) when the file cannot be read at all.</summary>
    public async Task<LibraryProbe> ReadAsync(string path, CancellationToken token)
    {
        var (code, output, _) = await ffprobe(["-v", "error", "-print_format", "json", "-show_entries",
            "format=start_time,duration:stream=index,codec_type,start_time,avg_frame_rate,r_frame_rate,field_order:stream_disposition=attached_pic", path], token);
        if (code != 0)
            throw new LibraryProbeFailed($"ffprobe n'a pas pu ouvrir le fichier (code {code})");
        var description = Describe(output);
        if (description.VideoIndex is null)
            return description.Probe;

        double start = description.Probe.FileStart ?? 0, duration = description.Probe.Duration ?? 0;
        var (intervals, windows) = Plan(start, duration);
        var (windowCode, windowOutput, _) = await ffprobe(["-v", "error", "-read_intervals", intervals, "-show_entries", PacketFields, "-print_format", "json", path], token);
        if (windowCode != 0)
        {
            // A seek refused by this file: what can be read without seeking is still worth having.
            (intervals, windows) = ($"%+{F(StartSeconds)}", [new(start, start + StartSeconds)]);
            (windowCode, windowOutput, _) = await ffprobe(["-v", "error", "-read_intervals", intervals, "-show_entries", PacketFields, "-print_format", "json", path], token);
            if (windowCode != 0)
                throw new LibraryProbeFailed($"ffprobe n'a pas pu lire les paquets (code {windowCode})");
        }
        var (video, audio, firstOther) = Packets(windowOutput, description);

        // The audio reaches the file's end and the video is absent from it: it stopped earlier, find out where.
        double endFrom = windows[^1].Start;
        if (windows.Count > 1 && audio.Any(p => p.Time >= endFrom) && !video.Any(p => p.Time >= endFrom))
        {
            double longFrom = Math.Max(start, start + duration - LongEndSeconds);
            var (longCode, longOutput, _) = await ffprobe(["-v", "error", "-select_streams", "V:0", "-read_intervals", $"{F(longFrom)}%{F(start + duration + 3600)}",
                "-show_entries", "packet=pts_time,dts_time,duration_time", "-print_format", "json", path], token);
            if (longCode == 0)
            {
                video.AddRange(ParsePackets(longOutput).Select(p => p.Packet));
                windows[^1] = new(longFrom, windows[^1].End);
            }
        }
        return description.Probe with { Video = video, Audio = audio, FirstOtherPacket = firstOther, Windows = windows };
    }

    private sealed record Description(LibraryProbe Probe, int? VideoIndex, HashSet<int> AudioIndexes);

    private static Description Describe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var format = root.TryGetProperty("format", out var f) ? f : default;
        int? videoIndex = null;
        var audio = new HashSet<int>();
        double? average = null, baseRate = null, videoStart = null, audioStart = null;
        bool interlaced = false;
        if (root.TryGetProperty("streams", out var streams))
            foreach (var s in streams.EnumerateArray())
            {
                int index = s.TryGetProperty("index", out var i) ? i.GetInt32() : -1;
                string? type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                bool cover = s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                if (type == "audio")
                {
                    audio.Add(index);
                    if (Number(Text(s, "start_time")) is { } own && (audioStart is null || own < audioStart))
                        audioStart = own;
                }
                else if (type == "video" && !cover && videoIndex is null)
                {
                    videoIndex = index;
                    average = Rate(Text(s, "avg_frame_rate"));
                    baseRate = Rate(Text(s, "r_frame_rate"));
                    videoStart = Number(Text(s, "start_time"));
                    interlaced = Text(s, "field_order") is "tt" or "bb" or "tb" or "bt";
                }
            }
        return new(new LibraryProbe
        {
            FileStart = format.ValueKind == JsonValueKind.Object ? Number(Text(format, "start_time")) : null,
            Duration = format.ValueKind == JsonValueKind.Object ? Number(Text(format, "duration")) : null,
            AverageFrameRate = average,
            BaseFrameRate = baseRate,
            Interlaced = interlaced,
            VideoStreamStart = videoStart,
            AudioStreamStart = audioStart,
        }, videoIndex, audio);
    }

    private static (List<ProbePacket> Video, List<ProbePacket> Audio, double? FirstOther) Packets(string json, Description description)
    {
        var video = new List<ProbePacket>();
        var audio = new List<ProbePacket>();
        double? firstOther = null;
        foreach (var (stream, packet) in ParsePackets(json))
        {
            if (stream == description.VideoIndex)
                video.Add(packet);
            else if (description.AudioIndexes.Contains(stream))
                audio.Add(packet);
            else if (packet.Time is { } time && (firstOther is null || time < firstOther))
                firstOther = time;
        }
        return (video, audio, firstOther);
    }

    private static List<(int Stream, ProbePacket Packet)> ParsePackets(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<(int, ProbePacket)>();
        if (doc.RootElement.TryGetProperty("packets", out var packets))
            foreach (var p in packets.EnumerateArray())
                list.Add((p.TryGetProperty("stream_index", out var i) ? i.GetInt32() : -1,
                    new ProbePacket(Number(Text(p, "pts_time")), Number(Text(p, "dts_time")), Number(Text(p, "duration_time")))));
        return list;
    }

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Number(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;

    private static double? Rate(string? s) =>
        s?.Split('/') is [var n, var d] && Number(n) is { } num && Number(d) is { } den && num > 0 && den > 0 ? num / den : null;
}

/// <summary>A file could not be analysed. The message holds no path: it is stored and shown.</summary>
public sealed class LibraryProbeFailed(string message) : Exception(message);

/// <summary>What is remembered of a file: enough to skip it while it does not change.</summary>
public sealed class LibraryFileEntry
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public long ModifiedTicks { get; set; }
    /// <summary>Version of the analysis that produced this: a newer one analyses the file again.</summary>
    public int Version { get; set; }
    public DateTimeOffset AnalyzedAt { get; set; }
    public double DurationSeconds { get; set; }
    public double SampledSeconds { get; set; }
    public bool EndRead { get; set; }
    public string? Error { get; set; }
    public List<LibraryFinding> Findings { get; set; } = [];
}

/// <summary>
/// The results, in memory and in a JSON file of the plugin's data folder: paths are personal, so owner-only on Linux,
/// never in a log or a failure report. The data folder is outside everything workers may read (Runtime.Roots).
/// </summary>
public sealed class LibraryDiagnosticStore(string path)
{
    /// <summary>Raise when the thresholds or what is read change: every file is analysed again.</summary>
    public const int AnalysisVersion = 1;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly object gate = new();
    private Dictionary<string, LibraryFileEntry>? entries;

    private Dictionary<string, LibraryFileEntry> Entries
    {
        get
        {
            if (entries != null)
                return entries;
            entries = new(StringComparer.Ordinal);
            try
            {
                if (File.Exists(path))
                    foreach (var e in JsonSerializer.Deserialize<List<LibraryFileEntry>>(File.ReadAllText(path), Json) ?? [])
                        entries[e.Path] = e;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Unreadable results are only results: the next run analyses again.
                entries.Clear();
            }
            return entries;
        }
    }

    /// <summary>True when this file, as it is now, was analysed by this version. A file that failed is tried again.</summary>
    public bool IsCurrent(string file, long size, long modifiedTicks)
    {
        lock (gate)
            return Entries.TryGetValue(file, out var e) && e.Error is null && e.Size == size && e.ModifiedTicks == modifiedTicks && e.Version == AnalysisVersion;
    }

    public void Put(LibraryFileEntry entry)
    {
        lock (gate)
            Entries[entry.Path] = entry;
    }

    public void Remove(string file)
    {
        lock (gate)
            Entries.Remove(file);
    }

    /// <summary>Forgets the files no longer in the library.</summary>
    public void Prune(IReadOnlySet<string> keep)
    {
        lock (gate)
            foreach (string file in Entries.Keys.Where(k => !keep.Contains(k)).ToList())
                Entries.Remove(file);
    }

    public List<LibraryFileEntry> Snapshot()
    {
        lock (gate)
            return [.. Entries.Values];
    }

    /// <summary>Through a temporary file and a rename: a crash never leaves a truncated file. Throws what the disk throws.</summary>
    public void Save()
    {
        string text;
        lock (gate)
            text = JsonSerializer.Serialize(Entries.Values, Json);
        string temp = path + ".tmp";
        File.Delete(temp);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var writer = new StreamWriter(temp, options))
            writer.Write(text);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>Where the running analysis is, for the page. Written by the scanner, read by the API.</summary>
public sealed class LibraryDiagnosticState
{
    public volatile bool Running;
    /// <summary>Why it waits (a playback, a conversion), null when it does not.</summary>
    public volatile string? PausedBecause;
    public int Done;
    public int Total;
    public DateTimeOffset? StartedAt;
    public DateTimeOffset? EndedAt;
    /// <summary>"termine", "annule", "echec"; null before the first run since Emby started.</summary>
    public volatile string? LastOutcome;
}

/// <summary>
/// Goes through the library one file at a time, gently: nothing runs while the server plays or converts, a running probe
/// is stopped as soon as that starts, and it waits between files as long as the last one took. Resumable: unchanged files
/// are skipped, and results are saved along the way. Logs carry counts only, never a path.
/// </summary>
public sealed class LibraryDiagnosticScanner(
    LibraryDiagnosticStore store,
    LibraryDiagnosticState state,
    Func<string, CancellationToken, Task<LibraryProbe>> probe,
    Func<string?> busyReason,
    Action<string> log,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public static readonly TimeSpan BusyPoll = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan BusyPollWhileProbing = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan MinimumPause = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaximumPause = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(1);

    private readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? Task.Delay;

    public async Task RunAsync(IReadOnlyList<string> paths, IProgress<double>? progress, CancellationToken token)
    {
        var files = paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        state.Running = true;
        state.Total = files.Count;
        state.Done = 0;
        state.StartedAt = DateTimeOffset.UtcNow;
        state.EndedAt = null;
        state.LastOutcome = null;
        // An empty list says nothing about the files (Emby still scanning, a volume away): results stay.
        if (files.Count > 0)
            store.Prune(files.ToHashSet(StringComparer.Ordinal));
        int analysed = 0, failed = 0;
        var sinceSave = Stopwatch.StartNew();
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                (bool ran, bool ok) = await AnalyseAsync(files[i], token);
                if (ran)
                {
                    analysed++;
                    if (!ok)
                        failed++;
                }
                state.Done = i + 1;
                progress?.Report(100.0 * (i + 1) / files.Count);
                if (sinceSave.Elapsed >= SaveEvery)
                {
                    TrySave();
                    sinceSave.Restart();
                }
            }
            state.LastOutcome = "termine";
            log($"{analysed} fichier(s) analysé(s) sur {files.Count}, {failed} illisible(s)");
        }
        catch (OperationCanceledException)
        {
            state.LastOutcome = "annule";
            log($"annulé : {analysed} fichier(s) analysé(s), reprise possible");
            throw;
        }
        catch
        {
            state.LastOutcome = "echec";
            throw;
        }
        finally
        {
            TrySave();
            state.PausedBecause = null;
            state.EndedAt = DateTimeOffset.UtcNow;
            state.Running = false;
        }
    }

    private void TrySave()
    {
        try
        {
            store.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The disk's message holds the data folder's path, not a media's; the analysis goes on in memory.
            log($"résultats non enregistrés ({ex.GetType().Name})");
        }
    }

    /// <summary>(a probe ran, it worked).</summary>
    private async Task<(bool Ran, bool Ok)> AnalyseAsync(string file, CancellationToken token)
    {
        if (Directory.Exists(file))
        {
            // A DVD or Blu-ray folder: not one file, and never converted by slices.
            store.Remove(file);
            return (false, true);
        }
        FileInfo info;
        try
        {
            info = new FileInfo(file);
            if (!info.Exists)
            {
                store.Put(new LibraryFileEntry { Path = file, Version = LibraryDiagnosticStore.AnalysisVersion, AnalyzedAt = DateTimeOffset.UtcNow, Error = "Fichier introuvable" });
                return (true, false);
            }
            if (store.IsCurrent(file, info.Length, info.LastWriteTimeUtc.Ticks))
                return (false, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            store.Put(new LibraryFileEntry { Path = file, Version = LibraryDiagnosticStore.AnalysisVersion, AnalyzedAt = DateTimeOffset.UtcNow, Error = "Fichier inaccessible" });
            return (true, false);
        }

        while (true)
        {
            await WaitUntilIdleAsync(token);
            var clock = Stopwatch.StartNew();
            using var running = CancellationTokenSource.CreateLinkedTokenSource(token);
            running.CancelAfter(ProbeTimeout);
            bool interrupted = false;
            var watcher = WatchAsync(() => { interrupted = true; running.Cancel(); }, running.Token);
            var entry = new LibraryFileEntry { Path = file, Size = info.Length, ModifiedTicks = info.LastWriteTimeUtc.Ticks, Version = LibraryDiagnosticStore.AnalysisVersion, AnalyzedAt = DateTimeOffset.UtcNow };
            try
            {
                var read = await probe(file, running.Token);
                var result = LibraryDiagnostic.Classify(read);
                entry.DurationSeconds = read.Duration ?? 0;
                entry.SampledSeconds = result.SampledSeconds;
                entry.EndRead = result.EndRead;
                entry.Findings = [.. result.Findings];
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && interrupted)
            {
                // A playback or a conversion started: same file again once it is over.
                continue;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                entry.Error = "ffprobe n'a pas répondu dans le temps imparti";
            }
            catch (LibraryProbeFailed ex)
            {
                entry.Error = ex.Message;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                // Never the exception's message: it may name the file.
                entry.Error = $"Analyse impossible ({ex.GetType().Name})";
            }
            finally
            {
                running.Cancel();
                await watcher;
            }
            store.Put(entry);
            var pause = clock.Elapsed;
            await wait(pause < MinimumPause ? MinimumPause : pause > MaximumPause ? MaximumPause : pause, token);
            return (true, entry.Error is null);
        }
    }

    private async Task WaitUntilIdleAsync(CancellationToken token)
    {
        while (busyReason() is { } reason)
        {
            state.PausedBecause = reason;
            await wait(BusyPoll, token);
        }
        state.PausedBecause = null;
    }

    private async Task WatchAsync(Action onBusy, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await wait(BusyPollWhileProbing, token);
                if (busyReason() != null)
                {
                    onBusy();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The probe ended first.
        }
    }
}

/// <summary>What GET /RemoteTranscode/LibraryDiagnostic returns: administrators only, as it holds the files' paths.</summary>
public static class LibraryDiagnosticReport
{
    private static readonly string[] CategoryOrder = LibraryDiagnostic.Categories.Select(c => c.Id).ToArray();

    public static object Build(IReadOnlyList<LibraryFileEntry> entries, LibraryDiagnosticState state, bool includeFiles)
    {
        var findingsOf = entries.Select(e => (Entry: e, Findings: e.Error is { } error
            ? new List<LibraryFinding> { new(LibraryDiagnostic.Unreadable, error) }
            : e.Findings)).ToList();
        var analysed = entries.Where(e => e.Error is null).ToList();
        return new
        {
            state = new
            {
                running = state.Running,
                pausedBecause = state.PausedBecause,
                done = state.Done,
                total = state.Total,
                startedAt = state.StartedAt,
                endedAt = state.EndedAt,
                lastOutcome = state.LastOutcome,
            },
            totals = new
            {
                analysed = analysed.Count,
                clean = analysed.Count(e => e.Findings.Count == 0),
                atRisk = analysed.Count(e => e.Findings.Count > 0),
                unreadable = entries.Count - analysed.Count,
                endNotRead = analysed.Count(e => !e.EndRead && e.DurationSeconds > 0),
                sampledSecondsPerFile = analysed.Count > 0 ? Math.Round(analysed.Average(e => e.SampledSeconds), 1) : 0,
            },
            limits = LibraryDiagnostic.Limits,
            categories = LibraryDiagnostic.Categories.Select(c => new
            {
                id = c.Id,
                level = c.Level,
                title = c.Title,
                meaning = c.Meaning,
                consequence = c.Consequence,
                count = findingsOf.Count(f => f.Findings.Any(x => x.Category == c.Id)),
            }),
            files = !includeFiles ? null : findingsOf.Where(f => f.Findings.Count > 0)
                .OrderBy(f => f.Findings.Min(x => Array.IndexOf(CategoryOrder, x.Category)))
                .ThenBy(f => f.Entry.Path, StringComparer.Ordinal)
                .Select(f => new
                {
                    path = f.Entry.Path,
                    durationSeconds = Math.Round(f.Entry.DurationSeconds),
                    findings = f.Findings.Select(x => new { category = x.Category, detail = x.Detail }),
                }),
        };
    }
}
