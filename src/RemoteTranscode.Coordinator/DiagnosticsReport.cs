using System.Globalization;
using System.Text;

namespace RemoteTranscode.Coordinator;

/// <summary>
/// A part of a report. <see cref="Lines"/> can be cut at a line (head and tail stay, so fences and collapsible blocks
/// stay balanced); a part without lines goes whole or not at all. Lower <see cref="Priority"/> numbers are served first.
/// </summary>
public sealed record ReportSection(int Priority, string Name, string Head, IReadOnlyList<string> Lines, string Tail = "")
{
    public static ReportSection Whole(int priority, string name, string text) => new(priority, name, text, []);
    public int Length => Head.Length + Tail.Length + Lines.Sum(l => l.Length + 1);
}

/// <summary>GitHub refuses bodies over 65536 characters: what matters most goes first, the rest is cut and says so.</summary>
public static class ReportBudget
{
    /// <summary>Under GitHub's 65536 characters, with a margin.</summary>
    public const int MaxBody = 65000;

    public static string Compose(IReadOnlyList<ReportSection> sections, int limit = MaxBody)
    {
        var kept = new string?[sections.Count];
        var omitted = new List<string>();
        // Room for the closing line naming what was left out.
        int left = limit - 400;
        foreach (int i in Enumerable.Range(0, sections.Count).OrderBy(i => sections[i].Priority).ThenBy(i => i))
        {
            var s = sections[i];
            if (s.Length <= left)
                kept[i] = Render(s, s.Lines.Count);
            else if (s.Lines.Count > 0 && Cut(s, left) is { } cut)
            {
                kept[i] = cut;
                omitted.Add(s.Name + " (cut)");
            }
            else
            {
                omitted.Add(s.Name);
                continue;
            }
            left -= kept[i]!.Length;
        }
        var b = new StringBuilder();
        foreach (string? text in kept)
            b.Append(text);
        if (omitted.Count > 0)
            b.Append("\n_Report size limit reached, left out: ").Append(string.Join(", ", omitted.Take(12))).Append("._\n");
        return b.ToString();
    }

    private static string Render(ReportSection s, int lines)
    {
        var b = new StringBuilder(s.Head);
        foreach (string line in s.Lines.Take(lines))
            b.Append(line).Append('\n');
        if (lines < s.Lines.Count)
            b.Append(CultureInfo.InvariantCulture, $"... {s.Lines.Count - lines} more line(s) cut: report size limit\n");
        return b.Append(s.Tail).ToString();
    }

    private static string? Cut(ReportSection s, int room)
    {
        int used = s.Head.Length + s.Tail.Length + 60;
        if (used >= room)
            return null;
        int n = 0;
        while (n < s.Lines.Count && used + s.Lines[n].Length + 1 <= room)
            used += s.Lines[n++].Length + 1;
        return n == 0 ? null : Render(s, n);
    }
}

/// <summary>
/// The diagnostics as parts of a report. Everything here is numbers, checked tokens, fixed words and sanitized commands;
/// <paramref>scrub</paramref> still takes the installation's own strings out as a last safety.
/// </summary>
public static class DiagnosticsRenderer
{
    private static string F(double? v, string format = "0.######") => v is { } d ? d.ToString(format, CultureInfo.InvariantCulture) : "?";
    private static string S(double? v, string format = "+0.0;-0.0;0.0") => v is { } d ? d.ToString(format, CultureInfo.InvariantCulture) : "-";
    private static string I(int? v) => v is { } d ? d.ToString(CultureInfo.InvariantCulture) : "-";

    public const int RecipePriority = 1;
    public const int SummaryPriority = 4;
    public const int SamplePriority = 6;
    public const int RawPriority = 8;

    public static string StatusText(DiagnosticsResult d) => d.Status switch
    {
        DiagnosticsStatus.TimedOut => $"stopped at the time limit after {d.Seconds:0} s: what follows is what was collected",
        DiagnosticsStatus.Cancelled => $"interrupted after {d.Seconds:0} s (server stopping): what follows is what was collected",
        DiagnosticsStatus.Skipped => "not run (another analysis held the slot)",
        _ => $"completed in {d.Seconds:0} s",
    };

