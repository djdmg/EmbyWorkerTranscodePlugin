using System.Globalization;

namespace RemoteTranscode.Coordinator;

/// <summary>One packet as ffprobe lists it, in seconds. Either time may be absent from the file.</summary>
public sealed record ProbePacket(double? Pts, double? Dts = null, double? Duration = null)
{
    /// <summary>The time the demuxer gives for this packet: its presentation time, else its decoding time.</summary>
    public double? Time => Pts ?? Dts;
}

/// <summary>A span of the file that was read, in file timestamps. The end of the last one may lie past the file's end.</summary>
public sealed record ProbeWindow(double Start, double End);

/// <summary>What ffprobe read of a file: its description and the packets of a few windows, nothing decoded.</summary>
public sealed record LibraryProbe
{
    /// <summary>The file's start as the server's ffprobe reads it (format start_time).</summary>
    public double? FileStart { get; init; }
    public double? Duration { get; init; }
    /// <summary>Stored frame rates (avg_frame_rate, r_frame_rate), null when absent or 0/0.</summary>
    public double? AverageFrameRate { get; init; }
    public double? BaseFrameRate { get; init; }
    public bool Interlaced { get; init; }
    /// <summary>The video stream's own start time: only used when no video packet was read in the first window.</summary>
    public double? VideoStreamStart { get; init; }
    /// <summary>Video packets (not cover art), in any order: files store them in decoding order, windows may overlap.</summary>
    public IReadOnlyList<ProbePacket> Video { get; init; } = [];
    /// <summary>Audio packets, every track together.</summary>
    public IReadOnlyList<ProbePacket> Audio { get; init; } = [];
    /// <summary>
    /// Where the audio streams say they start (the earliest). The first AAC packet is usually stored 21 ms before it, its
    /// samples being discarded by every player (encoder delay): the audio is heard from here, not from that packet.
    /// </summary>
    public double? AudioStreamStart { get; init; }
    /// <summary>Earliest packet of the other streams (subtitles, data): they count for the file's start too.</summary>
    public double? FirstOtherPacket { get; init; }
    /// <summary>The windows read, the first one at the file's start and the last one at its end.</summary>
    public IReadOnlyList<ProbeWindow> Windows { get; init; } = [];
}

/// <summary>A category of a file at risk, with what it means and what it does to a conversion (French: shown as is).</summary>
public sealed record LibraryCategory(string Id, string Level, string Title, string Meaning, string Consequence);

/// <summary>One finding on a file: its category and, in plain French, the measure behind it.</summary>
public sealed record LibraryFinding(string Category, string Detail);

/// <summary>Findings on a file, and how much of it they rest on.</summary>
public sealed record LibraryClassification(IReadOnlyList<LibraryFinding> Findings, double SampledSeconds, bool EndRead);

/// <summary>
/// From what ffprobe read of a file to its risks for a slice-based conversion. Pure: no process and no file, so every
/// threshold is covered by tests on synthetic numbers.
/// </summary>
public static class LibraryDiagnostic
{
    public const string LateVideoStart = "late-video-start";
    public const string FileStartDisagreement = "file-start-disagreement";
    public const string TimestampHole = "timestamp-hole";
    public const string NoPresentationTimes = "no-presentation-times";
    public const string VideoShorterThanAudio = "video-shorter-than-audio";
    public const string VariableFrameRate = "variable-frame-rate";
    public const string StoredRateMismatch = "stored-rate-mismatch";
    public const string Unreadable = "unreadable";

    /// <summary>Taken care of by the plugin: nothing to do, the file is listed for information.</summary>
    public const string LevelHandled = "gere";
    /// <summary>The conversion works, with a known defect or a limit.</summary>
    public const string LevelWatch = "a-surveiller";
    /// <summary>Not analysed.</summary>
    public const string LevelUnknown = "inconnu";

