# Bibliothèque commune du banc de test local (coordinateur + workers de test + relais « remote-ffmpeg »).
# Aucun accès au worker de production : chaque worker de test a son propre EMBY_WORKER_HOME sous work\workers\,
# et seuls les processus démarrés ici (PID notés dans work\pids.txt) sont arrêtés.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
$Work = Join-Path $Root 'work'
$WingetBin = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Directory -Filter 'Gyan.FFmpeg*' -ErrorAction SilentlyContinue |
    ForEach-Object { Get-ChildItem $_.FullName -Recurse -Filter ffprobe.exe -ErrorAction SilentlyContinue } | Select-Object -First 1
$Paths = [ordered]@{
    Work          = $Work
    Bin           = Join-Path $Work 'bin'
    Coordinator   = Join-Path $Work 'bin\coordinator\coordinator.exe'
    Shim          = Join-Path $Work 'bin\shim\remote-ffmpeg.exe'
    ShimNoSecret  = Join-Path $Work 'bin\shim-nosecret\remote-ffmpeg.exe'
    # Relais qui n'utilise jamais l'audio converti à l'avance (UseAudioAhead = false) : assemblage 0.7.17 (T16).
    ShimClassic   = Join-Path $Work 'bin\shim-classic\remote-ffmpeg.exe'
    # Relais configuré en « démarrage rapide » (LocalFirstSeconds = 20) avec le faux ffmpeg d'Emby (tests-localfirst.ps1).
    ShimLocalFirst = Join-Path $Work 'bin\shim-localfirst\remote-ffmpeg.exe'
    FakeFfmpeg    = Join-Path $Work 'bin\fake-emby-ffmpeg\fake-emby-ffmpeg.exe'
    WorkerExe     = Join-Path $Work 'bin\worker\EmbyRemoteWorker.exe'
    # ffmpeg des workers de test : build « full » 9.0.2 (celui qu'embarque le worker depuis 0.7.15, avec libplacebo pour
    # le tone mapping GPU) : celui de winget s'il est « full », sinon celui du prototype, sinon le ffmpeg du worker installé.
    WorkerFfmpeg  = @(
        $(if ($WingetBin -and $WingetBin.FullName -like '*full_build*') { Join-Path $WingetBin.DirectoryName 'ffmpeg.exe' }),
        'C:\emby\wip\gputonemap\ffmpeg-full\ffmpeg-9.0.2-full_build\bin\ffmpeg.exe',
        'C:\emby\artifacts\worker\EmbyRemoteWorker\ffmpeg\ffmpeg.exe') | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    # ffmpeg/ffprobe « serveur » : build amont 9.0.2 (winget), en remplacement du ffmpeg modifié d'Emby (absent ici).
    ServerFfprobe = if ($WingetBin) { $WingetBin.FullName } else { $null }
    ServerFfmpeg  = if ($WingetBin) { Join-Path $WingetBin.DirectoryName 'ffmpeg.exe' } else { $null }
    Media         = Join-Path $Work 'media'
    Out           = Join-Path $Work 'out'
    CoordDir      = Join-Path $Work 'coord'
    Workers       = Join-Path $Work 'workers'
    Logs          = Join-Path $Work 'logs'
    Pids          = Join-Path $Work 'pids.txt'
}
$WorkerPort = 38097
$ShimPort = 38098
$Dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$ProductionWorkerHome = Join-Path $env:LOCALAPPDATA 'EmbyRemoteWorker'

# Chemin « à la Unix » attendu par les planificateurs (C:\emby\x -> /emby/x ; même lecteur que le répertoire courant).
function U([string]$p) { ($p -replace '^[A-Za-z]:', '') -replace '\\', '/' }

