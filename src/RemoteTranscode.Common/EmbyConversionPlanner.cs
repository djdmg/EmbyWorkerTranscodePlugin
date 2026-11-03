using System.Globalization;

namespace RemoteTranscode.Common;

/// <summary>An Emby conversion (Convert / download) split into a chunked video encode and a server-side assembly.</summary>
public sealed record ConversionPlan(
    bool Remote,
    string Reason,
    string Input,
    string Output,
    string[] ChunkTemplate,
    string[] AssemblyTemplate,
    JobRequirements? Requirements)
{
    public const string ListPlaceholder = "{chunklist}";

    /// <summary>Audio converted ahead on the server, used at assembly when it can be (see <see cref="AudioAheadPlan"/>).</summary>
    public AudioAheadPlan? AudioAhead { get; init; }

    /// <summary>Why the audio is not converted ahead (null when it is).</summary>
    public string? AudioAheadReason { get; init; }
}

/// <summary>
/// Emby's audio, converted on the server while the workers encode the video, so the assembly only copies streams
/// instead of reading the whole source again to convert its audio.
/// <list type="bullet">
/// <item><see cref="PrepareTemplate"/>: Emby's command with its input options and every audio map and audio option, but
/// no video, written to an MP4 file ({audio}) with timestamps moved <see cref="TimeShift"/> seconds later, so the
/// AAC encoder's priming (before 0) needs no edit list. MP4, like Emby's output, keeps exactly what the assembly
/// copies: timestamps in the audio's own time base, each packet's duration (the last one is shorter), stream
/// tags and dispositions. Checked byte for byte against the usual assembly; NUT, for instance, loses the last
/// packet's duration and Matroska rounds timestamps to the millisecond.</item>
/// <item><see cref="AssemblyTemplate"/>: the usual assembly, whose encoded audio streams come from that file
/// (input 2, copied) instead of being converted again. Its <c>-itsoffset {audiooffset}</c> puts the timestamps back
/// exactly where Emby's own conversion puts them (see <see cref="AssemblyOffset"/>).</item>
/// </list>
/// Audio streams Emby copies stay copied from the source, exactly as in the usual assembly.
/// </summary>
public sealed record AudioAheadPlan(string[] PrepareTemplate, string[] AssemblyTemplate, int[] SourceStreams, bool CopyTs, bool StartAtZero)
{
    public const string AudioPlaceholder = "{audio}";
    public const string OffsetPlaceholder = "{audiooffset}";

    /// <summary>Seconds added to every timestamp of the prepared file (exact in any audio time base).</summary>
    public const int TimeShift = 10;

    /// <summary>
    /// Movie time scale of the prepared file. MP4 writes the time shift as an edit in this scale, 1000 by default: rounded to the
    /// millisecond, the AAC start of -21.333 ms read as -22 ms and all the audio came out 0.667 ms early. This value divides
    /// exactly by the usual sample rates (44.1 and 48 kHz and their multiples): measured with Emby's ffmpeg 5.1, packets at the
    /// same times as the classic conversion, 0.000000 s apart (2026-11-02).
    /// </summary>
    public const string MovieTimescale = "28224000";

    /// <summary>
    /// The -itsoffset that gives the prepared file's packets their original times in the assembly. ffmpeg offsets an
    /// input by itsoffset minus its start time (-start_at_zero, or no -copyts), or by itsoffset alone (-copyts only):
    /// either way the result must be -<see cref="TimeShift"/> s, exactly. <paramref name="preparedStartMicros"/> is
    /// the prepared file's start time as ffmpeg computes it (microseconds, as ffprobe prints it).
    /// </summary>
    public string AssemblyOffset(long preparedStartMicros)
    {
        long micros = (CopyTs && !StartAtZero ? 0 : preparedStartMicros) - TimeShift * 1_000_000L;
        string sign = micros < 0 ? "-" : "";
        long abs = Math.Abs(micros);
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{abs / 1_000_000}.{abs % 1_000_000:D6}");
    }

    /// <summary>The assembly with the prepared file and its offset filled in (the concat list placeholder stays).</summary>
    public string[] Assembly(string preparedFile, string offset) =>
        AssemblyTemplate.Select(a => a == AudioPlaceholder ? preparedFile : a == OffsetPlaceholder ? offset : a).ToArray();
}

/// <summary>
/// Emby converts a file with one ffmpeg command whose settings come from its own quality profiles. That command is
/// split in two without changing those settings:
/// <list type="bullet">
/// <item>chunks: the video part only (input options, video map, video filters and encoder options), written as
/// fixed-length MPEG-TS chunks so an interrupted encode resumes at the first missing chunk;</item>
/// <item>assembly, on the server: Emby's command unchanged except that the video comes from the concatenated chunks and
/// is copied; audio, subtitles, metadata, container and output path stay exactly as Emby asked.</item>
/// </list>
/// Anything the split cannot express safely keeps Emby's own ffmpeg (the caller runs the original command).
/// </summary>
public static class EmbyConversionPlanner
{
    public const double ChunkSeconds = 30;