    /// <summary>
    /// The video starts this long after the audio (seconds). Each stream is rounded by up to 1 ms in MKV: 5 ms cannot
    /// come from rounding, and it is also what the conversion check tells apart (SourceInfo.VerifyVideoStart).
    /// </summary>
    public const double LateStartThreshold = 0.005;

    /// <summary>
    /// The file's start lies this far before its first packet. Both come from the same timestamps, so a normal file
    /// shows no difference at all. The upper bound is that of EmbyConversionPlanner.SeekCorrection: over it, this is
    /// not two readings of the start.
    /// </summary>
    public const double StartDisagreementThreshold = 0.001;
    public const double StartDisagreementMaximum = 5;

    /// <summary>Rounding of a gap between two frames: 1 ms per timestamp in MKV.</summary>
    public const double TimestampRounding = 0.002;

    /// <summary>A gap is a hole over this many usual gaps, plus rounding: one lost frame (2 usual gaps) is one.</summary>
    public const double HoleFactor = 1.5;

    /// <summary>Interlaced video mixes one packet per field and one per frame: two usual gaps are normal there.</summary>
    public const double InterlacedHoleFactor = 2.5;

    /// <summary>In a variable frame rate, long gaps are the norm: only a long stop is still a hole.</summary>
    public const double VariableHoleFactor = 3;
    public const double VariableHoleMinimum = 0.25;

    /// <summary>A gap differs from the usual one over this share of it, and never under 2.5 ms (MKV at 120 fps: 8 and 9 ms).</summary>
    public const double IrregularShare = 0.12;
    public const double IrregularMinimum = 0.0025;

    /// <summary>Share of irregular gaps from which the rate is variable: a few holes never reach it.</summary>
    public const double VariableRateShare = 0.05;

    /// <summary>Gaps needed to say anything about the rhythm of the frames.</summary>
    public const int MinimumGaps = 30;

    /// <summary>Gaps needed to compare the real rate with the stored one.</summary>
    public const int MinimumGapsForRate = 60;

    /// <summary>Stored and real rates differ by this share: 23.976 and 24 (0.1 %) are the same here, 24 and 25 are not.</summary>
    public const double RateMismatchShare = 0.01;

    /// <summary>
    /// Presentation times trimmed from each edge of a window. A window stops at any packet: with B-frames, frames shown
    /// before the last ones read are still to come, which looks like a hole. H.264 and HEVC reorder at most 16 frames.
    /// </summary>
    public const int ReorderMargin = 16;

    /// <summary>Under a second, streams end together as far as a conversion is concerned (as in DiagnosticsAnalysis).</summary>
    public const double ShorterVideoThreshold = 1;

    /// <summary>How far before the end the conversion check looks for the video's end (Shim's VideoSpanAsync, second try).</summary>
    public const double CheckEndWindow = 600;