    public static IEnumerable<ReportSection> Recipe(DiagnosticsResult d, Func<string, string> scrub)
    {
        var lines = new List<string>();
        var head = new StringBuilder("\n### Reproduce without the original file\n");
        if (d.Recipe is { } recipe)
        {
            head.Append("Synthetic source with the same technical characteristics, for the bench in `test/local`:\n");
            head.Append("```\n").Append(scrub(recipe.Command)).Append("\n```\n");
            foreach (string note in recipe.Notes)
                head.Append("- ").Append(scrub(note)).Append('\n');
        }
        else
            head.Append("- no recipe: the source could not be described\n");
        if (d.Verdict.Class == VerdictClass.FileStartDisagreement)
            head.Append("- The machines read different file starts: run it with `test/local/fake-emby-ffprobe` on the server's side (bench test T24)\n");
        if (d.SeekMargin is { } margin)
            head.Append(CultureInfo.InvariantCulture, $"- Seek margin of the runs: {margin * 1000:0.###} ms below a frame's time\n");
        if (d.ToneMapper != null)
            head.Append("- Tone mapper: ").Append(scrub(d.ToneMapper)).Append('\n');
        if (d.AudioState != null)
            head.Append("- Audio: ").Append(scrub(FailureReportFormatter.Sanitize(d.AudioState, []))).Append('\n');
        if (d.AudioOffset != null)
            head.Append("- Audio offset at assembly: ").Append(scrub(d.AudioOffset)).Append('\n');
        foreach (var command in d.Commands)
            lines.Add($"{command.Label}:\n{Capped(scrub(CommandSanitizer.Line(command.Args)), 6000)}");
        yield return lines.Count > 0
            ? new ReportSection(RecipePriority, "reproduction recipe", head + "\nCommand templates (paths, titles and unknown values removed):\n```\n", lines, "```\n")
            : ReportSection.Whole(RecipePriority, "reproduction recipe", head.ToString());
    }

    public static IEnumerable<ReportSection> Summaries(DiagnosticsResult d, Func<string, string> scrub)
    {
        if (d.Fingerprint is { } fp)
            yield return new ReportSection(SummaryPriority, "source timing", "\n### Source timing fingerprint\n", FingerprintLines(fp, d).Select(l => scrub(l)).ToList());
        if (d.Slices.Any(s => s.ChunkCount > 0))
        {
            var machines = d.Slices.Select(s => s.Machine).OfType<string>().Distinct().ToList();
            var head = new StringBuilder("\n### Slices: listed against measured\n");
            head.Append(CultureInfo.InvariantCulture, $"Chunks measured with ffprobe: {d.ChunksMeasured} of {d.ChunksListed}. `end` = listed end - slice boundary, `list` = concat list length - slice length (ms); frames from the chunks' packets against those expected from the source; `shift` = first frame's offset in frames, against the first slice.\n");
            for (int i = 0; i < machines.Count; i++)
                head.Append(CultureInfo.InvariantCulture, $"- M{i + 1}: {scrub(machines[i])}\n");
            head.Append("\n| slice | machine | runs | resumes | chunks | end ms | list ms | frames | expected | diff | shift | next join ms |\n|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|\n");
            yield return new ReportSection(SummaryPriority, "slice summary", head.ToString(), d.Slices.Select(s =>
                $"| {s.Number} | {(s.Machine is { } m ? "M" + (machines.IndexOf(m) + 1) : "-")} | {s.Runs} | {s.Resumes} | {s.ChunkCount}{(s.MissingChunks > 0 ? $" ({s.MissingChunks} missing)" : "")} | {S(s.ListedEndMs)} | {S(s.ConcatMs)} | {I(s.Frames)} | {I(s.Expected)} | {(s.FrameDifference is { } x ? x.ToString("+0;-0;0", CultureInfo.InvariantCulture) : "-")} | {(s.FirstFrameShift is { } y ? y.ToString("+0;-0;0", CultureInfo.InvariantCulture) : "-")} | {S(s.JoinMs)} |").ToList());
        }
        if (d.FailingRuns.Count > 0)
        {
            var lines = new List<string>();
            foreach (var run in d.FailingRuns)
            {
                lines.Add($"--- {scrub(run.Machine ?? "?")}{(run.Slice is { } n ? $", slice {n}" : "")}");
                lines.AddRange(run.Lines.Take(30).Select(l => Capped(FailureReportFormatter.Sanitize(l, []), 400)).Select(scrub));
            }
            yield return new ReportSection(SummaryPriority + 1, "failing runs", "\n### Error output of the failing runs\n```\n", lines.Select(l => l.Replace("```", "'''")).ToList(), "```\n");
        }
    }