# ---- Processus : aide C# (stderr/stdout vers fichiers ligne par ligne, stdin gardé ouvert pour 'q') -------------
if (-not ('Harness.HProc' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
namespace Harness {
public sealed class HProc {
    public Process P;
    StreamWriter err, outw;
    readonly object gate = new object();
    public int Pid { get { return P.Id; } }
    public static HProc Start(string exe, string[] args, string stderrFile, string stdoutFile, IDictionary<string,string> env, string cwd) {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true,
            StandardErrorEncoding = System.Text.Encoding.UTF8, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        if (!string.IsNullOrEmpty(cwd)) psi.WorkingDirectory = cwd;
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        var h = new HProc();
        h.err = new StreamWriter(new FileStream(stderrFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        h.outw = string.IsNullOrEmpty(stdoutFile) ? h.err : new StreamWriter(new FileStream(stdoutFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        h.P = new Process { StartInfo = psi, EnableRaisingEvents = true };
        h.P.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (h.gate) h.err.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " | " + e.Data); };
        h.P.OutputDataReceived += (s, e) => { if (e.Data != null) lock (h.gate) h.outw.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " | " + e.Data); };
        h.P.Start();
        h.P.BeginErrorReadLine();
        h.P.BeginOutputReadLine();
        return h;
    }
    public void Send(string s) { try { P.StandardInput.Write(s); P.StandardInput.Flush(); } catch (Exception) { } }
    public bool Wait(int ms) { bool done = P.WaitForExit(ms); if (done) { P.WaitForExit(); lock (gate) { err.Flush(); outw.Flush(); } } return done; }
    public bool HasExited { get { return P.HasExited; } }
    public int ExitCode { get { return P.ExitCode; } }
}
}
'@
}

function Register-Pid([System.Diagnostics.Process]$p, [string]$role) {
    "$($p.Id)|$($p.ProcessName)|$($p.StartTime.ToString('o'))|$role" | Add-Content $Paths.Pids
}

function Start-H([string]$exe, [string[]]$arguments, [string]$name, [hashtable]$envVars = $null, [string]$cwd = $null, [switch]$NoRegister) {
    New-Item -ItemType Directory -Force $Paths.Logs | Out-Null
    $dict = $null
    if ($envVars) {
        $dict = [System.Collections.Generic.Dictionary[string, string]]::new()
        foreach ($k in $envVars.Keys) { $dict[$k] = [string]$envVars[$k] }
    }
    $h = [Harness.HProc]::Start($exe, $arguments, (Join-Path $Paths.Logs "$name.log"), $null, $dict, $cwd)
    if (-not $NoRegister) { Register-Pid $h.P $name }
    $h
}

# Arrête un processus démarré par ce banc (vérifie nom + heure de démarrage pour ne jamais toucher un PID réutilisé).
function Stop-Mine([int]$procId, [switch]$Tree) {
    $line = Get-Content $Paths.Pids -ErrorAction SilentlyContinue | Where-Object { $_ -like "$procId|*" } | Select-Object -Last 1
    if (-not $line) { throw "PID $procId non démarré par le banc : refus de l'arrêter" }
    $parts = $line.Split('|')
    $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
    if (-not $p) { return }
    if ($p.ProcessName -ne $parts[1] -or [math]::Abs(($p.StartTime - [datetime]::Parse($parts[2])).TotalSeconds) -gt 1) { return }
    if ($p.Path -and $p.Path -like 'C:\emby\artifacts\worker\*') { throw "refus : $($p.Path) est le worker de production" }
    if ($Tree) { & taskkill.exe /PID $procId /T /F | Out-Null } else { $p.Kill() }
    $p.WaitForExit(10000) | Out-Null
}

function Stop-AllMine {
    foreach ($line in @(Get-Content $Paths.Pids -ErrorAction SilentlyContinue)) {
        $procId = [int]$line.Split('|')[0]
        try { Stop-Mine $procId -Tree } catch { Write-Warning $_ }
    }
    Remove-Item $Paths.Pids -ErrorAction SilentlyContinue
}

function Assert-ProductionWorkerUntouched([int[]]$expected) {
    $now = @(Get-Process EmbyRemoteWorker -ErrorAction SilentlyContinue | Where-Object { $_.Path -like 'C:\emby\artifacts\worker\*' } | ForEach-Object Id)
    foreach ($id in $expected) { if ($now -notcontains $id) { throw "le worker de production $id a disparu !" } }
}

# ---- Coordinateur -------------------------------------------------------------------------------
function Write-CoordinatorConfig([hashtable]$overrides = @{}) {
    New-Item -ItemType Directory -Force $Paths.CoordDir, $Paths.Out | Out-Null
    $cfg = [ordered]@{
        WorkerUrl                  = "https://127.0.0.1:$WorkerPort"
        ShimUrl                    = "http://127.0.0.1:$ShimPort"
        DataDir                    = 'data'
        AllowedRoots               = @((U $Paths.Media))
        OutputRoots                = @((U $Paths.Out))
        HeartbeatTimeoutSeconds    = 15
        OfferTimeoutSeconds        = 5
        EncodeSpoolDir             = 'encode'
        EncodeLocalFallback        = $true
        ServerFfmpeg               = $Paths.ServerFfmpeg
        ServerFfprobe              = $Paths.ServerFfprobe
        ServerToolsWorkingDirectory = $null
        CertificateNames           = @('127.0.0.1', 'localhost')
    }
    foreach ($k in $overrides.Keys) { $cfg[$k] = $overrides[$k] }
    $cfg | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $Paths.CoordDir 'coordinator.json') -Encoding utf8
}

function Invoke-CoordinatorCli([string[]]$cliArgs) {
    & $Paths.Coordinator @cliArgs --config (Join-Path $Paths.CoordDir 'coordinator.json')
}

function Start-Coordinator([string]$logName = 'coordinator') {
    $h = Start-H $Paths.Coordinator @('--config', (Join-Path $Paths.CoordDir 'coordinator.json')) $logName -cwd $Paths.CoordDir
    for ($i = 0; $i -lt 60; $i++) {
        try { Invoke-RestMethod "http://127.0.0.1:$ShimPort/status" -TimeoutSec 2 | Out-Null; return $h } catch { Start-Sleep -Milliseconds 500 }
    }
    throw "le coordinateur ne répond pas (voir $($Paths.Logs)\$logName.log)"
}

function Get-Status { Invoke-RestMethod "http://127.0.0.1:$ShimPort/status" -TimeoutSec 5 }

function Get-WorkerState([string]$name) { (Get-Status).workers | Where-Object { $_.Name -eq $name -and -not $_.Revoked } | Select-Object -Last 1 }

function Wait-WorkerState([string]$name, [string[]]$states, [int]$timeoutSec = 60) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $w = Get-WorkerState $name
        if ($w -and $states -contains $w.State) { return $w }
        Start-Sleep -Milliseconds 500
    }
    $w = Get-WorkerState $name
    throw "worker $name pas dans l'état [$($states -join ',')] après $timeoutSec s (état : $($w.State) $($w.Reason))"
}

