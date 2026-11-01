// Faux « ffmpeg d'Emby » pour le banc de test local (C:\emby\test\local).
//
// Le relais remote-ffmpeg suit les segments produits par le ffmpeg du serveur grâce aux lignes stderr
// « SegmentComplete=... » que seul le ffmpeg modifié d'Emby écrit, et le ffmpeg amont refuse les options propres au
// fork (-loglevel +timing, -segment_write_temp, -print_graphs_file, -min_frame_time, -write_empty_segments...).
// Ce programme :
//   * retire ces options (avec leur valeur) et lance le ffmpeg amont avec les arguments restants ;
//   * transmet stdin (touche 'q') au ffmpeg amont, et rend son code de sortie ;
//   * pour une sortie HLS segmentée (-f segment ... -segment_list <liste>), fait écrire au ffmpeg amont une liste CSV
//     (nom,début,fin en secondes absolues, comme le muxer du fork), maintient lui-même la liste m3u8 demandée et écrit
//     sur stderr, pour chaque segment fermé, la ligne SegmentComplete au format du fork ;
//   * relaie le stderr du ffmpeg amont (progression « frame= ... time= ... speed= ») ligne par ligne ;
//   * peut simuler un serveur lent : -readrate (débit de lecture relatif au temps réel) et/ou un délai de démarrage.
//
// Réglages : fichier fake-emby-ffmpeg.json à côté de l'exécutable (lu à chaque lancement), puis variables
// d'environnement (prioritaires) :
//   RealFfmpeg / FAKE_EMBY_FFMPEG_REAL             ffmpeg amont à lancer (obligatoire)
//   ReadRate / FAKE_EMBY_FFMPEG_READRATE            ex. 0.8 : ajoute « -readrate 0.8 » avant le premier -i (0 = non)
//   ReadRateInitialBurst / FAKE_EMBY_FFMPEG_BURST   ex. 0 : « -readrate_initial_burst » (défaut ffmpeg : 0.5 s)
//   StartDelayMs / FAKE_EMBY_FFMPEG_START_DELAY_MS  attente avant de lancer ffmpeg (serveur qui tarde à démarrer)
//   StartZero / FAKE_EMBY_FFMPEG_START_ZERO          "first" (défaut : Start tel que le muxer le donne, 0 au 1er
//                                                   segment d'un run avec -ss), "all" (Start=0 partout), "none"
//                                                   (Start = fin du segment précédent ou -ss)
//   LogFile / FAKE_EMBY_FFMPEG_LOG                  journal des lancements (arguments, code de sortie, segments)
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FakeEmbyFfmpeg;

public sealed class FakeSettings
{
    public string? RealFfmpeg { get; set; }
    public double ReadRate { get; set; }
    public double? ReadRateInitialBurst { get; set; }
    public int StartDelayMs { get; set; }
    public string StartZero { get; set; } = "first";
    public string? LogFile { get; set; }
    /// <summary>Mode ffprobe (arguments -show_streams/-show_entries...) : ffprobe amont à lancer (défaut : à côté de RealFfmpeg).</summary>
    public string? RealFfprobe { get; set; }
    /// <summary>Mode ffprobe : attente avant de lancer ffprobe (serveur chargé, disque lent...).</summary>
    public int ProbeDelayMs { get; set; }

    public static FakeSettings Load()
    {
        var s = new FakeSettings();
        string file = Path.Combine(AppContext.BaseDirectory, "fake-emby-ffmpeg.json");
        try
        {
            if (File.Exists(file))
                s = JsonSerializer.Deserialize<FakeSettings>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? s;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"fake-emby-ffmpeg: réglages illisibles ({ex.Message})");
        }
        string? Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v : null;
        if (Env("FAKE_EMBY_FFMPEG_REAL") is { } real) s.RealFfmpeg = real;
        if (Env("FAKE_EMBY_FFMPEG_READRATE") is { } rr) s.ReadRate = double.Parse(rr, CultureInfo.InvariantCulture);
        if (Env("FAKE_EMBY_FFMPEG_BURST") is { } b) s.ReadRateInitialBurst = double.Parse(b, CultureInfo.InvariantCulture);
        if (Env("FAKE_EMBY_FFMPEG_START_DELAY_MS") is { } d) s.StartDelayMs = int.Parse(d, CultureInfo.InvariantCulture);
        if (Env("FAKE_EMBY_FFMPEG_START_ZERO") is { } z) s.StartZero = z;
        if (Env("FAKE_EMBY_FFMPEG_LOG") is { } l) s.LogFile = l;
        if (Env("FAKE_EMBY_FFMPEG_PROBE_DELAY_MS") is { } pd) s.ProbeDelayMs = int.Parse(pd, CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(s.RealFfprobe) && !string.IsNullOrEmpty(s.RealFfmpeg))
            s.RealFfprobe = Path.Combine(Path.GetDirectoryName(s.RealFfmpeg) ?? "", "ffprobe.exe");
        return s;
    }
}