    public static IEnumerable<ReportSection> Raw(DiagnosticsResult d)
    {
        if (d.FirstFrames.Count > 0)
            yield return Collapsed(SamplePriority, "first frames", "First video packets of the source (decoding order): pts dts duration flags", d.FirstFrames.Select(Packet).ToList());
        if (d.Boundaries.Count > 0)
        {
            var lines = new List<string>();
            foreach (var sample in d.Boundaries)
            {
                lines.Add($"slice {sample.Slice} starts at {F(sample.Time)} (file time)");
                lines.AddRange(sample.Frames.Select(p => "  " + Packet(p)));
            }
            yield return Collapsed(SamplePriority, "frames around boundaries", "Source frames around slice boundaries (presentation order): pts dts duration flags", lines);
        }
        if (d.Chunks.Count > 0)
            yield return Collapsed(RawPriority, "chunk table", "Every chunk: slice index listed_start listed_end concat_duration size | packets first_pts last_pts",
                d.Chunks.Select(c => $"{c.Slice} {c.Index} {F(c.ListedStart)} {F(c.ListedEnd)} {F(c.ConcatDuration)} {c.Size} | {(c.Measure is null ? "not measured" : c.Measure.Missing ? "file missing" : $"{c.Measure.Packets} {F(c.Measure.FirstPts)} {F(c.Measure.LastPts)}")}").ToList());
    }

    private static ReportSection Collapsed(int priority, string name, string summary, IReadOnlyList<string> lines) =>
        new(priority, name, $"\n<details><summary>{summary}</summary>\n\n```\n", lines, "```\n\n</details>\n");

    private static string Packet(PacketInfo p) => $"{F(p.Pts)} {F(p.Dts)} {F(p.Duration)} {p.Flags ?? "?"}";

    private static IEnumerable<string> FingerprintLines(SourceFingerprint fp, DiagnosticsResult d)
    {
        yield return $"- Container: {fp.Container ?? "?"}, start_time {F(fp.Start)} s, duration {F(fp.Duration)} s, bit rate {(fp.BitRate is { } b ? b.ToString(CultureInfo.InvariantCulture) : "?")}, {fp.Chapters} chapter(s)";
        foreach (var s in fp.Streams)
        {
            string common = $"- Stream {s.Index} {s.Type ?? "?"}{(s.Cover ? " (cover art)" : "")}: {s.Codec ?? "?"}{(s.Profile is { } p ? $" ({p})" : "")}, start_time {F(s.Start)} s, duration {F(s.Duration)} s, time base {s.TimeBase ?? "?"}";
            yield return s.Type switch
            {
                "video" => $"{common}, r_frame_rate {s.RFrameRate ?? "?"}, avg_frame_rate {s.AvgFrameRate ?? "?"}, has_b_frames {I(s.HasBFrames)}, level {I(s.Level)}, {s.Width}x{s.Height}, {s.PixelFormat ?? "?"}, field order {s.FieldOrder ?? "?"}, color {s.ColorPrimaries ?? "?"}/{s.ColorTransfer ?? "?"}/{s.ColorSpace ?? "?"}/{s.ColorRange ?? "?"}",
                "audio" => $"{common}, {I(s.SampleRate)} Hz, {I(s.Channels)} channels ({s.ChannelLayout ?? "?"})",
                _ => common,
            };
        }
        if (d.FileStarts.Count > 0)
            yield return "- File start as each machine's own ffmpeg read it (the lines above are the server's ffprobe only): " + string.Join("; ", d.FileStarts.Select(p => $"{p.Key}: {F(p.Value)} s"));
        else
            yield return "- File start as the machines' own ffmpeg read it: not reported (only the server's ffprobe is known here)";
        if (d.Stats is { } st)
        {
            string rate = st.ConstantRate switch { true => "constant (CFR)", false => "variable (VFR)", _ => "unknown" };
            yield return $"- Frame rate in a sample of {st.Packets} packets: {rate}, frame {F(st.FrameSeconds)} s (from {F(st.MinDelta)} to {F(st.MaxDelta)}), {st.WithoutPts} without pts";
            yield return $"- GOP in the sample: {(st.GopMedian is { } g ? $"median {g} frames, longest {I(st.GopMax)}" : "unknown (fewer than two keyframes)")}, {st.Keyframes} keyframes";
        }
        if (fp.Video is { Start: { } start } && DiagnosticsAnalysis.SourceFrame(fp, d.Stats) is { } frame && frame > 0)
        {
            double mod = start % frame;
            yield return $"- Video start against the frame grid: start mod frame {F(mod * 1000, "0.0")} ms, frame - that {F((frame - mod) * 1000, "0.0")} ms";
        }
        if (d.Ends is { } ends)
        {
            string audio = ends.Audio.Count > 0 ? string.Join(", ", ends.Audio.Select(a => $"stream {a.Key} at {F(a.Value)} s")) : "not found";
            yield return $"- Stream ends (read from {F(ends.From)} s on): video at {(ends.Video is { } v ? F(v) + " s" : "not found (before the window)")}, audio {audio}";
        }
    }

    private static string Capped(string text, int length) => text.Length <= length ? text : text[..length] + "...";
}