# ---- Workers de test ------------------------------------------------------------------------------
function WorkerHome([string]$name) { Join-Path $Paths.Workers $name }

function New-TestWorker([string]$name, [int]$maxJobs = 2, [string]$reclaim = 'FinishPlayback') {
    $home_ = WorkerHome $name
    if ($home_ -ieq $ProductionWorkerHome) { throw 'refus : dossier du worker de production' }
    Remove-Item $home_ -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $home_ | Out-Null
    $out = Invoke-CoordinatorCli @('pair', $name)
    $code = ($out | Select-Object -Last 1).Trim()
    $env:EMBY_WORKER_HOME = $home_
    try { & $Paths.WorkerExe pair $code | Out-Null } finally { Remove-Item Env:EMBY_WORKER_HOME }
    for ($i = 0; $i -lt 20 -and -not (Test-Path "$home_\worker.json"); $i++) { Start-Sleep -Milliseconds 250 }
    Set-TestWorkerConfig $name @{ MaxJobs = $maxJobs; Reclaim = $reclaim }
    $id = ($out | Select-String 'paired with id (\w+)').Matches[0].Groups[1].Value
    [pscustomobject]@{ Name = $name; Id = $id; Home = $home_; Code = $code }
}

# Seuils de charge neutralisés : deux workers de test + le worker de production sur le même PC se verraient
# mutuellement comme « applications étrangères ». Stabilisation courte pour des tests rapides.
function Set-TestWorkerConfig([string]$name, [hashtable]$opts) {
    $file = Join-Path (WorkerHome $name) 'worker.json'
    $j = Get-Content $file -Raw | ConvertFrom-Json
    $j.Name = $name
    $j.FfmpegPath = $Paths.WorkerFfmpeg
    $j.Available = $true
    if ($opts.ContainsKey('MaxJobs')) { $j.MaxJobs = $opts.MaxJobs }
    $a = $j.Availability
    $a.Mode = 0; $a.BlockOnBattery = $false
    $a.MaxForeignCpuPercent = 1000; $a.MaxForeignGpuPercent = 1000; $a.MaxMemoryPercent = 100
    $a.LoadHysteresisSeconds = 1; $a.StableSeconds = 2
    if ($opts.ContainsKey('Reclaim')) { $a.Reclaim = if ($opts.Reclaim -eq 'ReleaseImmediately') { 1 } else { 0 } }
    if ($opts.ContainsKey('Schedule')) { $a.Schedule = $opts.Schedule } else { $a.Schedule = @() }
    if ($opts.ContainsKey('Available')) { $j.Available = $opts.Available }
    foreach ($k in 'HardwareEncodingForConversions', 'HardwareEncodingForPlayback') {
        if ($opts.ContainsKey($k)) { $j | Add-Member -NotePropertyName $k -NotePropertyValue $opts[$k] -Force }
    }
    $j | ConvertTo-Json -Depth 8 | Set-Content $file -Encoding utf8
}