public static class Program
{
    // Options propres au fork d'Emby (toutes suivies d'une valeur), refusées par le ffmpeg amont.
    private static readonly HashSet<string> ForkOptionsWithValue = new(StringComparer.Ordinal)
    {
        "-segment_write_temp", "-print_graphs_file", "-min_frame_time", "-write_empty_segments",
    };

    private static readonly object ErrGate = new();
    private static readonly Stream Stderr = Console.OpenStandardError();
    private static FakeSettings settings = new();
    private static bool timing;
    private static volatile bool quit;

    public static int Main(string[] args)
    {
        settings = FakeSettings.Load();
        // Mode ffprobe (le coordinateur peut être configuré avec cet exécutable comme ServerFfprobe) : ffprobe amont
        // lancé après ProbeDelayMs, sorties héritées.
        if (args.Any(a => a.StartsWith("-show_", StringComparison.Ordinal)) && !args.Contains("-f"))
        {
            Log($"probe pid={Environment.ProcessId} delay={settings.ProbeDelayMs} : {Join(args)}");
            if (settings.ProbeDelayMs > 0)
                Thread.Sleep(settings.ProbeDelayMs);
            var probe = new ProcessStartInfo(settings.RealFfprobe!) { UseShellExecute = false };
            foreach (string a in args)
                probe.ArgumentList.Add(a);
            using var pp = Process.Start(probe)!;
            KillWithMe.Attach(pp);
            pp.WaitForExit();
            Log($"probe-end pid={Environment.ProcessId} code={pp.ExitCode} ms={(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds:0}");
            return pp.ExitCode;
        }
        if (string.IsNullOrEmpty(settings.RealFfmpeg) || !File.Exists(settings.RealFfmpeg))
        {
            Line($"fake-emby-ffmpeg: ffmpeg amont introuvable ({settings.RealFfmpeg ?? "RealFfmpeg non réglé"})");
            return 1;
        }

        var plan = Rewrite(args);
        Log($"start pid={Environment.ProcessId} readrate={settings.ReadRate.ToString(CultureInfo.InvariantCulture)} delay={settings.StartDelayMs} segment={(plan.Segment != null)}\n  in : {Join(args)}\n  out: {Join(plan.Args)}");

        Process? child = null;
        // stdin : lu ici dès le départ (un 'q' reçu pendant le délai de démarrage arrête tout), puis transmis à ffmpeg.
        var stdinThread = new Thread(() =>
        {
            var input = Console.OpenStandardInput();
            var buf = new byte[1];
            try
            {
                while (input.Read(buf, 0, 1) == 1)
                {
                    if (buf[0] == (byte)'q')
                        quit = true;
                    var c = child;
                    if (c == null)
                        continue;
                    try
                    {
                        c.StandardInput.BaseStream.WriteByte(buf[0]);
                        c.StandardInput.BaseStream.Flush();
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }) { IsBackground = true, Name = "stdin" };
        stdinThread.Start();

        if (settings.StartDelayMs > 0)
        {
            var until = DateTime.UtcNow.AddMilliseconds(settings.StartDelayMs);
            while (DateTime.UtcNow < until && !quit)
                Thread.Sleep(20);
            if (quit)
            {
                Raw("\n\n[q] command received. Exiting.\n\n");
                Log($"exit pid={Environment.ProcessId} code=0 ('q' pendant le délai de démarrage)");
                return 0;
            }
        }

        var psi = new ProcessStartInfo(settings.RealFfmpeg)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
        };
        foreach (string a in plan.Args)
            psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        KillWithMe.Attach(p);
        child = p;
        if (quit)
        {
            try { p.StandardInput.BaseStream.WriteByte((byte)'q'); p.StandardInput.BaseStream.Flush(); } catch (Exception) { }
        }

        var watcher = plan.Segment is { } seg ? new SegmentWatcher(seg) : null;
        var watchThread = watcher == null ? null : new Thread(() => watcher.Loop(p)) { IsBackground = true, Name = "segments" };
        watchThread?.Start();

        // stderr de ffmpeg : relayé par lignes entières (terminées par \r ou \n) pour ne jamais couper une ligne
        // par une ligne SegmentComplete.
        var reader = p.StandardError;
        var line = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                line.Append(buffer[i]);
                if (buffer[i] is '\n' or '\r')
                {
                    Raw(line.ToString());
                    line.Clear();
                }
            }
        }
        if (line.Length > 0)
            Raw(line.ToString());
        p.WaitForExit();
        int code = p.ExitCode;
        watchThread?.Join(2000);
        watcher?.Finish(code);
        Log($"exit pid={Environment.ProcessId} code={code} segments={watcher?.Count ?? 0}");
        return code;
    }