    public static readonly IReadOnlyList<LibraryCategory> Categories =
    [
        new(TimestampHole, LevelWatch, "Trou dans les temps de la vidéo",
            "Il manque au moins une image dans la vidéo : l'écart entre deux images est anormalement long. La source elle-même a ce défaut.",
            "Quand la commande d'Emby fixe la fréquence d'images (-r), ffmpeg comble le trou en répétant une image : aucun décalage mesuré sur un trou de 85 ms. Sans fréquence fixée, le résultat n'a pas été mesuré."),
        new(NoPresentationTimes, LevelWatch, "Paquets vidéo sans temps de présentation",
            "Des paquets vidéo n'indiquent pas quand les afficher (typiquement un AVI DivX/Xvid avec B-frames groupées). Deux lectures qui sautent dans le fichier ne retombent pas sur la même image.",
            "Le fichier n'est pas découpé en tranches : il est converti d'un seul bloc, par une seule machine, donc sans le gain de plusieurs workers. Le contrôle final vérifie le début de la vidéo, mais la fin n'y est connue qu'à quelques images près (au moins 3)."),
        new(VariableFrameRate, LevelWatch, "Cadence variable",
            "Les images ne sont pas régulièrement espacées dans les passages lus (vidéo enregistrée ou capturée à cadence variable, ou 3:2 stocké tel quel).",
            "Si le profil d'Emby impose une cadence (-r), la conversion répète ou saute des images pour la tenir, comme Emby seul. Le contrôle final calcule sa tolérance avec la cadence moyenne enregistrée. Ce cas n'a jamais été passé au banc de test du plugin : à vérifier sur le résultat."),
        new(StoredRateMismatch, LevelWatch, "Cadence enregistrée fausse",
            "La cadence inscrite dans le fichier ne correspond pas à l'espacement réel des images.",
            "Le contrôle final calcule sa tolérance avec la cadence enregistrée : trop serrée, un résultat juste peut être rejeté et reconverti sur le serveur (plus lent) ; trop large, une image perdue en fin de vidéo peut passer. Si Emby impose -r d'après cette cadence, des images sont répétées ou sautées, comme Emby seul."),
        new(VideoShorterThanAudio, LevelHandled, "Vidéo plus courte que le son",
            "La vidéo s'arrête nettement avant la fin du son (par exemple sur un générique sans image).",
            "Géré : la dernière tranche s'arrête à la fin de la vidéo et le son est gardé en entier, au prix de deux courts essais supplémentaires. Si l'écart dépasse 10 minutes, le contrôle final relit toute la piste pour trouver sa fin, ce qui prend plus de temps ; un dépassement de délai fait refaire la conversion sur le serveur."),
        new(LateVideoStart, LevelHandled, "Vidéo commençant après le son",
            "La première image vient un peu après le début du son (fréquent après un remuxage).",
            "Géré depuis le plugin 0.7.35 : l'assemblage remet la vidéo à sa place et le contrôle final compare ce décalage. Un résultat décalé est rejeté et reconverti sur le serveur (plus lent, mais juste). Avec un plugin plus ancien, la vidéo sortait en avance sur le son du début à la fin."),
        new(FileStartDisagreement, LevelHandled, "Début de fichier lu différemment",
            "Le ffprobe d'Emby place le début du fichier avant son premier paquet : les versions de ffmpeg ne s'accordent pas sur ce point (ffmpeg 5.1 d'Emby et ffmpeg 9 des workers ont donné 0 et 0,021 s sur un même fichier).",
            "Géré depuis le plugin 0.7.34 avec un worker 0.7.19 ou plus récent : le worker déplace son point de départ de la différence. Avec un worker plus ancien, ses tranches commencent une image trop tard, avec une image répétée ou sautée aux jonctions."),
        new(Unreadable, LevelUnknown, "Non analysé",
            "ffprobe n'a pas pu lire ce fichier dans le temps imparti, ou le fichier est inaccessible. Rien n'est conclu sur lui.",
            "Inconnue. L'analyse est retentée à la prochaine exécution de la tâche."),
    ];

    /// <summary>What a sampling cannot see, shown with the results.</summary>
    public static readonly IReadOnlyList<string> Limits =
    [
        "Analyse par échantillons : sont lus le début, cinq passages de quelques secondes et la fin de chaque fichier, soit environ 40 s : 1 à 2 % d'un épisode, moins d'un film. Un trou isolé hors de ces passages n'est pas vu, et il a donc peu de chances d'être trouvé : un fichier absent de la liste n'est pas garanti sans trou. De même pour une partie à cadence différente.",
        "Le début lu par les workers n'est pas mesuré : seul le ffprobe d'Emby tourne ici. Un fichier n'est signalé que si ce ffprobe place le début avant le premier paquet.",
        "Le fichier n'est jamais modifié, rien n'est décodé pour ces mesures (ffprobe peut décoder quelques images pour identifier les pistes à l'ouverture).",
    ];