function Start-TestWorker([string]$name, [switch]$Tray, [string]$logSuffix = '') {
    $home_ = WorkerHome $name
    $wargs = if ($Tray) { @('--tray') } else { @('run') }
    Start-H $Paths.WorkerExe $wargs "worker-$name$logSuffix" @{ EMBY_WORKER_HOME = $home_ } -cwd $home_
}

function Invoke-WorkerRelease([string]$name) {
    $env:EMBY_WORKER_HOME = WorkerHome $name
    try { & $Paths.WorkerExe release | Out-Null; $LASTEXITCODE } finally { Remove-Item Env:EMBY_WORKER_HOME }
}

# Processus ffmpeg d'un job (le worker lui donne l'URL /jobs/<id>/files/... en entrée).
function Get-JobFfmpeg([string]$jobId) {
    # Les entrées passent par le cache local du worker (http://127.0.0.1:port/...) : l'adresse lue par le ffmpeg de
    # la tâche est dans la commande que le worker journalise.
    $line = Get-ChildItem $Paths.Workers -Recurse -Filter 'worker-*.log' -ErrorAction SilentlyContinue |
        Select-String -Pattern "job $($jobId): ffmpeg" -SimpleMatch | Select-Object -Last 1
    $url = if ($line) { [regex]::Match($line.Line, 'http://127\.0\.0\.1:\d+/\S+').Value } else { '' }
    @(Get-CimInstance Win32_Process -Filter "Name='ffmpeg.exe'" | Where-Object { $_.CommandLine -like "*/jobs/$jobId/*" -or ($url -and $_.CommandLine -like "*$url*") })
}

# ---- Relais (remote-ffmpeg) -----------------------------------------------------------------------
function Write-ShimConfig([string]$shimExe = $Paths.Shim, [string]$secretPath = (Join-Path $Paths.CoordDir 'data\shim.secret'),
    [string]$realFfmpeg = $Paths.ServerFfmpeg, [double]$localFirstSeconds = 0, [bool]$useAudioAhead = $true) {
    $cfg = [ordered]@{
        CoordinatorUrl = "ws://127.0.0.1:$ShimPort/ws/shim"
        SecretPath     = $secretPath
        RealFfmpeg     = $realFfmpeg
        RealFfprobe    = $Paths.ServerFfprobe
        ToolsDirectory = $null
        # Le démarrage par le serveur suit les lignes SegmentComplete du ffmpeg d'Emby, absentes du ffmpeg amont du
        # banc : par défaut les tests partent directement sur un worker. Le relais shim-localfirst utilise le faux
        # ffmpeg d'Emby (fake-emby-ffmpeg, qui écrit ces lignes) avec LocalFirstSeconds = 20.
        LocalFirstSeconds = $localFirstSeconds
        UseAudioAhead  = $useAudioAhead
    }
    $cfg | ConvertTo-Json | Set-Content (Join-Path (Split-Path $shimExe) 'shim.json') -Encoding utf8
}

function Start-Shim([string[]]$shimArgs, [string]$name, [string]$exe = $Paths.Shim) {
    Start-H $exe $shimArgs "shim-$name" -cwd $Paths.Work
}

# ---- Démarrage rapide : faux ffmpeg d'Emby + relais shim-localfirst ----------------------------------------------
# Réglages du faux ffmpeg (lus à chaque lancement) : ReadRate (ex. 0.8 = serveur lent), StartDelayMs, StartZero...
function Set-FakeFfmpeg([hashtable]$opts = @{}) {
    $cfg = [ordered]@{ RealFfmpeg = $Paths.ServerFfmpeg; LogFile = (Join-Path $Paths.Logs 'fake-emby-ffmpeg.log'); ReadRate = 0; StartDelayMs = 0; StartZero = 'first' }
    foreach ($k in $opts.Keys) { $cfg[$k] = $opts[$k] }
    $cfg | ConvertTo-Json | Set-Content (Join-Path (Split-Path $Paths.FakeFfmpeg) 'fake-emby-ffmpeg.json') -Encoding utf8
}

# Copie du relais configurée avec le faux ffmpeg comme RealFfmpeg et LocalFirstSeconds = 20 (le relais shim\ reste
# inchangé pour les tests T01-T12). À appeler après Initialize-Bench (secret du coordinateur).
function Initialize-LocalFirstShim([double]$localFirstSeconds = 20) {
    if (-not (Test-Path $Paths.FakeFfmpeg)) { throw "faux ffmpeg absent : publier test\local\fake-emby-ffmpeg (voir README)" }
    New-Item -ItemType Directory -Force (Split-Path $Paths.ShimLocalFirst) | Out-Null
    Copy-Item $Paths.Shim $Paths.ShimLocalFirst -Force
    Write-ShimConfig $Paths.ShimLocalFirst -realFfmpeg $Paths.FakeFfmpeg -localFirstSeconds $localFirstSeconds
    Set-FakeFfmpeg
}

