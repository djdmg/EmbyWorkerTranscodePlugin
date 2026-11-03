using System.Text.Json;
using RemoteTranscode.Coordinator;

namespace RemoteTranscode.Tests;

/// <summary>Risks of a file for a slice-based conversion, from synthetic numbers; and the gentle, resumable scan around it.</summary>
public class LibraryDiagnosticTests
{
    private const double Fps2398 = 24000.0 / 1001;
    private const double Length = 3600;

    /// <summary>The windows the probe reads on a 1 h file starting at 0.</summary>
    private static List<ProbeWindow> Windows() => LibraryDiagnosticProbe.Plan(0, Length).Windows;

    /// <summary>A frame's time as a container stores it: to the millisecond in MKV (no rounding when null).</summary>
    private static double Stored(double time, bool milliseconds) => milliseconds ? Math.Round(time * 1000) / 1000 : time;

    /// <summary>
    /// Regular frames in each window, in decoding order: with <paramref name="bFrames"/>, as an IBBP pattern whose frames
    /// come out shown later than decoded. The file's last frame is at <paramref name="videoEnd"/>.
    /// </summary>
    private static List<ProbePacket> Frames(double fps, IEnumerable<ProbeWindow> windows, double start = 0, double videoEnd = Length,
        bool milliseconds = true, bool bFrames = false, Func<double, double>? shift = null)
    {
        var packets = new List<ProbePacket>();
        double frame = 1 / fps;
        foreach (var w in windows)
        {
            var times = new List<double>();
            for (long n = (long)Math.Ceiling(Math.Max(0, w.Start - start) * fps); start + n * frame < Math.Min(w.End, videoEnd); n++)
                times.Add(start + n * frame);
            if (bFrames)
                for (int i = 0; i + 2 < times.Count; i += 3)
                    (times[i], times[i + 1], times[i + 2]) = (times[i + 2], times[i], times[i + 1]);
            packets.AddRange(times.Select(t => new ProbePacket(Stored(shift?.Invoke(t) ?? t, milliseconds), Stored(t, milliseconds), frame)));
        }
        return packets;
    }

    private static List<ProbePacket> AudioPackets(IEnumerable<ProbeWindow> windows, double start = 0, double audioEnd = Length)
    {
        const double Frame = 0.032;
        var packets = new List<ProbePacket>();
        foreach (var w in windows)
            for (long n = (long)Math.Ceiling(Math.Max(0, w.Start - start) / Frame); start + n * Frame < Math.Min(w.End, audioEnd); n++)
                packets.Add(new(Stored(start + n * Frame, true), Stored(start + n * Frame, true), Frame));
        return packets;
    }

    private static LibraryProbe Probe(double fps = Fps2398, double? storedRate = null, double fileStart = 0, double videoStart = 0, double audioStart = 0,
        double videoEnd = Length, double audioEnd = Length, bool milliseconds = true, bool bFrames = false, Func<double, double>? shift = null, bool interlaced = false)
    {
        var windows = Windows();
        return new LibraryProbe
        {
            FileStart = fileStart,
            Duration = Length,
            AverageFrameRate = storedRate ?? fps,
            BaseFrameRate = storedRate ?? fps,
            Interlaced = interlaced,
            Video = Frames(fps, windows, videoStart, videoEnd, milliseconds, bFrames, shift),
            Audio = AudioPackets(windows, audioStart, audioEnd),
            Windows = windows,
        };
    }

    private static List<string> Categories(LibraryProbe probe) => LibraryDiagnostic.Classify(probe).Findings.Select(f => f.Category).ToList();

    // ---- A clean file: no false finding -----------------------------------------------------------------------

    [Theory]
    [InlineData(Fps2398, true, false)]  // 23.976 in MKV: gaps alternate 41 and 42 ms
    [InlineData(Fps2398, true, true)]   // the same with B-frames: decoding order is not presentation order
    [InlineData(25.0, false, true)]
    [InlineData(29.97, true, true)]
    [InlineData(59.94, true, false)]    // gaps alternate 16 and 17 ms
    [InlineData(119.88, true, false)]   // gaps alternate 8 and 9 ms
    public void A_clean_file_gives_no_finding(double fps, bool milliseconds, bool bFrames)
    {
        var result = LibraryDiagnostic.Classify(Probe(fps: fps, milliseconds: milliseconds, bFrames: bFrames));
        Assert.Empty(result.Findings);
        Assert.True(result.EndRead);
        Assert.InRange(result.SampledSeconds, 30, 50);
    }