    public sealed record SegmentPlan(string ListPath, string CsvPath, string Pattern, int StartNumber, double SessionStart, double SegmentSeconds);

    public sealed record Plan(List<string> Args, SegmentPlan? Segment);

    /// <summary>Retire les options du fork, branche la liste CSV et le ralentissement.</summary>
    public static Plan Rewrite(string[] args)
    {
        var a = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string name = args[i].Split(':')[0];
            if ((args[i] == "-loglevel" || args[i] == "-v") && i + 1 < args.Length)
            {
                // "+timing" (ou "x+timing") : préfixe horaire du fork ; on garde le reste pour ffmpeg amont.
                string v = args[i + 1];
                if (v.Contains("timing", StringComparison.Ordinal))
                {
                    timing = true;
                    string rest = string.Join("+", v.Split('+', StringSplitOptions.RemoveEmptyEntries).Where(f => f != "timing"));
                    if (rest.Length > 0)
                    {
                        a.Add(args[i]);
                        a.Add(rest);
                    }
                    i++;
                    continue;
                }
            }
            if (ForkOptionsWithValue.Contains(name) && i + 1 < args.Length)
            {
                i++;
                continue;
            }
            a.Add(args[i]);
        }

        if (settings.ReadRate > 0)
        {
            int firstInput = a.IndexOf("-i");
            if (firstInput >= 0)
            {
                var extra = new List<string> { "-readrate", settings.ReadRate.ToString(CultureInfo.InvariantCulture) };
                if (settings.ReadRateInitialBurst is { } burst)
                    extra.AddRange(["-readrate_initial_burst", burst.ToString(CultureInfo.InvariantCulture)]);
                a.InsertRange(firstInput, extra);
            }
        }