# Lignes SegmentComplete émises par le relais (ce qu'Emby lit).
function Get-SegmentLines([string]$logName) {
    $file = Join-Path $Paths.Logs "$logName.log"
    foreach ($l in Get-Content $file -ErrorAction SilentlyContinue) {
        if ($l -match 'SegmentComplete=\S* Index=(\d+) Start=(-?[\d.]+) End=(-?[\d.]+).*filename=(\S+)') {
            [pscustomobject]@{ Index = [int]$Matches[1]; Start = [double]::Parse($Matches[2], [cultureinfo]::InvariantCulture); End = [double]::Parse($Matches[3], [cultureinfo]::InvariantCulture); File = $Matches[4] }
        }
    }
}

function Wait-Segments([string]$logName, [int]$count, [int]$timeoutSec = 90) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $s = @(Get-SegmentLines $logName)
        if ($s.Count -ge $count) { return $s }
        Start-Sleep -Milliseconds 300
    }
    throw "$logName : moins de $count segments après $timeoutSec s"
}

# Continuité : indices contigus, sans doublon, jonctions Start(n+1) ≈ End(n) (tolérance en secondes).
function Test-SegmentContinuity($segments, [double]$tolerance = 0.010) {
    $problems = @()
    $seen = @{}
    $prev = $null
    foreach ($s in $segments) {
        if ($seen.ContainsKey($s.Index)) { $problems += "doublon index $($s.Index)" }
        $seen[$s.Index] = $true
        if ($prev) {
            if ($s.Index -ne $prev.Index + 1) { $problems += "trou/saut $($prev.Index) -> $($s.Index)" }
            elseif ($s.Start -eq 0 -and $prev.End -gt 0) { }   # Start=0 rapporté par le muxer amont au 1er segment d'un run avec -ss : jonction vérifiée sur les fichiers (Test-MediaJunction)
            elseif ([math]::Abs($s.Start - $prev.End) -gt $tolerance) { $problems += ("jonction {0}->{1} : End={2:F6} Start={3:F6} (écart {4:F3} ms)" -f $prev.Index, $s.Index, $prev.End, $s.Start, (($s.Start - $prev.End) * 1000)) }
        }
        $prev = $s
    }
    $problems
}

# Horodatages vidéo réels d'un segment .ts : premier PTS et fin (dernier PTS + durée d'image), en secondes.
function Get-SegMediaRange([string]$file) {
    $rows = & $Paths.ServerFfprobe -v error -select_streams v:0 -show_entries packet=pts_time,duration_time -of csv=p=0 $file 2>$null
    $pts = @(); $dur = 0.04
    foreach ($r in $rows) { $f = "$r".Split(','); if ($f[0] -and $f[0] -ne 'N/A') { $pts += [double]::Parse($f[0], [cultureinfo]::InvariantCulture); if ($f.Count -gt 1 -and $f[1] -ne 'N/A') { $dur = [double]::Parse($f[1], [cultureinfo]::InvariantCulture) } } }
    if (-not $pts) { return $null }
    $sorted = $pts | Sort-Object
    [pscustomobject]@{ First = $sorted[0]; End = $sorted[-1] + $dur; Frames = $pts.Count; Dup = ($pts.Count - @($pts | Sort-Object -Unique).Count) }
}

# Jonction réelle entre deux segments consécutifs : écart = 1er PTS(n) - fin(n-1) (0 = continu ; < 0 = recouvrement).
function Test-MediaJunction([string]$dir, [string]$session, [int]$index) {
    $a = Get-SegMediaRange (Join-Path $dir "${session}_$($index - 1).ts")
    $b = Get-SegMediaRange (Join-Path $dir "${session}_$index.ts")
    if (-not $a -or -not $b) { return $null }
    [pscustomobject]@{ GapMs = [math]::Round(($b.First - $a.End) * 1000, 1); PrevEnd = $a.End; NextFirst = $b.First; Frames = "$($a.Frames)+$($b.Frames)"; Dup = $a.Dup + $b.Dup }
}

