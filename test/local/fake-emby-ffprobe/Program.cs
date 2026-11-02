using System.Diagnostics;
using System.Text.RegularExpressions;

// Le chemin du vrai ffprobe est dans fake-emby-ffprobe.txt, à côté de l'exécutable.
string real = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fake-emby-ffprobe.txt")).Trim();
var psi = new ProcessStartInfo(real) { RedirectStandardOutput = true, UseShellExecute = false };
foreach (string a in args)
    psi.ArgumentList.Add(a);
using var p = Process.Start(psi)!;
string output = await p.StandardOutput.ReadToEndAsync();
await p.WaitForExitAsync();
// La section "format" vient après les paquets et les pistes : seul son start_time change.
int at = output.LastIndexOf("\"format\"", StringComparison.Ordinal);
if (at >= 0)
    output = output[..at] + Regex.Replace(output[at..], @"(""start_time""\s*:\s*"")[^""]*", "${1}0.000000");
Console.Out.Write(output);
return p.ExitCode;