    [Fact]
    public void The_distance_between_two_windows_is_not_a_hole()
    {
        var windows = Windows();
        Assert.True(windows.Count >= 7);
        Assert.DoesNotContain(LibraryDiagnostic.TimestampHole, Categories(Probe()));
    }

    [Fact]
    public void Frames_still_to_come_at_a_window_edge_are_not_a_hole()
    {
        // The window stops on a P frame: the B-frames shown before it were not read. Its frames then jump by 3.
        var probe = Probe(bFrames: true);
        var cut = new List<ProbePacket>();
        var middle = probe.Windows[1];
        double frame = 1 / Fps2398;
        cut.AddRange(probe.Video.Where(p => p.Pts < middle.End - 20 * frame || p.Pts > middle.End));
        cut.Add(new(middle.End - 18 * frame));
        cut.Add(new(middle.End - 15 * frame));
        Assert.DoesNotContain(LibraryDiagnostic.TimestampHole, Categories(probe with { Video = cut }));
    }

    [Fact]
    public void What_a_seek_reads_before_a_window_is_not_a_hole()
    {
        // A 10 s GOP: the seek to window 2 lands 1 s after window 1's end. Those frames are read, then nothing up to window 1.
        var probe = Probe();
        var windows = probe.Windows;
        var leadIn = Frames(Fps2398, [new(windows[1].End + 1, windows[1].End + 2)]);
        Assert.Empty(Categories(probe with { Video = probe.Video.Concat(leadIn).ToList() }));
    }

    [Fact]
    public void A_short_file_read_whole_gives_no_finding()
    {
        var windows = LibraryDiagnosticProbe.Plan(0, 30).Windows;
        var result = LibraryDiagnostic.Classify(new LibraryProbe
        {
            FileStart = 0,
            Duration = 30,
            AverageFrameRate = 25,
            Video = Frames(25, windows, videoEnd: 30),
            Audio = AudioPackets(windows, audioEnd: 30),
            Windows = windows,
        });
        Assert.Empty(result.Findings);
        Assert.Equal(30, result.SampledSeconds, 0);
    }

    // ---- Late start --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.079)]
    [InlineData(0.021)]
    public void A_video_starting_after_its_audio_is_found(double start)
    {
        var finding = Assert.Single(LibraryDiagnostic.Classify(Probe(videoStart: start)).Findings);
        Assert.Equal(LibraryDiagnostic.LateVideoStart, finding.Category);
        Assert.Contains($"{start * 1000:0} ms", finding.Detail);
    }

    [Fact]
    public void Discarded_samples_at_the_start_of_the_audio_are_not_a_late_video()
    {
        // The first AAC packet stored 21 ms before the audio's start: as ffmpeg itself writes it.
        var probe = Probe();
        var audio = probe.Audio.Prepend(new ProbePacket(-0.021, -0.021, 0.021)).ToList();
        Assert.Empty(Categories(probe with { Audio = audio, AudioStreamStart = 0 }));
        // And a real delay still shows, with the same kind of audio.
        Assert.Equal([LibraryDiagnostic.LateVideoStart], Categories(Probe(videoStart: 0.079) with { Audio = audio, AudioStreamStart = 0 }));
    }

    [Fact]
    public void A_stream_start_read_as_0_does_not_hide_where_the_packets_are() =>
        Assert.Equal([LibraryDiagnostic.FileStartDisagreement], Categories(Probe(fileStart: 0, videoStart: 0.021, audioStart: 0.021) with { AudioStreamStart = 0, VideoStreamStart = 0 }));

    [Fact]
    public void Millisecond_rounding_is_not_a_late_start() =>
        Assert.Empty(Categories(Probe(videoStart: 0.002)));

    [Fact]
    public void An_audio_starting_later_is_not_a_late_video() =>
        Assert.DoesNotContain(LibraryDiagnostic.LateVideoStart, Categories(Probe(audioStart: 0.5)));

    // ---- File start read differently ---------------------------------------------------------------------------