# ---- Arguments « à la Emby 4.10 » -------------------------------------------------------------------
# Forme copiée des commandes réelles (src\RemoteTranscode.Tests\PlannerTests.cs, MatchingTests.cs) avec nos chemins.
function New-PlaybackArgs {
    param(
        [string]$InputPath, [string]$Session, [double]$Ss = 0, [int]$StartNumber = 0,
        [string]$SubtitleFile, [int]$AudioStream = 1, [switch]$Hdr, [switch]$NoForkOptions,
        [int]$Width = 1280, [int]$Height = 720, [string]$Preset = 'veryfast'
    )
    $dir = Join-Path $Paths.Out $Session
    New-Item -ItemType Directory -Force $dir | Out-Null
    $u = U $dir
    $a = [System.Collections.Generic.List[string]]::new()
    if (-not $NoForkOptions) { $a.AddRange([string[]]@('-loglevel', '+timing', '-y', '-print_graphs_file', "$u/graph.txt")) } else { $a.Add('-y') }
    $a.AddRange([string[]]@('-copyts', '-start_at_zero', '-f', 'matroska,webm'))
    if ($Ss -gt 0) { $a.AddRange([string[]]@('-ss', [TimeSpan]::FromSeconds($Ss).ToString('hh\:mm\:ss\.fff'))) }
    $a.AddRange([string[]]@('-c:v:0', $(if ($Hdr) { 'hevc' } else { 'h264' }), '-noautorotate', '-i', (U $InputPath)))
    if ($Hdr) {
        $graph = "[0:0]scale@f1=width=${Width}:height=${Height},setparams@f2=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,format@f3=pix_fmts=p010,supertonemap@f4=tonemap=hable,setparams@f5=color_primaries=bt709:color_trc=bt709:colorspace=bt709,format@f6=pix_fmts=yuv420p[f6_out0]"
        $label = '[f6_out0]'
    } elseif ($SubtitleFile) {
        $graph = "[0:0]scale@f1=width=${Width}:height=${Height},subtitles@f2=filename='$(U $SubtitleFile)':fontsdir='$(U (Join-Path $Paths.Media 'fonts'))':charenc=utf-8[f2_out0]"
        $label = '[f2_out0]'
    } else {
        $graph = "[0:0]scale@f1=width=${Width}:height=${Height}[f1_out0]"
        $label = '[f1_out0]'
    }
    $a.AddRange([string[]]@('-filter_complex', $graph, '-map', $label, '-map', "0:$AudioStream", '-sn',
        '-c:v:0', 'libx264', '-g:v:0', '75', '-maxrate:v:0', '3000000', '-bufsize:v:0', '6000000', '-sc_threshold:v:0', '0',
        '-keyint_min:v:0', '75', '-r:v:0', '25', '-pix_fmt:v:0', 'yuv420p', '-preset:v:0', $Preset, '-profile:v:0', 'main',
        '-x264opts:v:0', 'subme=0:me_range=4:rc_lookahead=10:partitions=none', '-crf:v:0', '23',
        '-c:a:0', 'aac', '-ab:a:0', '192000', '-ac:a:0', '2', '-metadata:s:a:0', 'language=fre', '-disposition:a:0', 'default',
        '-max_delay', '5000000', '-avoid_negative_ts', 'disabled', '-f', 'segment', '-map_metadata', '-1', '-map_chapters', '-1',
        '-segment_format', 'mpegts', '-segment_list', "$u/$Session.m3u8", '-segment_list_type', 'm3u8',
        '-segment_time', '00:00:03.000', '-segment_start_number', "$StartNumber", '-individual_header_trailer', '0',
        '-write_header_trailer', '0'))
    if (-not $NoForkOptions) { $a.AddRange([string[]]@('-segment_write_temp', '1')) }
    $a.Add("$u/${Session}_%d.ts")
    , $a.ToArray()
}