    // Options that only shape the video stream when they carry no stream specifier.
    private static readonly HashSet<string> VideoGeneric = new(StringComparer.Ordinal)
    {
        "-vf", "-r", "-s", "-aspect", "-pix_fmt", "-g", "-keyint_min", "-sc_threshold", "-crf", "-qp", "-preset", "-tune",
        "-profile", "-level", "-x264opts", "-x264-params", "-x265-params", "-maxrate", "-minrate", "-bufsize", "-vcodec",
        "-color_primaries", "-color_trc", "-colorspace", "-color_range", "-bf", "-refs", "-force_key_frames",
    };

    // Server-only or fork-only options removed from the worker command (the assembly keeps Emby's command as is).
    private static readonly HashSet<string> ServerOnly = new(StringComparer.Ordinal) { "-loglevel", "-v", "-print_graphs_file" };

    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "-y", "-copyts", "-start_at_zero", "-noautorotate", "-autorotate", "-sn", "-an", "-vn", "-dn", "-re",
        "-accurate_seek", "-noaccurate_seek", "-ignore_unknown", "-nostdin", "-nostats", "-shortest", "-fix_sub_duration",
    };

    public static ConversionPlan Plan(IReadOnlyList<string> args, IReadOnlyList<string> allowedRoots)
    {
        ConversionPlan Local(string reason) => new(false, reason, "", "", [], [], null);

        int inputCount = args.Count(a => a == "-i");
        if (inputCount != 1)
            return Local("un seul fichier source attendu");
        // Every option must be recognisable as a known flag or an option followed by its value; otherwise the split
        // below would have to guess which argument is a value (and could carry a server-only option to the worker).
        if (CheckOptions(args) is { } unexpected)
            return Local($"option inattendue : {unexpected}");
        int iIndex = IndexOf(args, "-i");
        if (iIndex + 1 >= args.Count - 1)
            return Local("pas de fichier source ou de sortie");
        string input = args[iIndex + 1];
        if (input.StartsWith("file:", StringComparison.Ordinal))
            input = input[5..];
        if (input.Contains("://", StringComparison.Ordinal) || !allowedRoots.Any(r => EmbyJobPlanner.IsUnder(input, r)))
            return Local("source hors des bibliothèques autorisées");
        string output = args[^1];
        if (output.StartsWith('-') || output.Contains("%d", StringComparison.Ordinal))
            return Local("sortie inattendue");
        if (args.Contains("-f") && EmbyJobPlanner.ValueOf(args.Skip(iIndex + 2).ToList(), "-f") is "segment" or "hls" or "image2")
            return Local("pas une conversion de fichier");

        // Output side: find the video map (Emby lists the video stream first) and the video encoder.
        var outputSide = args.Skip(iIndex + 2).Take(args.Count - iIndex - 3).ToList();
        int firstMap = outputSide.IndexOf("-map");
        if (firstMap < 0 || firstMap + 1 >= outputSide.Count)
            return Local("pas de -map");
        string videoMap = outputSide[firstMap + 1];
        string? encoder = null;
        for (int i = 0; i < outputSide.Count - 1; i++)
            if (outputSide[i] is "-c:v:0" or "-c:v" or "-vcodec" or "-codec:v:0")
                encoder = outputSide[i + 1];
        if (encoder is null or "copy")
            return Local("pas de réencodage vidéo (remux)");
        if (!WorkerCommandBuilder.HardwareEquivalentsOf(encoder).Any() && encoder is not ("libx264" or "libx265"))
            return Local($"encodeur {encoder} non pris en charge");
        if (outputSide.IndexOf("-filter_complex") is int fc and >= 0 && (fc + 1 >= outputSide.Count || FilterTouchesAudio(outputSide[fc + 1])))
            return Local("graphe de filtres audio");

        // ---- Chunk template (worker) ----
        // Input options are kept as they are (seek, hardware decoding, ...). An option not listed in Flags takes a value
        // (checked by CheckOptions): the next argument is copied with it.
        var chunk = new List<string>();
        for (int i = 0; i < iIndex; i++)
        {
            string a = args[i];
            if (ServerOnly.Contains(a)) { i++; continue; }
            if (a == "-re") continue;   // Emby throttles conversions to real time on the server; workers go full speed
            chunk.Add(a);
            if (!Flags.Contains(a) && i + 1 < iIndex) chunk.Add(args[++i]);
        }
        chunk.AddRange(["-i", "{input:0}", "-map", videoMap]);
        var filters = new List<string>();
        for (int i = 0; i < outputSide.Count; i++)
        {
            string a = outputSide[i];
            string name = a.Split(':')[0];
            bool hasValue = !Flags.Contains(a) && i + 1 < outputSide.Count;
            if (a == "-map") { i++; continue; }
            // Container-level tag (e.g. hvc1 for MP4): meaningless in the MPEG-TS chunks, applied at assembly.
            if (a == "-vtag") { i++; continue; }
            if (IsVideoOption(a))
            {
                chunk.Add(a);
                if (hasValue) chunk.Add(outputSide[++i]);
                if (name is "-filter_complex" or "-vf" or "-filter")
                    filters.AddRange(ArgTemplateValidator.FilterNames(outputSide[i]));
                continue;
            }
            if (hasValue) i++;
        }
        // No shift of negative timestamps: a run starting at 0 would otherwise list every chunk time two or three frames
        // late (the encoder's reordering delay), and a resume from those times would skip frames. With it, chunk times
        // are source times whether the run seeks or not (the frames themselves are the same either way).
        chunk.AddRange(["-an", "-sn", "-dn", "-avoid_negative_ts", "disabled", "-f", "segment",
            "-segment_time", ChunkSeconds.ToString(CultureInfo.InvariantCulture), "-segment_format", "mpegts",
            "-segment_list", "{list}", "-segment_list_type", "m3u8", "{out}"]);

        // ---- Assembly template (server, Emby's ffmpeg) ----
        // Input 0 stays Emby's source (audio, subtitles, metadata, chapters); input 1 is the concat list of the
        // encoded chunks, whose video replaces Emby's video map and is copied.
        var assembly = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a == "-re") continue;   // the video is already encoded: assemble as fast as possible
            if (i == iIndex)
            {
                assembly.AddRange(["-i", args[i + 1], "-f", "concat", "-safe", "0", "-i", ConversionPlan.ListPlaceholder]);
                i++;
                continue;
            }
            if (i > iIndex + 1 && i < args.Count - 1)
            {
                int o = i - (iIndex + 2);
                if (o == firstMap) { assembly.AddRange(["-map", "1:v:0"]); i++; continue; }
                if (a is "-c:v:0" or "-c:v" or "-vcodec" or "-codec:v:0") { assembly.AddRange([a, "copy"]); i++; continue; }
                if (IsVideoOption(a) && a != "-vtag" && !a.StartsWith("-metadata", StringComparison.Ordinal) && !a.StartsWith("-disposition", StringComparison.Ordinal))
                {
                    if (!Flags.Contains(a)) i++;
                    continue;
                }
            }
            assembly.Add(a);
        }

        var requirements = new JobRequirements(encoder, filters.Distinct().ToArray());
        string[] template = chunk.ToArray();
        if (!ArgTemplateValidator.Validate(template, out string? invalid))
            return Local($"commande non prise en charge : {invalid}");
        var audio = PlanAudioAhead(args, iIndex, outputSide, firstMap, assembly, out string? audioReason);
        return new ConversionPlan(true, "ok", input, output, template, assembly.ToArray(), requirements)
        {
            AudioAhead = audio,
            AudioAheadReason = audioReason,
        };
    }

    // ---- Audio converted ahead (see AudioAheadPlan) ----

    // Input formats whose timestamps ffmpeg never corrects for discontinuities: there, the audio's timestamps do not
    // depend on whether the video is read too (with MPEG-TS, a jump in the video would move the audio as well).
    // MPEG-TS stays out, measured on 2026-11-03 with generated files, both assemblies run as planned, ffmpeg 9.0.2 only
    // (Emby's 5.1, whose discontinuity handling is older, was not available). Start at 1.4 s, 33-bit wrap mid-file and a
    // 5 s gap: same file, byte for byte, except -copyts alone across the wrap (audio packets the same, but both assemblies
    // lose 40 % of them; the check of the prepared file refuses it). A 5000 s jump, forward, backward or with the audio
    // 12 s after the video: with -copyts each stream's audio packets are the same (pts, size, content) but the file is
    // not, the packets being in another order from the jump on, and the check of the prepared file lets the forward
    // jumps through. Not identical, and only a shorter assembly to gain on few files.
    private static readonly HashSet<string> AheadInputFormats = new(StringComparer.Ordinal)
    {
        "matroska,webm", "matroska", "webm", "mov,mp4,m4a,3gp,3g2,mj2", "mov", "mp4", "avi",
    };

    private static readonly HashSet<string> AheadInputExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".webm", ".mp4", ".m4v", ".mov", ".avi",
    };

    // Options that only shape the audio streams: without a specifier they apply to every audio stream.
    private static readonly HashSet<string> AudioOnly = new(StringComparer.Ordinal) { "-acodec", "-ab", "-ac", "-ar", "-af", "-aq" };

    // Options allowed with an audio stream specifier (":a", ":a:1", "-metadata:s:a:0"...).
    private static readonly HashSet<string> AudioSpecified = new(StringComparer.Ordinal)
    {
        "-c", "-codec", "-acodec", "-b", "-ab", "-ac", "-ar", "-af", "-aq", "-filter", "-q", "-qscale", "-profile",
        "-sample_fmt", "-channel_layout", "-ch_layout", "-cutoff", "-metadata", "-disposition",
    };

    // Options kept in the prepared run as they are (process-wide, or harmless without video).
    private static readonly HashSet<string> AheadGlobal = new(StringComparer.Ordinal)
    {
        "-y", "-copyts", "-start_at_zero", "-threads", "-max_muxing_queue_size", "-strict", "-sn", "-dn", "-nostdin",
    };

    // Output file options that belong to Emby's container: left to the assembly, not given to the prepared file.
    private static readonly HashSet<string> AheadContainer = new(StringComparer.Ordinal)
    {
        "-f", "-map_metadata", "-map_chapters", "-avoid_negative_ts", "-max_delay", "-movflags", "-metadata",
    };

    /// <summary>
    /// The audio converted ahead, when Emby's command lends itself to it; otherwise null and the reason (the usual
    /// assembly then converts the audio, as before). Deliberately narrow, limited to what was checked to give the same
    /// file as the usual assembly (byte for byte with ffmpeg 9): MP4 output, AAC encoded audio (other streams copied),
    /// source container whose timestamps ffmpeg leaves alone, nothing that ties the audio to the video (-shortest,
    /// output -t/-ss) and only options known to concern the audio or the container. The caller still checks the source
    /// streams (<see cref="AudioAheadPlan.SourceStreams"/> must be audio) before converting anything.
    /// </summary>
    private static AudioAheadPlan? PlanAudioAhead(IReadOnlyList<string> args, int iIndex, List<string> outputSide, int firstMap,
        List<string> assembly, out string? reason)
    {
        AudioAheadPlan? No(string why, out string? r)
        {
            r = why;
            return null;
        }

        // The input: a container whose timestamps are never corrected for discontinuities.
        string? inputFormat = null;
        for (int i = 0; i < iIndex; i++)
        {
            if (args[i] == "-f" && i + 1 < iIndex)
                inputFormat = args[i + 1];
            if (!Flags.Contains(args[i]))
                i++;
        }
        if (inputFormat is null ? !AheadInputExtensions.Contains(Path.GetExtension(args[iIndex + 1])) : !AheadInputFormats.Contains(inputFormat))
            return No($"format source {inputFormat ?? Path.GetExtension(args[iIndex + 1])}", out reason);

        // The output side, option by option: audio maps, the codec of each audio stream, and what goes where.
        var maps = new List<int>();
        var codecOptions = new List<(int At, string Spec)>();
        var prepare = new List<string>();
        bool audioDisposition = false, otherDisposition = false;
        string? outputFormat = null;
        for (int i = 0; i < outputSide.Count; i++)
        {
            string a = outputSide[i];
            bool hasValue = !Flags.Contains(a);
            string? value = hasValue && i + 1 < outputSide.Count ? outputSide[i + 1] : null;
            int colon = a.IndexOf(':');
            string name = colon < 0 ? a : a[..colon];
            string spec = colon < 0 ? "" : a[(colon + 1)..];
            if (a == "-map")
            {
                if (i != firstMap)
                {
                    if (value is null || !value.StartsWith("0:", StringComparison.Ordinal)
                        || !int.TryParse(value.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out int stream))
                        return No($"-map {value}", out reason);
                    maps.Add(stream);
                    prepare.AddRange([a, value]);
                }
            }
            else if (name == "-disposition" && !IsAudioSpec(spec))
                otherDisposition = true;
            else if (name == "-metadata" && spec.Length > 0 && spec != "g" && !IsAudioSpec(spec[(spec.StartsWith("s:", StringComparison.Ordinal) ? 2 : 0)..]))
            {
                // Stream metadata of the video (or by absolute index): the assembly's business only.
                if (!spec.StartsWith("s:v", StringComparison.Ordinal))
                    return No(a, out reason);
            }
            else if (IsVideoOption(a))
            {
                // The video's own options: the assembly copies the chunks.
            }
            else if (AudioOnly.Contains(a) || (AudioSpecified.Contains(name) && IsAudioSpec(name == "-metadata" ? spec.StartsWith("s:", StringComparison.Ordinal) ? spec[2..] : "" : spec)))
            {
                if (name is "-c" or "-codec" or "-acodec")
                    codecOptions.Add((i, name == "-acodec" ? "a" + (spec.Length > 0 ? ":" + spec : "") : spec));
                if (name == "-disposition")
                    audioDisposition = true;
                prepare.Add(a);
                if (value != null)
                    prepare.Add(value);
            }
            else if (AheadGlobal.Contains(a))
            {
                prepare.Add(a);
                if (hasValue && value != null)
                    prepare.Add(value);
            }
            else if (AheadContainer.Contains(name) && (name != "-metadata" || spec is "" or "g"))
            {
                if (a == "-f")
                    outputFormat = value;
            }
            else
                return No(a, out reason);
            if (hasValue)
                i++;
        }
        if (outputFormat != "mp4")
            return No($"format de sortie {outputFormat}", out reason);
        if (maps.Count == 0)
            return No("pas d'audio", out reason);
        // Dispositions given for the video only: ffmpeg would choose default audio streams in the prepared file by
        // itself, and not in the assembly.
        if (otherDisposition && !audioDisposition)
            return No("disposition de la vidéo", out reason);

        // Each audio stream's codec (the last matching option wins, as in ffmpeg): AAC encoded, or copied.
        var codecs = new string?[maps.Count];
        foreach (var (at, spec) in codecOptions)
        {
            string value = outputSide[at + 1];
            if (spec == "a")
                Array.Fill(codecs, value);
            else if (spec.StartsWith("a:", StringComparison.Ordinal)
                && int.TryParse(spec.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out int k) && k < maps.Count)
                codecs[k] = value;
            else
                return No($"codec audio {spec}", out reason);
        }
        if (codecs.Any(c => c is not ("aac" or "copy")))
            return No($"codec audio {codecs.First(c => c is not ("aac" or "copy")) ?? "par défaut"}", out reason);
        if (!codecs.Contains("aac"))
            return No("audio recopié", out reason);

        // ---- Prepared run: Emby's input, its audio streams and options, an MP4 file shifted by TimeShift ----
        var run = new List<string>();
        for (int i = 0; i < iIndex; i++)
        {
            string a = args[i];
            if (ServerOnly.Contains(a)) { i++; continue; }
            if (a == "-re") continue;
            run.Add(a);
            if (!Flags.Contains(a) && i + 1 < iIndex) run.Add(args[++i]);
        }
        run.AddRange(["-i", args[iIndex + 1]]);
        run.AddRange(prepare);
        run.AddRange(["-vn", "-map_chapters", "-1", "-avoid_negative_ts", "disabled",
            "-output_ts_offset", AudioAheadPlan.TimeShift.ToString(CultureInfo.InvariantCulture),
            "-movie_timescale", AudioAheadPlan.MovieTimescale, "-f", "mp4", AudioAheadPlan.AudioPlaceholder]);

        // ---- Assembly: the usual one, AAC streams copied from the prepared file (input 2) ----
        var final = new List<string>();
        int list = assembly.IndexOf(ConversionPlan.ListPlaceholder);
        int audioIndex = -1;
        bool seenVideoMap = false;
        for (int i = 0; i < assembly.Count; i++)
        {
            string a = assembly[i];
            final.Add(a);
            if (i == list)
            {
                final.AddRange(["-itsoffset", AudioAheadPlan.OffsetPlaceholder, "-i", AudioAheadPlan.AudioPlaceholder]);
                continue;
            }
            if (i < list || i == assembly.Count - 1)
                continue;
            int colon = a.IndexOf(':');
            string name = colon < 0 ? a : a[..colon];
            if (a == "-map" && i + 1 < assembly.Count)
            {
                if (!seenVideoMap)
                    seenVideoMap = true;   // "1:v:0", the chunks
                else if (codecs[++audioIndex] == "aac")
                {
                    final.Add($"2:{audioIndex}");
                    i++;
                    continue;
                }
            }
            else if ((name is "-c" or "-codec" && colon > 0 && a[(colon + 1)..].StartsWith('a')) || name == "-acodec")
            {
                // Copied: the prepared file holds the converted audio (a copied stream stays a copy of the source).
                final.Add("copy");
                i++;
                continue;
            }
            else if (a == "-af" || (name == "-filter" && colon > 0 && a[(colon + 1)..].StartsWith('a')))
            {
                // Already applied; ffmpeg refuses filters on copied streams.
                final.RemoveAt(final.Count - 1);
                i++;
                continue;
            }
            if (!Flags.Contains(a) && i + 1 < assembly.Count - 1)
                final.Add(assembly[++i]);
        }
        reason = null;
        return new AudioAheadPlan(run.ToArray(), final.ToArray(), maps.ToArray(), args.Contains("-copyts"), args.Contains("-start_at_zero"));

        static bool IsAudioSpec(string spec) => spec == "a" || spec.StartsWith("a:", StringComparison.Ordinal);
    }

    // ---- Where the audio starts (see SourceInfo.VerifyAudioStarts) ----

    // Audio filters that move sound in time (or can: aresample with async).
    private static readonly string[] AudioMovingFilters = ["aresample", "asetpts", "atrim", "adelay", "atempo"];

    // Source containers with exact times, whose streams start where they say. Not MPEG-TS (cut anywhere, a stream's first
    // packets may not decode, and ffmpeg corrects its times) nor AVI (times may be approximate).
    private static readonly HashSet<string> AudioStartInputs = new(StringComparer.Ordinal)
    {
        "matroska,webm", "matroska", "webm", "mov,mp4,m4a,3gp,3g2,mj2", "mov", "mp4",
    };

    private static readonly HashSet<string> AudioStartInputExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mkv", ".webm", ".mp4", ".m4v", ".mov" };

    // Output containers whose audio start was measured to read as the source's. An MPEG-TS result starts at 1.4 s, for
    // instance; others were not measured.
    private static readonly HashSet<string> AudioStartOutputs = new(StringComparer.Ordinal) { "mp4", "mov", "matroska" };

    /// <summary>
    /// Why the audio's start cannot be compared between a source and the result of this assembly (null: it can). Refused: what
    /// moves the audio on Emby's own request (-async fills from 0, input or output offsets and seeks, -copyts keeping the file's
    /// start, audio filters working on time, -avoid_negative_ts moving every stream), and the containers left out above.
    /// -shortest does not count: it cuts the end, not the start.
    /// </summary>
    public static string? WhyAudioStartIsNotCompared(IReadOnlyList<string> assembly)
    {
        int firstInput = IndexOf(assembly, "-i");
        int lastInput = assembly.ToList().LastIndexOf("-i");
        if (firstInput < 0 || lastInput + 2 >= assembly.Count)
            return "commande inattendue";
        string? inputFormat = null, outputFormat = null;
        bool copyTs = false, startAtZero = false;
        for (int i = 0; i < assembly.Count - 1; i++)
        {
            string a = assembly[i];
            string next = assembly[i + 1];
            if (a is "-async" or "-ss" or "-sseof" or "-itsoffset" or "-output_ts_offset")
                return a;
            if (a == "-copyts")
                copyTs = true;
            else if (a == "-start_at_zero")
                startAtZero = true;
            else if (a == "-f" && i < firstInput)
                inputFormat = next;
            else if (a == "-f" && i > lastInput)
                outputFormat = next;
            else if (a == "-avoid_negative_ts" && next != "disabled")
                return $"{a} {next}";
            else if ((a == "-af" || a.StartsWith("-filter:a", StringComparison.Ordinal)) && AudioMovingFilters.Any(f => next.Contains(f, StringComparison.Ordinal)))
                return "filtre audio";
        }
        if (copyTs && !startAtZero)
            return "-copyts sans -start_at_zero";
        string input = assembly[firstInput + 1];
        if (inputFormat is null ? !AudioStartInputExtensions.Contains(Path.GetExtension(input)) : !AudioStartInputs.Contains(inputFormat))
            return $"format source {inputFormat ?? Path.GetExtension(input)}";
        return outputFormat != null && AudioStartOutputs.Contains(outputFormat) ? null : $"format de sortie {outputFormat ?? "inconnu"}";
    }

    /// <summary>
    /// The source streams an assembly maps, in order, without the chunks' video. Null when a map is written another way
    /// than "0:N" (the streams it means are then not known here).
    /// </summary>
    public static IReadOnlyList<int>? AssemblySourceMaps(IReadOnlyList<string> assembly)
    {
        var maps = new List<int>();
        for (int i = 0; i + 1 < assembly.Count; i++)
        {
            if (assembly[i] != "-map" || assembly[i + 1] == "1:v:0")
                continue;
            string map = assembly[i + 1];
            if (!map.StartsWith("0:", StringComparison.Ordinal) || !int.TryParse(map.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out int stream))
                return null;
            maps.Add(stream);
        }
        return maps;
    }

    /// <summary>Shortest chunk a run is planned to end with (see <see cref="ChunkArgs"/>).</summary>
    public const double MinChunkSeconds = 2;

    /// <summary>
    /// Margin, in seconds, below a frame's time: far below any frame duration, far above timestamp rounding.
    /// </summary>
    public const double FrameMargin = 0.001;

    /// <summary>Largest margin below a frame's time (see <see cref="SeekMargin"/>).</summary>
    public const double MaxSeekMargin = 0.05;

    /// <summary>
    /// How far below a frame's time a run's -ss and -t are set: half the smallest gap seen between the source's frames,
    /// so the run aims between two frames. ffmpeg counts -ss from the file's start, and two ffmpeg builds may not agree
    /// on where a file starts: Emby's 5.1, which sets the boundaries, saw start 0 on a file where the workers' 9.0 saw
    /// 0.005 s (both its streams start there). With a 1 ms margin, 5 ms was enough for a worker to skip the first frame of
    /// its slice and, its -t counting from the first frame kept, to end on the next slice's first frame; its chunks,
    /// listed one frame late, then made the assembled video too long (+21 frames, issue #3). Halfway between two frames,
    /// such a difference selects the same frames. Without a usable gap, <see cref="FrameMargin"/>.
    /// </summary>
    public static double SeekMargin(double? smallestFrameGap) =>
        smallestFrameGap is > 0 and var gap ? Math.Clamp(gap / 2, FrameMargin, MaxSeekMargin) : FrameMargin;

    /// <summary>
    /// Chunk command for a run whose first frame is at <paramref name="start"/> (a frame's time) and, for a slice that
    /// is not the last one, whose last frame is the one before <paramref name="end"/> (also a frame's time). A run
    /// from the source's first frame does not seek (<paramref name="seek"/> false). Chunks are numbered from
    /// <paramref name="index"/>. <paramref name="length"/>, the expected length of the run, avoids a tiny last chunk.
    /// </summary>
    public static string[] ChunkArgs(IReadOnlyList<string> template, int index, double start, bool seek, double? end = null, double? length = null, double margin = FrameMargin)
    {
        var args = template.ToList();
        // A run a little longer than a whole number of chunks would end with a chunk of a few frames: longer chunks
        // instead, the remainder spread over them (a lone tiny chunk once went missing, 3 frames short at the end).
        if (length is > ChunkSeconds and var l && l % ChunkSeconds < MinChunkSeconds)
        {
            int at = args.IndexOf("-segment_time");
            if (at >= 0 && at + 1 < args.Count)
                args[at + 1] = Time(l / Math.Floor(l / ChunkSeconds) + MinChunkSeconds);
        }
        // Input options before the (first) "-i". ffmpeg keeps the frames from the first one at or after -ss, and -t
        // counts from that first kept frame. Both ends are set below a frame's time (see SeekMargin), so a run keeps exactly the
        // frames from its start frame up to the one before its end frame, and two runs that meet at the same frame
        // neither overlap nor leave a gap. Numbering from the resumed chunk keeps file names continuous.
        var input = new List<string>();
        if (seek)
            input.AddRange(["-ss", Time(Math.Max(0, start - margin))]);
        if (end is { } e)
            input.AddRange(["-t", Time(Math.Max(FrameMargin, e - start - margin))]);
        args.InsertRange(args.IndexOf("-i"), input);
        args.InsertRange(args.Count - 1, ["-segment_start_number", index.ToString(CultureInfo.InvariantCulture)]);
        return args.ToArray();
    }

    /// <summary>
    /// The assembly with the video put back where it starts in the source. The concat demuxer starts the chunks at 0 whatever
    /// their timestamps, so a video starting after the file's start came out that much ahead of its audio: 79 ms on a source
    /// whose audio starts at 0 and video at 0.079 s, measured on 2026-11-02 against the source's scene changes, and 7 ms in
    /// the bench's lip-sync test. Null: nothing to do.
    /// </summary>
    public static string[] WithVideoOffset(IReadOnlyList<string> assembly, string? offset)
    {
        var args = assembly.ToList();
        int concat = args.IndexOf("concat");
        if (offset != null && concat > 0 && args[concat - 1] == "-f")
            args.InsertRange(concat - 1, ["-itsoffset", offset]);
        return args.ToArray();
    }

    /// <summary>
    /// How much a worker's -ss must move, given the file's start as its ffmpeg and as the server's read it; 0 when they
    /// agree. Under -copyts -start_at_zero, ffmpeg keeps the frames whose time minus the file's start reaches -ss, and two
    /// builds may read different starts: Emby's 5.1 read 0 on a file where the workers' 9.0 read 0.021 s, more than half a
    /// frame at 25 fps, so the seek margin (<see cref="SeekMargin"/>) was not enough: every worker slice began one frame
    /// late, with a repeated or skipped frame where it met a server slice, and a last slice on a worker lost its last
    /// frame (seen on 2026-11-02). A difference over a few seconds is not this: left alone, the check of the file decides.
    /// </summary>
    public static double SeekCorrection(double serverStart, double ownStart)
    {
        double difference = ownStart - serverStart;
        return Math.Abs(difference) is >= 0.0005 and <= 5 ? difference : 0;
    }

    /// <summary>The arguments with the input -ss moved down by <paramref name="correction"/> (never below 0).</summary>
    public static string[] ShiftSeek(IReadOnlyList<string> args, double correction)
    {
        var shifted = args.ToArray();
        int input = Array.IndexOf(shifted, "-i");
        for (int i = 0; i + 1 < input; i++)
            if (shifted[i] == "-ss" && double.TryParse(shifted[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double ss))
            {
                shifted[i + 1] = Time(Math.Max(0, ss - correction));
                break;
            }
        return shifted;
    }

    // Microseconds: the precision ffprobe prints timestamps with.
    private static string Time(double seconds) => seconds.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>
    /// Target source length of a slice: the unit of work one worker takes in a conversion. Short, so the work spreads
    /// over many workers and the end of a conversion never waits long for a slow one; long enough that the cost of
    /// starting a run (opening the source, decoding up to the first frame) stays small.
    /// </summary>
    public const double SliceSeconds = 120;

    /// <summary>Chunk numbers reserved per slice (slice k numbers its chunks from k × this).</summary>
    public const int ChunksPerSlice = 100;

    /// <summary>Upper bound on slices, so chunk numbers keep five digits.</summary>
    public const int MaxSlices = 999;

    /// <summary>
    /// Splits a conversion into slices that different workers encode at the same time: about <see cref="SliceSeconds"/>
    /// each, then shorter ones at the end (60, 60, 30, 30 s), so the last pieces spread over the workers and a slow one
    /// never keeps the others waiting for long. These are nominal times: the coordinator moves each boundary to the
    /// first frame at or after it. The last slice is open-ended: it runs to the end of the source whatever its probed
    /// duration says.
    /// </summary>
    public static IReadOnlyList<(double Start, double End, bool Last)> Slices(double duration)
    {
        if (duration <= 0)
            return [(0, 0, true)];
        double[] tail = duration > 600 ? [60, 60, 30, 30] : duration > 240 ? [30, 30] : [];
        double body = duration - tail.Sum();
        int n = (int)Math.Clamp(Math.Round(body / SliceSeconds), 1, MaxSlices - tail.Length);
        var starts = new List<double>();
        for (int i = 0; i < n; i++)
            starts.Add(Math.Round(i * body / n, 3));
        double t = body;
        foreach (double length in tail)
        {
            starts.Add(Math.Round(t, 3));
            t += length;
        }
        return starts.Select((s, i) => (s, i + 1 < starts.Count ? starts[i + 1] : duration, i + 1 == starts.Count)).ToList();
    }

    /// <summary>
    /// Walks Emby's command (before the output path) as option/value pairs: a known flag stands alone, any other
    /// option must be followed by a value, i.e. an argument that is not itself an option (negative numbers such as
    /// "-map_metadata -1" are values). Returns the first argument that does not fit, null when all do. Refusing is
    /// safer than guessing: an unknown value-less flag would otherwise swallow the next option.
    /// </summary>
    private static string? CheckOptions(IReadOnlyList<string> args)
    {
        int last = args.Count - 1;   // the output path
        if (last < 1)
            return "commande vide";
        for (int i = 0; i < last; i++)
        {
            string a = args[i];
            if (!IsOption(a))
                return a;                       // a value without option
            if (Flags.Contains(a))
                continue;
            if (i + 1 >= last || IsOption(args[i + 1]))
                return a;                       // an option without value (unknown flag, or value missing)
            i++;
        }
        return null;
    }

    private static bool IsOption(string a) =>
        a.Length > 1 && a[0] == '-' && !double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static bool IsVideoOption(string a)
    {
        string name = a.Split(':')[0];
        if (name is "-filter_complex" || VideoGeneric.Contains(a))
            return true;
        // Stream-specified options: -crf:v:0, -maxrate:v:0, -filter:v:0, -c:v:0 ...
        return a.Contains(":v", StringComparison.Ordinal) && !name.StartsWith("-metadata", StringComparison.Ordinal) && name != "-disposition"
            || a is "-vtag";
    }

    // Conservative: any audio stream label or filter name starting with 'a' (anull, aresample...) keeps the conversion
    // on the server, since the chunks carry video only.
    private static bool FilterTouchesAudio(string graph) =>
        graph.Contains(":a", StringComparison.Ordinal) || ArgTemplateValidator.FilterNames(graph).Any(f => f.StartsWith('a') && f is not ("aspect"));

    private static int IndexOf(IReadOnlyList<string> args, string value)
    {
        for (int i = 0; i < args.Count; i++)
            if (args[i] == value)
                return i;
        return -1;
    }
}