    [Fact]
    public void A_file_start_before_every_packet_is_found_without_a_late_video()
    {
        // As Emby's ffprobe 5.1 reads a whole file offset by 21 ms; the workers' ffmpeg 9 reads 0.021 s.
        var finding = Assert.Single(LibraryDiagnostic.Classify(Probe(fileStart: 0, videoStart: 0.021, audioStart: 0.021)).Findings);
        Assert.Equal(LibraryDiagnostic.FileStartDisagreement, finding.Category);
        Assert.Contains("0,021 s", finding.Detail);
    }

    [Fact]
    public void A_nonzero_start_read_alike_is_not_a_disagreement() =>
        Assert.Empty(Categories(Probe(fileStart: 1.4, videoStart: 1.4, audioStart: 1.4)));

    [Fact]
    public void A_subtitle_starting_first_explains_the_file_start()
    {
        var probe = Probe(fileStart: 0, videoStart: 0.3, audioStart: 0.3) with { FirstOtherPacket = 0 };
        Assert.Empty(Categories(probe));
    }

    // ---- Holes -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_gap_of_85_ms_is_a_hole()
    {
        double at = Windows()[2].Start + 2;
        var finding = Assert.Single(LibraryDiagnostic.Classify(Probe(shift: t => t > at ? t + 0.043 : t)).Findings);
        Assert.Equal(LibraryDiagnostic.TimestampHole, finding.Category);
        Assert.Contains("85 ms", finding.Detail);
    }

    [Fact]
    public void A_hole_is_found_through_b_frames_too()
    {
        double at = Windows()[3].Start + 2;
        Assert.Equal([LibraryDiagnostic.TimestampHole], Categories(Probe(bFrames: true, shift: t => t > at ? t + 0.043 : t)));
    }

    [Fact]
    public void A_hole_outside_the_windows_is_not_seen()
    {
        // Between two windows: documented, a sampling sees nothing there (the windows are shifted alike).
        double at = Windows()[2].End + 60;
        Assert.Empty(Categories(Probe(shift: t => t > at ? t + 0.043 : t)));
    }

    [Fact]
    public void Interlaced_field_and_frame_packets_are_not_holes()
    {
        var windows = Windows();
        var video = new List<ProbePacket>();
        foreach (var w in windows.Take(windows.Count - 1))
            for (int i = 0; i < w.End - w.Start; i++)
            {
                // One second of field packets (20 ms), then 10 frame packets (40 ms) in place of the last 20.
                for (int n = 0; n < 30; n++)
                    video.Add(new(w.Start + i + n * 0.02));
                for (int n = 0; n < 10; n++)
                    video.Add(new(w.Start + i + 0.6 + n * 0.04));
            }
        var probe = Probe(interlaced: true, storedRate: 25) with { Video = video, Windows = windows };
        Assert.DoesNotContain(LibraryDiagnostic.TimestampHole, Categories(probe));
        Assert.DoesNotContain(LibraryDiagnostic.VariableFrameRate, Categories(probe));
    }

    // ---- Variable frame rate -----------------------------------------------------------------------------------

    [Fact]
    public void A_variable_frame_rate_is_found_and_its_gaps_are_not_holes()
    {
        var windows = Windows();
        var video = new List<ProbePacket>();
        foreach (var w in windows)
        {
            double t = w.Start, end = Math.Min(w.End, Length);
            for (int n = 0; t < end; n++)
            {
                video.Add(new(Stored(t, true)));
                t += n % 3 == 0 ? 0.100 : n % 3 == 1 ? 0.033 : 0.050;
            }
        }
        var categories = Categories(Probe(storedRate: 18) with { Video = video });
        Assert.Contains(LibraryDiagnostic.VariableFrameRate, categories);
        Assert.DoesNotContain(LibraryDiagnostic.TimestampHole, categories);
        Assert.DoesNotContain(LibraryDiagnostic.StoredRateMismatch, categories);
    }

    [Fact]
    public void A_long_stop_in_a_variable_frame_rate_is_still_a_hole()
    {
        var windows = Windows();
        var video = new List<ProbePacket>();
        foreach (var w in windows)
        {
            double t = w.Start, end = Math.Min(w.End, Length);
            for (int n = 0; t < end; n++)
            {
                video.Add(new(Stored(t, true)));
                t += n % 3 == 0 ? 0.060 : 0.033;
                if (n == 40 && w == windows[1])
                    t += 1;
            }
        }
        var categories = Categories(Probe(storedRate: 24) with { Video = video });
        Assert.Contains(LibraryDiagnostic.VariableFrameRate, categories);
        Assert.Contains(LibraryDiagnostic.TimestampHole, categories);
    }

