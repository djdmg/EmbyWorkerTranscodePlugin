using System.Diagnostics;
using System.Management;
using RemoteTranscode.Common;

namespace RemoteTranscode.Worker;

/// <summary>
/// Measures what this PC can really do: each encoder ffmpeg lists is exercised with a one-second encode, because a
/// build can list nvenc/amf/qsv encoders that the installed hardware or driver cannot run. Hardware decoding is
/// likewise tried on short generated clips, codec by codec, on the GPU of each validated hardware encoder.
/// </summary>
public static class CapabilityProbe
{
    private static readonly string[] CandidateEncoders =
    [
        "h264_nvenc", "hevc_nvenc", "av1_nvenc",
        "h264_qsv", "hevc_qsv", "av1_qsv",
        "h264_amf", "hevc_amf", "av1_amf",
        "libx264", "libx265",
    ];

    private static readonly string[] RequiredFilters = ["scale", "subtitles", "zscale", "tonemap", "format", "overlay"];

    public static async Task<WorkerCapabilities> RunAsync(string ffmpeg, Action<string> log)
    {
        string version = (await Run(ffmpeg, "-hide_banner -version", 10)).Output.Split('\n').FirstOrDefault()?.Trim() ?? "?";
        string encodersList = (await Run(ffmpeg, "-hide_banner -encoders", 10)).Output;
        string filtersList = (await Run(ffmpeg, "-hide_banner -filters", 10)).Output;

        var validated = new List<string>();
        foreach (string encoder in CandidateEncoders.Where(e => encodersList.Contains(" " + e + " ", StringComparison.Ordinal)))
        {
            // 8-bit 720p test pattern, one second: enough to open the hardware session and encode real frames.
            var result = await Run(ffmpeg, $"-hide_banner -loglevel error -f lavfi -i testsrc2=size=1280x720:rate=30 -t 1 -pix_fmt yuv420p -c:v {encoder} -f null -", 30);
            log($"encoder {encoder}: {(result.ExitCode == 0 ? "OK" : "unusable (" + FirstLine(result.Output) + ")")}");
            if (result.ExitCode == 0)
                validated.Add(encoder);
        }

        string[] hwDecoders = await HardwareDecodersAsync(ffmpeg, encodersList, validated, log);

        return new WorkerCapabilities
        {
            FfmpegVersion = version,
            VideoEncoders = validated.ToArray(),
            HwDecoders = hwDecoders,
            Filters = ParseFilters(filtersList),
            Gpus = Gpus(),
            LogicalCpus = Environment.ProcessorCount,
            TotalMemoryBytes = SystemLoad.TotalMemory(),
        };
    }

    /// <summary>
    /// For each GPU family with a validated hardware encoder, the codecs it really decodes: a short clip made with the
    /// software encoder is decoded with that family's method, frames kept on the GPU. hwdownload only accepts GPU frames,
    /// so a silent fallback to software decoding (codec not supported by the GPU) fails the test.
    /// </summary>
    private static async Task<string[]> HardwareDecodersAsync(string ffmpeg, string encodersList, IReadOnlyCollection<string> encoders, Action<string> log)
    {
        var families = HardwareDecoding.Families.Where(f => encoders.Any(e => HardwareDecoding.FamilyOf(e) == f)).ToList();
        if (families.Count == 0)
            return [];
        var validated = new List<string>();
        string dir = Path.Combine(Path.GetTempPath(), "EmbyRemoteWorker-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (codec, softwareEncoder) in HardwareDecoding.Codecs)
            {
                if (!encodersList.Contains(" " + softwareEncoder + " ", StringComparison.Ordinal))
                    continue;
                // Half a second of 8-bit 360p: above every decoder's minimum size, quick to make even with libaom.
                string clip = Path.Combine(dir, codec + ".mkv");
                string speed = softwareEncoder switch
                {
                    "libx264" or "libx265" => "-preset ultrafast",
                    "libaom-av1" => "-cpu-used 8 -row-mt 1",
                    "libvpx-vp9" => "-deadline realtime -cpu-used 8",
                    _ => "",
                };
                var made = await Run(ffmpeg, $"-hide_banner -loglevel error -y -f lavfi -i testsrc2=size=640x360:rate=25 -t 0.5 -pix_fmt yuv420p -c:v {softwareEncoder} {speed} \"{clip}\"", 60);
                if (made.ExitCode != 0)
                {
                    log($"hardware decoding {codec}: test clip not created ({FirstLine(made.Output)})");
                    continue;
                }
                foreach (string family in families)
                {
                    // Same shape as the jobs: Emby's decoder named before the input, the method's options next to it.
                    var result = await Run(ffmpeg, $"-hide_banner -loglevel error {string.Join(' ', HardwareDecoding.ProbeOptions(family))} -c:v {codec} -i \"{clip}\" -vf {HardwareDecoding.ProbeFilter} -f null -", 30);
                    log($"hardware decoding {HardwareDecoding.Key(family, codec)}: {(result.ExitCode == 0 ? "OK" : "unusable (" + FirstLine(result.Output) + ")")}");
                    if (result.ExitCode == 0)
                        validated.Add(HardwareDecoding.Key(family, codec));
                }
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return validated.ToArray();
    }

    /// <summary>Filter names from "ffmpeg -filters" (lines like " T.C scale  V->V  Scale the input video size.").</summary>
    private static string[] ParseFilters(string list) =>
        list.Split('\n')
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"^\s*[T.][S.][C.]?\s+([A-Za-z0-9_]+)\s"))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();

    public static string[] Gpus()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController");
            return searcher.Get().Cast<ManagementObject>().Select(o => $"{o["Name"]} ({o["DriverVersion"]})").ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string FirstLine(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";

    /// <summary>
    /// Runs ffmpeg once and returns stdout + stderr. Both streams are read concurrently so a full pipe cannot block it;
    /// a hung driver initialisation is killed after the timeout. Probe processes are not attached to the Job Object.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> Run(string exe, string arguments, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            p.Kill(true);
            return (-1, "timeout");
        }
        return (p.ExitCode, await stdout + await stderr);
    }
}