# Conversion (Convertir / téléchargement) : forme d'Emby 4.10 (ConversionTests.cs) sans les options propres au fork,
# pour que l'assemblage côté serveur (ffmpeg amont ici) fonctionne.
function New-ConversionArgs {
    param(
        [string]$InputPath, [string]$OutputPath, [string]$Encoder = 'libx264', [int[]]$AudioStreams = @(1),
        [string]$SubtitleFile, [switch]$Hdr, [int]$Width = 1280, [int]$Height = 720, [switch]$ForkOptions,
        # Décodeur imposé avant -i (« -c:v:0 h264 » d'Emby) ; 'none' : aucun, ffmpeg choisit (source AV1, T20).
        [string]$InputCodec,
        # Conteneur de la source et « -r » qu'Emby ajoute (T26).
        [string]$InputFormat = 'matroska,webm', [string]$Rate
    )
    $a = [System.Collections.Generic.List[string]]::new()
    if ($ForkOptions) { $a.AddRange([string[]]@('-loglevel', '+timing')) }
    if (-not $InputCodec) { $InputCodec = if ($Hdr) { 'hevc' } else { 'h264' } }
    $a.AddRange([string[]]@('-y', '-copyts', '-start_at_zero', '-f', $InputFormat))
    if ($InputCodec -ne 'none') { $a.AddRange([string[]]@('-c:v:0', $InputCodec)) }
    $a.AddRange([string[]]@('-noautorotate', '-i', (U $InputPath)))
    if ($Hdr) {
        $a.AddRange([string[]]@('-filter_complex', "[0:0]scale@f1=width=${Width}:height=${Height},setparams@f2=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,format@f3=pix_fmts=p010,supertonemap@f4=tonemap=hable,setparams@f5=color_primaries=bt709:color_trc=bt709:colorspace=bt709,format@f6=pix_fmts=yuv420p[f6_out0]", '-map', '[f6_out0]'))
    } elseif ($SubtitleFile) {
        $a.AddRange([string[]]@('-filter_complex', "[0:0]scale@f1=width=${Width}:height=${Height},subtitles@f2=filename='$(U $SubtitleFile)':charenc=utf-8[f2_out0]", '-map', '[f2_out0]'))
    } else {
        $a.AddRange([string[]]@('-map', '0:0'))
    }
    foreach ($s in $AudioStreams) { $a.AddRange([string[]]@('-map', "0:$s")) }
    $a.Add('-sn')
    $a.AddRange([string[]]@('-c:v:0', $Encoder))
    if (-not $Hdr -and -not $SubtitleFile) { $a.AddRange([string[]]@('-vf', "scale=w=${Width}:h=${Height}")) }
    if ($Rate) { $a.AddRange([string[]]@('-r:v:0', $Rate)) }
    $a.AddRange([string[]]@('-pix_fmt:v:0', 'yuv420p', '-maxrate:v:0', '4000000', '-bufsize:v:0', '8000000', '-preset:v:0', 'veryfast', '-crf:v:0', '23'))
    for ($i = 0; $i -lt $AudioStreams.Count; $i++) { $a.AddRange([string[]]@("-c:a:$i", 'aac', "-ab:a:$i", '160000', "-ac:a:$i", '2')) }
    $a.AddRange([string[]]@('-avoid_negative_ts', 'disabled', '-f', 'mp4', '-map_metadata', '-1', '-map_chapters', '-1', (U $OutputPath)))
    , $a.ToArray()
}

# ---- Vérifications ffprobe -------------------------------------------------------------------------
function Probe([string]$file) {
    $json = & $Paths.ServerFfprobe -v error -print_format json -show_format -show_streams $file 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $json) { return $null }
    $j = $json | ConvertFrom-Json
    [pscustomobject]@{
        Duration = [double]::Parse($j.format.duration, [cultureinfo]::InvariantCulture)
        Streams  = @($j.streams)
        Format   = $j.format.format_name
    }
}

function Prop($o, [string]$name) { if ($o -and $o.PSObject.Properties[$name]) { $o.$name } else { $null } }

# "video:h264/yuv420p/bt709 1280x720, audio:aac[fre], ..."
function Describe-Streams($probe) {
    if (-not $probe) { return 'illisible' }
    ($probe.Streams | ForEach-Object {
        $d = "$($_.codec_type):$($_.codec_name)"
        if (Prop $_ 'pix_fmt') { $d += "/$($_.pix_fmt)" }
        if (Prop $_ 'color_transfer') { $d += "/$($_.color_transfer)" }
        if (Prop $_ 'width') { $d += " $($_.width)x$($_.height)" }
        $lang = Prop (Prop $_ 'tags') 'language'
        if ($lang) { $d += "[$lang]" }
        $d
    }) -join ', '
}

# Décodage complet : aucune erreur de flux (fichier non tronqué / non corrompu).
function Test-Decode([string]$file) {
    $errors = & $Paths.ServerFfmpeg -hide_banner -v error -i $file -f null - 2>&1
    @($errors | Where-Object { $_ -and "$_".Trim() })
}

# ---- Banc complet -------------------------------------------------------------------------------------
$Bench = @{ Coordinator = $null; Workers = @{}; Ids = @{}; Production = @() }