    // ---- Stored frame rate -------------------------------------------------------------------------------------

    [Fact]
    public void A_stored_rate_far_from_the_real_one_is_found()
    {
        var finding = Assert.Single(LibraryDiagnostic.Classify(Probe(fps: 25, storedRate: Fps2398)).Findings);
        Assert.Equal(LibraryDiagnostic.StoredRateMismatch, finding.Category);
        Assert.Contains("23,976", finding.Detail);
    }

    [Theory]
    [InlineData(24.0)]     // real 23.976: 0.1 %, not a defect of any consequence here
    [InlineData(23.976)]
    public void A_rate_close_to_the_real_one_is_not_a_mismatch(double stored) =>
        Assert.Empty(Categories(Probe(storedRate: stored)));

    [Fact]
    public void An_interlaced_file_with_one_packet_per_field_is_not_a_mismatch() =>
        Assert.Empty(Categories(Probe(fps: 50, storedRate: 25, interlaced: true)));

    // ---- Packets without presentation time ---------------------------------------------------------------------

    [Fact]
    public void Packets_without_presentation_time_are_found_and_their_absence_is_not_a_hole()
    {
        var probe = Probe(fps: 25);
        // Every other packet, as in an AVI with packed B-frames: decoding times stay regular.
        var video = probe.Video.Select((p, i) => i % 2 == 0 ? p : p with { Pts = null }).ToList();
        var result = LibraryDiagnostic.Classify(probe with { Video = video });
        var finding = Assert.Single(result.Findings);
        Assert.Equal(LibraryDiagnostic.NoPresentationTimes, finding.Category);
        Assert.Contains($"{video.Count(p => p.Pts is null)} paquets", finding.Detail);
    }

    // ---- Video shorter than its audio --------------------------------------------------------------------------

    [Fact]
    public void A_video_stopping_before_the_audio_in_the_last_window_is_measured()
    {
        var finding = Assert.Single(LibraryDiagnostic.Classify(Probe(videoEnd: Length - 5)).Findings);
        Assert.Equal(LibraryDiagnostic.VideoShorterThanAudio, finding.Category);
        Assert.Contains("La vidéo s'arrête 5,0 s avant", finding.Detail);
    }

    [Fact]
    public void A_video_absent_from_the_last_window_is_at_least_that_much_shorter()
    {
        var finding = Assert.Single(LibraryDiagnostic.Classify(Probe(videoEnd: 3000)).Findings);
        Assert.Equal(LibraryDiagnostic.VideoShorterThanAudio, finding.Category);
        Assert.Contains("au moins 10,0 s", finding.Detail);
    }

    [Fact]
    public void A_video_shorter_than_what_the_check_reads_says_so()
    {
        var windows = Windows();
        windows[^1] = new(Length - LibraryDiagnosticProbe.LongEndSeconds, windows[^1].End);
        var probe = Probe(videoEnd: 2000) with { Windows = windows, Audio = AudioPackets(windows) };
        var finding = Assert.Single(LibraryDiagnostic.Classify(probe).Findings);
        Assert.Contains("au moins 11 min", finding.Detail);
        Assert.Contains("le contrôle final", finding.Detail);
    }

    [Fact]
    public void Streams_ending_within_a_second_end_together() =>
        Assert.Empty(Categories(Probe(videoEnd: Length - 0.5)));

    [Fact]
    public void An_end_that_could_not_be_read_says_nothing_about_the_end()
    {
        var probe = Probe();
        var result = LibraryDiagnostic.Classify(probe with
        {
            Video = probe.Video.Where(p => p.Pts < Length - 60).ToList(),
            Audio = probe.Audio.Where(p => p.Pts < Length - 60).ToList(),
        });
        Assert.Empty(result.Findings);
        Assert.False(result.EndRead);
    }

    // ---- Reading ffprobe's output --------------------------------------------------------------------------------