        SegmentPlan? seg = null;
        int f = FindPair(a, "-f", "segment");
        int list = a.LastIndexOf("-segment_list");
        if (f >= 0 && list >= 0 && list + 1 < a.Count)
        {
            string listPath = a[list + 1];
            int type = a.LastIndexOf("-segment_list_type");
            string listType = type >= 0 && type + 1 < a.Count ? a[type + 1] : (listPath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? "m3u8" : "flat");
            if (listType == "m3u8")
            {
                string csv = listPath + ".fake.csv";
                try { File.Delete(csv); } catch (Exception) { }
                a[list + 1] = csv;
                if (type >= 0)
                    a[type + 1] = "csv";
                else
                    a.InsertRange(list + 2, ["-segment_list_type", "csv"]);
                int start = a.LastIndexOf("-segment_start_number");
                int startNumber = start >= 0 && start + 1 < a.Count && int.TryParse(a[start + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
                int ss = a.IndexOf("-ss");
                int firstInput = a.IndexOf("-i");
                double sessionStart = ss >= 0 && ss < firstInput ? ParseTime(a[ss + 1]) : 0;
                int st = a.LastIndexOf("-segment_time");
                double segSeconds = st >= 0 && st + 1 < a.Count ? ParseTime(a[st + 1]) : 2;
                seg = new SegmentPlan(listPath, csv, a[^1], startNumber, sessionStart, segSeconds);
            }
        }
        return new Plan(a, seg);
    }

    private static int FindPair(List<string> a, string name, string value)
    {
        for (int i = 0; i + 1 < a.Count; i++)
            if (a[i] == name && a[i + 1] == value)
                return i;
        return -1;
    }

    public static double ParseTime(string s)
    {
        if (TimeSpan.TryParseExact(s, [@"hh\:mm\:ss\.fff", @"hh\:mm\:ss", @"hh\:mm\:ss\.FFFFFFF"], CultureInfo.InvariantCulture, out var ts))
            return ts.TotalSeconds;
        double total = 0;
        foreach (string part in s.Split(':'))
            total = total * 60 + double.Parse(part, CultureInfo.InvariantCulture);
        return total;
    }

    /// <summary>Ligne stderr (préfixe horaire du fork si -loglevel +timing était demandé).</summary>
    public static void Line(string text) => Raw((timing ? DateTime.Now.ToString("HH:mm:ss.fff ", CultureInfo.InvariantCulture) : "") + text + "\n");

    public static void Raw(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        lock (ErrGate)
        {
            Stderr.Write(bytes);
            Stderr.Flush();
        }
    }

    private static readonly object LogGate = new();

    public static void Log(string text)
    {
        if (string.IsNullOrEmpty(settings.LogFile))
            return;
        try
        {
            lock (LogGate)
            {
                using var fs = new FileStream(settings.LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                byte[] b = Encoding.UTF8.GetBytes($"{DateTime.Now:HH:mm:ss.fff} {text}\n");
                fs.Write(b);
            }
        }
        catch (Exception) { }
    }

    private static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(x => x.Contains(' ') || x.Length == 0 ? $"\"{x}\"" : x));

    /// <summary>
    /// Suit la liste CSV écrite par le ffmpeg amont : une ligne « nom,début,fin » par segment fermé. Pour chacune :
    /// liste m3u8 réécrite (fichier temporaire + renommage), puis ligne SegmentComplete sur stderr.
    /// </summary>
    private sealed class SegmentWatcher(SegmentPlan plan)
    {
        private readonly List<(string Name, double Duration)> entries = [];
        private readonly Regex indexOf = new("^" + Regex.Escape(Path.GetFileName(plan.Pattern)).Replace("%d", @"(\d+)") + "$");
        private int lines;
        private double prevEnd = -1;
        private readonly object gate = new();
        public int Count => entries.Count;

        public void Loop(Process p)
        {
            while (!p.HasExited)
            {
                Poll(final: false);
                Thread.Sleep(40);
            }
        }

        public void Finish(int exitCode)
        {
            Poll(final: true);
            if (exitCode == 0)
                WriteM3u8(endList: true);
        }

        private void Poll(bool final)
        {
            lock (gate)
            {
                string text;
                try
                {
                    if (!File.Exists(plan.CsvPath))
                        return;
                    using var fs = new FileStream(plan.CsvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var r = new StreamReader(fs);
                    text = r.ReadToEnd();
                }
                catch (IOException)
                {
                    return;
                }
                // Seules les lignes terminées par \n sont complètes.
                var all = text.Split('\n');
                int complete = all.Length - 1;
                for (; lines < complete; lines++)
                {
                    string l = all[lines].Trim('\r', ' ');
                    if (l.Length == 0)
                        continue;
                    // Le nom peut contenir des virgules (entre guillemets) : on lit début et fin depuis la droite.
                    int c2 = l.LastIndexOf(',');
                    int c1 = c2 > 0 ? l.LastIndexOf(',', c2 - 1) : -1;
                    if (c1 < 0)
                        continue;
                    string name = l[..c1].Trim('"');
                    double start = double.Parse(l[(c1 + 1)..c2], CultureInfo.InvariantCulture);
                    double end = double.Parse(l[(c2 + 1)..], CultureInfo.InvariantCulture);
                    int index = indexOf.Match(name) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : plan.StartNumber + entries.Count;
                    double realStart = prevEnd >= 0 ? prevEnd : plan.SessionStart;
                    double shown = settings.StartZero switch
                    {
                        "all" => 0,
                        "none" => realStart,
                        _ => start,
                    };
                    entries.Add((name, Math.Max(0, end - realStart)));
                    prevEnd = end;
                    WriteM3u8(endList: false);
                    Line(string.Create(CultureInfo.InvariantCulture,
                        $"SegmentComplete=video:0 Index={index} Start={shown:F6} End={end:F6} Duration={end - shown:F6} offset_pts=0 start_pts={(long)Math.Round(shown * 1_000_000)} Frames=0 filename={name}"));
                }
            }
        }

        private void WriteM3u8(bool endList)
        {
            try
            {
                var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-MEDIA-SEQUENCE:" + plan.StartNumber.ToString(CultureInfo.InvariantCulture) + "\n#EXT-X-ALLOW-CACHE:YES\n");
                double max = entries.Count == 0 ? plan.SegmentSeconds : Math.Max(plan.SegmentSeconds, entries.Max(e => e.Duration));
                sb.Append(CultureInfo.InvariantCulture, $"#EXT-X-TARGETDURATION:{(int)Math.Ceiling(max)}\n");
                foreach (var (n, d) in entries)
                    sb.Append(CultureInfo.InvariantCulture, $"#EXTINF:{d:F6},\n{n}\n");
                if (endList)
                    sb.Append("#EXT-X-ENDLIST\n");
                string tmp = plan.ListPath + ".fake.tmp";
                File.WriteAllText(tmp, sb.ToString());
                File.Move(tmp, plan.ListPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Line($"fake-emby-ffmpeg: liste {plan.ListPath} non écrite ({ex.Message})");
            }
        }
    }
}

/// <summary>Objet de travail Windows : le ffmpeg amont meurt avec ce processus, même tué sans son arborescence.</summary>
internal static class KillWithMe
{
    private static IntPtr job;

    public static void Attach(Process p)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            if (job == IntPtr.Zero)
            {
                job = CreateJobObject(IntPtr.Zero, null);
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                IntPtr ptr = Marshal.AllocHGlobal(size);
                Marshal.StructureToPtr(info, ptr, false);
                SetInformationJobObject(job, 9, ptr, (uint)size);
                Marshal.FreeHGlobal(ptr);
            }
            AssignProcessToJobObject(job, p.Handle);
        }
        catch (Exception) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