# Remise à zéro (données du coordinateur, workers de test, sorties, journaux), appariement de w1/w2, démarrage.
function Initialize-Bench([string[]]$workers = @('w1', 'w2'), [switch]$Start) {
    $Bench.Production = @(Get-Process EmbyRemoteWorker -ErrorAction SilentlyContinue | Where-Object { $_.Path -like 'C:\emby\artifacts\worker\*' } | ForEach-Object Id)
    foreach ($port in $WorkerPort, $ShimPort) {
        if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { throw "port $port déjà utilisé (banc précédent encore actif ? lancer cleanup.ps1)" }
    }
    Remove-Item $Paths.CoordDir, $Paths.Out, $Paths.Workers, $Paths.Logs -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $Paths.Work 'results.jsonl') -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $Paths.Logs, $Paths.Out | Out-Null
    Write-CoordinatorConfig
    Invoke-CoordinatorCli @('workers') | Out-Null   # crée le certificat et le dossier data
    foreach ($w in $workers) { $Bench.Ids[$w] = (New-TestWorker $w).Id }
    $Bench.Coordinator = Start-Coordinator
    Write-ShimConfig
    New-Item -ItemType Directory -Force (Split-Path $Paths.ShimNoSecret) | Out-Null
    Copy-Item $Paths.Shim $Paths.ShimNoSecret -Force
    Write-ShimConfig $Paths.ShimNoSecret (Join-Path $Paths.Work 'absent\shim.secret')
    # First start of freshly paired workers: they detect their encoders and GPU decoders (several minutes when the
    # PC's GPU is busy with real work), hence a longer wait than for a restart.
    if ($Start) { foreach ($w in $workers) { Start-Worker $w | Out-Null } ; foreach ($w in $workers) { Wait-WorkerState $w 'Available' 420 | Out-Null } }
}

function Start-Worker([string]$name, [switch]$Tray) {
    $suffix = if ($Bench.Workers.ContainsKey("$name#n")) { '-' + (++$Bench.Workers["$name#n"]) } else { $Bench.Workers["$name#n"] = 1; '' }
    $Bench.Workers[$name] = Start-TestWorker $name -Tray:$Tray -logSuffix $suffix
    $Bench.Workers[$name]
}

function Stop-Worker([string]$name, [switch]$Hard) {
    $h = $Bench.Workers[$name]
    if ($h -and -not $h.HasExited) { Stop-Mine $h.Pid -Tree:(-not $Hard) }
    $Bench.Workers.Remove($name)
}

# Nom du journal du dernier démarrage d'un worker.
function WorkerLog([string]$name) { $n = $Bench.Workers["$name#n"]; if ($n -gt 1) { "worker-$name-$n" } else { "worker-$name" } }

function Stop-Bench {
    Stop-AllMine
    if ($Bench.Production.Count) { Assert-ProductionWorkerUntouched $Bench.Production; Write-Host "worker de production intact (PID $($Bench.Production -join ','))" }
}

# Identifiant du job annoncé par le relais (« job <id> running on worker <w> »), et le worker.
function Get-ShimJobs([string]$logName) {
    foreach ($l in Get-Content (Join-Path $Paths.Logs "$logName.log") -ErrorAction SilentlyContinue) {
        if ($l -match 'job ([0-9a-f]{16}) (running on|continues on|back on) worker (\S+)') { [pscustomobject]@{ Job = $Matches[1]; How = $Matches[2]; Worker = $Matches[3] } }
    }
}

function Wait-LogLine([string]$logName, [string]$pattern, [int]$timeoutSec = 60) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $m = Select-String -Path (Join-Path $Paths.Logs "$logName.log") -Pattern $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($m) { return $m.Line }
        Start-Sleep -Milliseconds 300
    }
    $null
}

# ---- Résultats ---------------------------------------------------------------------------------------
$Results = [System.Collections.Generic.List[object]]::new()
function Add-Result([string]$id, [string]$name, [string]$verdict, [string]$detail) {
    $r = [pscustomobject]@{ Id = $id; Test = $name; Verdict = $verdict; Detail = $detail; At = (Get-Date).ToString('HH:mm:ss') }
    $Results.Add($r)
    $color = switch ($verdict) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Yellow' } }
    Write-Host ("[{0}] {1} {2} — {3}" -f $verdict, $id, $name, $detail) -ForegroundColor $color
    $r | ConvertTo-Json -Compress | Add-Content (Join-Path $Paths.Work 'results.jsonl')
}

function Log-Excerpt([string]$logName, [string]$pattern, [int]$max = 6) {
    @(Select-String -Path (Join-Path $Paths.Logs "$logName.log") -Pattern $pattern -ErrorAction SilentlyContinue | Select-Object -First $max | ForEach-Object { $_.Line.Trim() })
}