    public static LibraryClassification Classify(LibraryProbe probe)
    {
        var findings = new List<LibraryFinding>();
        var windows = MergedWindows(probe.Windows);
        int missing = probe.Video.Count(p => p.Pts is null);
        if (missing > 0)
            findings.Add(new(NoPresentationTimes, $"{missing} paquets vidéo sur {probe.Video.Count} lus sans temps de présentation"));
        // Without presentation times on every packet, the rest would look like holes: decoding times are regular there.
        var times = probe.Video.Select(p => missing > 0 ? p.Dts : p.Pts).OfType<double>().Order().ToList();
        double firstWindowEnd = windows.Count > 0 ? windows[0].End : double.PositiveInfinity;

        double? videoStart = times.Count > 0 && times[0] <= firstWindowEnd ? times[0] : probe.VideoStreamStart;
        // The later of the two: the first packet may hold discarded samples, and Emby's ffprobe may read a stream's own
        // start as 0 where its packets say otherwise.
        double? audioStart = probe.Audio.Select(p => p.Time).Min();
        if (audioStart > firstWindowEnd)
            audioStart = null;
        else if (audioStart < probe.AudioStreamStart)
            audioStart = probe.AudioStreamStart;
        if (videoStart - audioStart >= LateStartThreshold)
            findings.Add(new(LateVideoStart, $"La vidéo commence {Milliseconds(videoStart!.Value - audioStart!.Value)} après le son"));

        // Packets only: Emby's ffprobe may also read the streams' own start times as 0.
        double? earliest = new[] { times.Count > 0 && times[0] <= firstWindowEnd ? times[0] : (double?)null, audioStart, probe.FirstOtherPacket }.Min();
        if (probe.FileStart is { } fileStart && earliest - fileStart is >= StartDisagreementThreshold and <= StartDisagreementMaximum)
            findings.Add(new(FileStartDisagreement, $"Début du fichier lu à {Seconds3(fileStart)}, premier paquet à {Seconds3(earliest!.Value)}"));

        var gaps = GapsInWindows(times, windows);
        bool variable = false;
        if (gaps.Count >= MinimumGaps)
        {
            double usual = Median(gaps);
            double tolerance = Math.Max(IrregularMinimum, usual * IrregularShare);
            int irregular = gaps.Count(g => Math.Abs(g - usual) > tolerance);
            // Interlaced video mixes field and frame packets: their rhythm says nothing about the frames.
            variable = !probe.Interlaced && irregular >= gaps.Count * VariableRateShare;
            if (variable)
                findings.Add(new(VariableFrameRate, $"Écart entre images de {Milliseconds(gaps.Min())} à {Milliseconds(gaps.Max())}, habituel {Milliseconds(usual)}"));

            double holeFrom = variable ? Math.Max(usual * VariableHoleFactor, VariableHoleMinimum)
                : probe.Interlaced ? usual * InterlacedHoleFactor + TimestampRounding
                : usual * HoleFactor + TimestampRounding;
            var holes = gaps.Where(g => g > holeFrom).ToList();
            if (holes.Count > 0)
                findings.Add(new(TimestampHole, $"{holes.Count} trou(s) dans les passages lus, le plus long de {Milliseconds(holes.Max())} (images espacées de {Milliseconds(usual)})"));

            var regular = gaps.Where(g => g <= holeFrom).ToList();
            if (!variable && regular.Count >= MinimumGapsForRate && (probe.AverageFrameRate ?? probe.BaseFrameRate) is { } stored && stored > 0)
            {
                // The mean, not the median: rounded to the millisecond, a median at 120 fps is off by 10 %.
                double real = 1 / regular.Average();
                // A very short file's average rate is off by a frame or two out of its few frames.
                double allowed = Math.Max(RateMismatchShare, probe.Duration is > 0 and var d ? 2 / (d * real) : 0);
                double ratio = real / stored;
                bool sameRate = Math.Abs(ratio - 1) <= allowed;
                bool fields = probe.Interlaced && (Math.Abs(ratio - 2) <= 2 * allowed || Math.Abs(ratio - 0.5) <= allowed / 2);
                if (!sameRate && !fields)
                    findings.Add(new(StoredRateMismatch, $"Cadence enregistrée {Rate(stored)} i/s, cadence réelle {Rate(real)} i/s"));
            }
        }

        double? fileEnd = End(probe.Audio, 0);
        double lastStart = windows.Count > 0 ? windows[^1].Start : double.PositiveInfinity;
        double frame = probe.AverageFrameRate is > 0 and var rate ? 1 / rate : 0;
        double? videoEnd = End(probe.Video, frame);
        // Read up to the end: the last window holds packets. Seeking does not find the last packets in every container.
        bool endRead = fileEnd >= lastStart || videoEnd >= lastStart;
        if (fileEnd >= lastStart && videoEnd is { } ve)
        {
            // A video absent from the last window ended before it: where exactly is not known.
            bool exact = ve >= lastStart;
            double shorter = fileEnd.Value - (exact ? ve : lastStart);
            if (shorter >= ShorterVideoThreshold)
                findings.Add(new(VideoShorterThanAudio, $"La vidéo s'arrête {(exact ? "" : "au moins ")}{Duration(shorter)} avant le son"
                    + (shorter >= CheckEndWindow ? " : le contrôle final ne trouvera pas la fin de la vidéo" : "")));
        }
        // The last window's end lies past the file's end on purpose.
        double fileLimit = probe.Duration is > 0 and var length ? (probe.FileStart ?? 0) + length : double.PositiveInfinity;
        double sampled = windows.Sum(w => Math.Max(0, Math.Min(w.End, fileLimit) - w.Start));
        return new(findings, sampled, endRead);
    }