    private const string Description = """
        {"streams":[
          {"index":0,"codec_type":"video","field_order":"progressive","avg_frame_rate":"25/1","r_frame_rate":"25/1","start_time":"0.000000","disposition":{"attached_pic":0}},
          {"index":1,"codec_type":"audio","avg_frame_rate":"0/0","r_frame_rate":"0/0","start_time":"0.000000"},
          {"index":2,"codec_type":"video","avg_frame_rate":"0/0","r_frame_rate":"90000/1","disposition":{"attached_pic":1}},
          {"index":3,"codec_type":"subtitle"}],
         "format":{"start_time":"0.000000","duration":"3600.000000"}}
        """;

    private static string PacketsJson(IEnumerable<(int Stream, double Time)> packets) =>
        JsonSerializer.Serialize(new
        {
            packets = packets.Select(p => new Dictionary<string, object>
            {
                ["stream_index"] = p.Stream,
                ["pts_time"] = p.Time.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture),
                ["dts_time"] = p.Time.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture),
            }),
        });

    [Fact]
    public async Task The_probe_reads_only_windows_and_sorts_packets_by_stream()
    {
        var calls = new List<IReadOnlyList<string>>();
        var probe = new LibraryDiagnosticProbe((args, _) =>
        {
            calls.Add(args);
            string output = !args.Contains("-read_intervals") ? Description
                : PacketsJson([(0, 0.04), (1, 0), (2, 0), (3, 0.01), (0, Length - 1), (1, Length - 1)]);
            return Task.FromResult((0, output, ""));
        });
        var read = await probe.ReadAsync("/media/sample.mkv", CancellationToken.None);
        Assert.Equal(25, read.AverageFrameRate);
        Assert.Equal([0.04, Length - 1], read.Video.Select(p => p.Pts));
        Assert.Equal(2, read.Audio.Count);
        Assert.Equal(0, read.FirstOtherPacket); // the cover art and the subtitle count as other streams
        Assert.Equal(2, calls.Count);
        // No tags (they hold titles) and no decoding option.
        Assert.DoesNotContain(calls.SelectMany(c => c), a => a.Contains("tags") || a == "-show_frames");
        Assert.All(calls[1][calls[1].ToList().IndexOf("-read_intervals") + 1].Split(','), part => Assert.Contains('%', part));
    }

    [Fact]
    public async Task A_video_absent_from_the_end_window_is_looked_for_further_back()
    {
        var calls = new List<IReadOnlyList<string>>();
        var probe = new LibraryDiagnosticProbe((args, _) =>
        {
            calls.Add(args);
            string output = !args.Contains("-read_intervals") ? Description
                : args.Contains("-select_streams") ? PacketsJson([(0, Length - 34)])
                : PacketsJson([(0, 10), (1, 0), (1, Length - 1)]);
            return Task.FromResult((0, output, ""));
        });
        var read = await probe.ReadAsync("/media/sample.mkv", CancellationToken.None);
        Assert.Equal(3, calls.Count);
        Assert.Equal(Length - LibraryDiagnosticProbe.LongEndSeconds, read.Windows[^1].Start);
        Assert.Contains(read.Video, p => p.Pts == Length - 34);
    }

    [Fact]
    public async Task A_refused_seek_still_reads_the_start()
    {
        var probe = new LibraryDiagnosticProbe((args, _) =>
        {
            if (!args.Contains("-read_intervals"))
                return Task.FromResult((0, Description, ""));
            string intervals = args[args.ToList().IndexOf("-read_intervals") + 1];
            return Task.FromResult(intervals.Contains(',') ? (1, "", "seek error") : (0, PacketsJson([(0, 0), (1, 0)]), ""));
        });
        var read = await probe.ReadAsync("/media/sample.mkv", CancellationToken.None);
        Assert.Single(read.Windows);
        Assert.Single(read.Video);
    }

    [Fact]
    public async Task A_failure_message_holds_no_path()
    {
        var probe = new LibraryDiagnosticProbe((_, _) => Task.FromResult((1, "", "/media/private/sample.mkv: Invalid data")));
        var ex = await Assert.ThrowsAsync<LibraryProbeFailed>(() => probe.ReadAsync("/media/private/sample.mkv", CancellationToken.None));
        Assert.DoesNotContain("private", ex.Message);
    }

    // ---- The scan ------------------------------------------------------------------------------------------------

    private sealed class TempFolder : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("library-diagnostic-").FullName;

        public string File(string name)
        {
            string path = Path.Combine(Root, name);
            System.IO.File.WriteAllText(path, "x");
            return path;
        }

        public string Results => Path.Combine(Root, "results.json");

        public void Dispose() => Directory.Delete(Root, true);
    }

    private static LibraryProbe CleanProbe() => Probe();

    private static Task NoWait(TimeSpan _, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.Delay(1, token);
    }

    private static LibraryDiagnosticScanner Scanner(LibraryDiagnosticStore store, Func<string, CancellationToken, Task<LibraryProbe>> probe, Func<string?>? busy = null, LibraryDiagnosticState? state = null, List<string>? log = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(store, state ?? new LibraryDiagnosticState(), probe, busy ?? (() => null), m => log?.Add(m), delay ?? NoWait);

    [Fact]
    public async Task Unchanged_files_are_skipped_on_the_next_run_and_a_changed_one_is_read_again()
    {
        using var folder = new TempFolder();
        string a = folder.File("a.mkv"), b = folder.File("b.mkv");
        var read = new List<string>();
        Task<LibraryProbe> Read(string path, CancellationToken _) { read.Add(path); return Task.FromResult(CleanProbe()); }

        await Scanner(new LibraryDiagnosticStore(folder.Results), Read).RunAsync([a, b], null, CancellationToken.None);
        Assert.Equal([a, b], read);

        // A new store: what counts is the file on disk.
        read.Clear();
        await Scanner(new LibraryDiagnosticStore(folder.Results), Read).RunAsync([a, b], null, CancellationToken.None);
        Assert.Empty(read);

        File.AppendAllText(b, "more");
        await Scanner(new LibraryDiagnosticStore(folder.Results), Read).RunAsync([a, b], null, CancellationToken.None);
        Assert.Equal([b], read);

        read.Clear();
        File.SetLastWriteTimeUtc(a, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await Scanner(new LibraryDiagnosticStore(folder.Results), Read).RunAsync([a, b], null, CancellationToken.None);
        Assert.Equal([a], read);
    }

    [Fact]
    public async Task A_cancelled_run_keeps_what_it_analysed_and_the_next_one_resumes_after_it()
    {
        using var folder = new TempFolder();
        string a = folder.File("a.mkv"), b = folder.File("b.mkv");
        using var cancel = new CancellationTokenSource();
        var state = new LibraryDiagnosticState();
        Task<LibraryProbe> Stop(string path, CancellationToken _)
        {
            cancel.Cancel();
            return Task.FromResult(CleanProbe());
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Scanner(new LibraryDiagnosticStore(folder.Results), Stop, state: state).RunAsync([a, b], null, cancel.Token));
        Assert.Equal("annule", state.LastOutcome);
        Assert.False(state.Running);

        var read = new List<string>();
        Task<LibraryProbe> Read(string path, CancellationToken _) { read.Add(path); return Task.FromResult(CleanProbe()); }
        await Scanner(new LibraryDiagnosticStore(folder.Results), Read).RunAsync([a, b], null, CancellationToken.None);
        Assert.Equal([b], read);
    }

    [Fact]
    public async Task Nothing_is_read_while_the_server_is_busy()
    {
        using var folder = new TempFolder();
        string a = folder.File("a.mkv");
        int checks = 0;
        var state = new LibraryDiagnosticState();
        string? pausedSeen = null;
        string? Busy()
        {
            pausedSeen = state.PausedBecause ?? pausedSeen;
            return ++checks <= 3 ? "lecture en cours" : null;
        }
        int reads = 0;
        Task<LibraryProbe> Read(string path, CancellationToken _)
        {
            Assert.True(checks > 3);
            reads++;
            return Task.FromResult(CleanProbe());
        }
        await Scanner(new LibraryDiagnosticStore(folder.Results), Read, Busy, state).RunAsync([a], null, CancellationToken.None);
        Assert.Equal(1, reads);
        Assert.Equal("lecture en cours", pausedSeen);
        Assert.Null(state.PausedBecause);
    }

    [Fact]
    public async Task A_playback_starting_during_a_probe_stops_it_and_the_file_is_read_again_afterwards()
    {
        using var folder = new TempFolder();
        string a = folder.File("a.mkv");
        // Idle for the first check (before the probe), busy for the watcher's, idle again from then on.
        int checks = 0;
        string? Busy() => ++checks == 2 ? "lecture en cours" : null;
        int reads = 0;
        async Task<LibraryProbe> Read(string path, CancellationToken token)
        {
            if (++reads == 1)
                await Task.Delay(Timeout.Infinite, token);
            return CleanProbe();
        }
        var store = new LibraryDiagnosticStore(folder.Results);
        await Scanner(store, Read, Busy).RunAsync([a], null, CancellationToken.None);
        Assert.Equal(2, reads);
        var entry = Assert.Single(store.Snapshot());
        Assert.Null(entry.Error);
    }

    [Fact]
    public async Task A_failed_file_is_recorded_without_its_path_in_the_message_or_the_log_and_tried_again()
    {
        using var folder = new TempFolder();
        string a = folder.File("private-name.mkv");
        var log = new List<string>();
        var store = new LibraryDiagnosticStore(folder.Results);
        int reads = 0;
        Task<LibraryProbe> Fail(string path, CancellationToken _)
        {
            reads++;
            throw new InvalidOperationException("cannot read " + path);
        }
        await Scanner(store, Fail, log: log).RunAsync([a], null, CancellationToken.None);
        var entry = Assert.Single(store.Snapshot());
        Assert.DoesNotContain("private-name", entry.Error);
        Assert.DoesNotContain(log, m => m.Contains("private-name"));
        await Scanner(store, Fail, log: log).RunAsync([a], null, CancellationToken.None);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task Files_gone_from_the_library_are_forgotten_but_an_empty_library_says_nothing()
    {
        using var folder = new TempFolder();
        string a = folder.File("a.mkv"), b = folder.File("b.mkv");
        var store = new LibraryDiagnosticStore(folder.Results);
        Task<LibraryProbe> Read(string path, CancellationToken _) => Task.FromResult(CleanProbe());
        await Scanner(store, Read).RunAsync([a, b], null, CancellationToken.None);
        await Scanner(store, Read).RunAsync([], null, CancellationToken.None);
        Assert.Equal(2, store.Snapshot().Count);
        await Scanner(store, Read).RunAsync([a], null, CancellationToken.None);
        Assert.Equal(a, Assert.Single(store.Snapshot()).Path);
    }

    [Fact]
    public async Task The_report_counts_per_category_and_lists_only_files_with_findings()
    {
        using var folder = new TempFolder();
        string clean = folder.File("clean.mkv"), late = folder.File("late.mkv"), missing = Path.Combine(folder.Root, "missing.mkv");
        var store = new LibraryDiagnosticStore(folder.Results);
        Task<LibraryProbe> Read(string path, CancellationToken _) => Task.FromResult(path == late ? Probe(videoStart: 0.079) : CleanProbe());
        var state = new LibraryDiagnosticState();
        await Scanner(store, Read, state: state).RunAsync([clean, late, missing], null, CancellationToken.None);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(LibraryDiagnosticReport.Build(store.Snapshot(), state, true)));
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("totals").GetProperty("analysed").GetInt32());
        Assert.Equal(1, root.GetProperty("totals").GetProperty("clean").GetInt32());
        Assert.Equal(1, root.GetProperty("totals").GetProperty("unreadable").GetInt32());
        int Count(string id) => root.GetProperty("categories").EnumerateArray().First(c => c.GetProperty("id").GetString() == id).GetProperty("count").GetInt32();
        Assert.Equal(1, Count(LibraryDiagnostic.LateVideoStart));
        Assert.Equal(1, Count(LibraryDiagnostic.Unreadable));
        Assert.Equal(0, Count(LibraryDiagnostic.TimestampHole));
        var files = root.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()).ToList();
        Assert.Equal(2, files.Count);
        Assert.DoesNotContain(clean, files);

        using var summary = JsonDocument.Parse(JsonSerializer.Serialize(LibraryDiagnosticReport.Build(store.Snapshot(), state, false)));
        Assert.Equal(JsonValueKind.Null, summary.RootElement.GetProperty("files").ValueKind);
    }

    [Fact]
    public void Every_category_says_what_it_means_and_what_it_does_to_a_conversion()
    {
        foreach (var c in LibraryDiagnostic.Categories)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Title));
            Assert.False(string.IsNullOrWhiteSpace(c.Meaning));
            Assert.False(string.IsNullOrWhiteSpace(c.Consequence));
        }
    }
}