    /// <summary>Windows in order, those that touch merged: a short file is read as one span.</summary>
    private static List<ProbeWindow> MergedWindows(IReadOnlyList<ProbeWindow> windows)
    {
        var merged = new List<ProbeWindow>();
        foreach (var w in windows.OrderBy(w => w.Start))
        {
            if (merged.Count > 0 && w.Start <= merged[^1].End)
                merged[^1] = new(merged[^1].Start, Math.Max(merged[^1].End, w.End));
            else
                merged.Add(w);
        }
        return merged;
    }

    /// <summary>
    /// Gaps between successive presentation times (sorted, so B-frame reordering does not matter), inside each window
    /// only. What a seek reads before a window, from the keyframe it lands on, is left out: with a long GOP it can touch
    /// the previous window's span, and the distance between the two is no gap. A window's edges are left out too (see
    /// <see cref="ReorderMargin"/>), except the start of the file: nothing comes before it.
    /// </summary>
    private static List<double> GapsInWindows(List<double> sortedTimes, List<ProbeWindow> windows)
    {
        var gaps = new List<double>();
        for (int w = 0; w < windows.Count; w++)
        {
            var inWindow = sortedTimes.Where(t => t >= windows[w].Start && t <= windows[w].End).Distinct().ToList();
            int from = w == 0 ? 0 : ReorderMargin;
            for (int i = from + 1; i < inWindow.Count - ReorderMargin; i++)
                gaps.Add(inWindow[i] - inWindow[i - 1]);
        }
        return gaps;
    }

    private static double? End(IReadOnlyList<ProbePacket> packets, double defaultLength)
    {
        double? end = null;
        foreach (var p in packets)
            if (p.Time is { } time && (end is null || time + (p.Duration ?? defaultLength) > end))
                end = time + (p.Duration ?? defaultLength);
        return end;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    private static string Milliseconds(double seconds) => (seconds * 1000).ToString("0", CultureInfo.InvariantCulture) + " ms";

    private static string Seconds3(double seconds) => French(seconds.ToString("0.000", CultureInfo.InvariantCulture)) + " s";

    private static string Rate(double rate) => French(rate.ToString("0.###", CultureInfo.InvariantCulture));

    private static string Duration(double seconds) =>
        seconds < 120 ? French(seconds.ToString("0.0", CultureInfo.InvariantCulture)) + " s"
        : (seconds / 60).ToString("0", CultureInfo.InvariantCulture) + " min";

    private static string French(string number) => number.Replace('.', ',');
}
