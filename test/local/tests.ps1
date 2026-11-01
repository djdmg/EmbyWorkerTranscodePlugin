# Tests d'acceptation (cahier des charges §11) adaptés au banc local. Chargé par run-tests.ps1 (après lib.ps1).
# Chaque test démarre/arrête lui-même les workers dont il a besoin et consigne son verdict avec Add-Result.

$H264 = Join-Path $Paths.Media 'film_h264.mkv'
$HDR = Join-Path $Paths.Media 'film_hdr10.mkv'
$SRT = Join-Path $Paths.Media 'film_h264.fre.srt'
$SourceH264 = 150.0
$SourceHdr = 120.0

function Ensure-Workers([string[]]$names, [switch]$Only) {
    if ($Only) { foreach ($w in @('w1', 'w2', 'w3')) { if ($names -notcontains $w -and $Bench.Workers[$w]) { Stop-Worker $w } } }
    foreach ($w in $names) {
        if (-not $Bench.Workers[$w] -or $Bench.Workers[$w].HasExited) { Set-TestWorkerConfig $w @{ Reclaim = 'FinishPlayback' }; Start-Worker $w | Out-Null }
    }
    foreach ($w in $names) { Wait-WorkerState $w 'Available' 120 | Out-Null }
}

function Finish-Shim($h, [int]$timeoutSec = 30) {
    if (-not $h.HasExited) { $h.Send('q') }
    if (-not $h.Wait($timeoutSec * 1000)) { Write-Warning "relais $($h.Pid) toujours actif : arrêt forcé"; Stop-Mine $h.Pid -Tree; return $null }
    $h.ExitCode
}

function Wait-Exit($h, [int]$timeoutSec) { if ($h.Wait($timeoutSec * 1000)) { $h.ExitCode } else { $null } }

function Approx([double]$a, [double]$b, [double]$tol) { [math]::Abs($a - $b) -le $tol }

# Fréquence dominante d'une piste audio sinusoïdale (taux de passage par zéro × fs / 2).
function Get-ToneHz([string]$file, [int]$audioIndex = 0) {
    $out = & $Paths.ServerFfmpeg -hide_banner -i $file -map "0:a:$audioIndex" -t 20 -af 'aresample=48000,pan=mono|c0=c0,astats' -f null - 2>&1
    $m = $out | Select-String 'Zero crossings rate: ([\d.]+)' | Select-Object -Last 1
    $rate = if ($m) { $m.Matches[0].Groups[1].Value } else { $null }
    if (-not $rate) { return $null }
    [math]::Round([double]::Parse($rate, [cultureinfo]::InvariantCulture) * 48000 / 2)
}

# Vérifie un fichier converti : lisible, durée, flux attendus, décodage complet sans erreur.
function Check-Output([string]$file, [double]$duration, [int]$audioCount, [string]$vcodec = 'h264') {
    $p = Probe $file
    if (-not $p) { return @{ Ok = $false; Text = "sortie absente ou illisible ($file)" } }
    $v = @($p.Streams | Where-Object codec_type -eq 'video')
    $a = @($p.Streams | Where-Object codec_type -eq 'audio')
    $errors = @(Test-Decode $file)
    $problems = @()
    if (-not (Approx $p.Duration $duration 0.6)) { $problems += ("durée {0:0.00}s au lieu de {1:0.00}s" -f $p.Duration, $duration) }
    if ($v.Count -ne 1 -or $v[0].codec_name -ne $vcodec) { $problems += "vidéo inattendue" }
    if ($a.Count -ne $audioCount) { $problems += "$($a.Count) piste(s) audio au lieu de $audioCount" }
    if ($errors.Count) { $problems += "erreurs de décodage : $($errors | Select-Object -First 2)" }
    @{ Ok = $problems.Count -eq 0; Text = ("{0:0.00}s ; {1}{2}" -f $p.Duration, (Describe-Streams $p), $(if ($problems) { ' ; PROBLÈMES : ' + ($problems -join ' ; ') } else { ' ; décodage complet sans erreur' })); Probe = $p }
}

function Encode-Summary([string]$output) {
    (Get-Status).encodes | Where-Object { $_.Output -eq (U $output) } | Select-Object -First 1
}

# ---------------------------------------------------------------------------------------------------------------
function T01 {
    Ensure-Workers @('w1', 'w2')
    $out = Join-Path $Paths.Out 'conv-t01.mp4'
    $t0 = Get-Date
    $s = Start-Shim (New-ConversionArgs -InputPath $H264 -OutputPath $out -AudioStreams 1) 't01'
    $code = Wait-Exit $s 900
    $secs = ((Get-Date) - $t0).TotalSeconds
    $chk = Check-Output $out $SourceH264 1
    $e = Encode-Summary $out
    $remote = [bool](Wait-LogLine 'shim-t01' 'confiée aux workers' 1)
    $ok = $code -eq 0 -and $remote -and $chk.Ok -and $e -and $e.Status -eq 'Completed'
    Add-Result 'T01' 'Conversion par un worker disponible' $(if ($ok) { 'PASS' } else { 'FAIL' }) ("code {0}, {1:0}s, worker {2}, statut {3}, {4} morceaux ; {5}" -f $code, $secs, ((@(Log-Excerpt 'coordinator' 'Background job .* running on' 50)[-1]) -replace '.*running on (\S+).*','$1'), (Prop $e 'Status'), (Prop $e 'Chunks'), $chk.Text)
}

function T02 {
    Ensure-Workers @('w1', 'w2')
    $shims = @{}
    foreach ($n in 'a', 'b', 'c', 'd') {
        $shims[$n] = Start-Shim (New-PlaybackArgs -InputPath $H264 -Session "t02$n" -Preset ultrafast) "t02$n"
        Start-Sleep -Milliseconds 700
    }
    foreach ($n in $shims.Keys) { try { Wait-Segments "shim-t02$n" 2 120 | Out-Null } catch { } }
    $placement = foreach ($n in 'a', 'b', 'c', 'd') { $j = @(Get-ShimJobs "shim-t02$n") | Select-Object -First 1; "t02$n=$(if ($j) { $j.Worker } else { 'local/rejeté' })" }
    foreach ($n in $shims.Keys) { Finish-Shim $shims[$n] | Out-Null }
    # Deux conversions simultanées
    $c1 = Start-Shim (New-ConversionArgs -InputPath $H264 -OutputPath (Join-Path $Paths.Out 'conv-t02a.mp4')) 't02conv-a'
    Start-Sleep -Milliseconds 500
    $c2 = Start-Shim (New-ConversionArgs -InputPath $H264 -OutputPath (Join-Path $Paths.Out 'conv-t02b.mp4')) 't02conv-b'
    Start-Sleep 15
    $running = (Get-Status).encodes | Where-Object { $_.Output -like '*conv-t02*' } | ForEach-Object { "$([IO.Path]::GetFileName($_.Output))=$($_.Worker)" }
    $codes = @((Wait-Exit $c1 900), (Wait-Exit $c2 900))
    $ca = Check-Output (Join-Path $Paths.Out 'conv-t02a.mp4') $SourceH264 1
    $cb = Check-Output (Join-Path $Paths.Out 'conv-t02b.mp4') $SourceH264 1
    $workersUsed = @($placement | ForEach-Object { $_.Split('=')[1] } | Sort-Object -Unique)
    $convWorkers = @($running | ForEach-Object { $_.Split('=')[1] } | Sort-Object -Unique)
    $ok = ($workersUsed -contains 'w1') -and ($workersUsed -contains 'w2') -and ($workersUsed -notcontains 'local/rejeté') -and $convWorkers.Count -eq 2 -and $ca.Ok -and $cb.Ok -and ($codes -join ',') -eq '0,0'
    Add-Result 'T02' 'Répartition sur deux workers' $(if ($ok) { 'PASS' } else { 'FAIL' }) ("lectures : {0} ; conversions : {1} (codes {2}) ; sorties valides : {3}/{4}" -f ($placement -join ' '), ($running -join ' '), ($codes -join ','), $ca.Ok, $cb.Ok)
}

function T03 {
    foreach ($w in @('w1', 'w2')) { Stop-Worker $w }
    Start-Sleep 2
    $states = ((Get-Status).workers | ForEach-Object { "$($_.Name)=$($_.State)" }) -join ' '
    # a) commande Emby réelle (options du fork) : refus du coordinateur -> ffmpeg local d'Emby
    $a = Start-Shim (New-PlaybackArgs -InputPath $H264 -Session 't03a') 't03a'
    $codeA = Wait-Exit $a 30
    $rejA = Wait-LogLine 'shim-t03a' 'not offloaded' 1
    $locA = Wait-LogLine 'shim-t03a' 'Invalid loglevel|Unrecognized option' 1
    # b) mêmes arguments sans les options du fork : le ffmpeg local (amont) produit les segments
    $b = Start-Shim (New-PlaybackArgs -InputPath $H264 -Session 't03b' -NoForkOptions) 't03b'
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and @(Get-ChildItem (Join-Path $Paths.Out 't03b') -Filter '*.ts' -ErrorAction SilentlyContinue).Count -lt 4) { Start-Sleep 1 }
    $nTs = @(Get-ChildItem (Join-Path $Paths.Out 't03b') -Filter '*.ts' -ErrorAction SilentlyContinue).Count
    $codeB = Finish-Shim $b
    $rejB = Wait-LogLine 'shim-t03b' 'not offloaded' 1
    $segB = @(Get-SegmentLines 'shim-t03b').Count
    # c) conversion : refusée par le coordinateur -> commande d'Emby exécutée sur le serveur
    $out = Join-Path $Paths.Out 'conv-t03c.mp4'
    $c = Start-Shim (New-ConversionArgs -InputPath $H264 -OutputPath $out) 't03c'
    $codeC = Wait-Exit $c 900
    $rejC = Wait-LogLine 'shim-t03c' 'conversion sur le serveur' 1
    $chk = Check-Output $out $SourceH264 1
    $ok = $rejA -and $rejB -and $nTs -ge 4 -and $codeB -eq 0 -and $rejC -and $codeC -eq 0 -and $chk.Ok
    Add-Result 'T03' 'Aucun worker disponible' $(if ($ok) { 'PASS' } else { 'FAIL' }) ("états : $states | a) « $rejA » puis ffmpeg local : « $locA » (code $codeA) | b) sans options du fork : $nTs segments .ts écrits par le ffmpeg local, $segB lignes SegmentComplete (le ffmpeg amont ne les émet pas), code $codeB | c) « $rejC », code $codeC, $($chk.Text)")
}

function T04 {
    Stop-Worker 'w2'; Stop-Worker 'w1'
    Set-TestWorkerConfig 'w1' @{ Available = $true; Reclaim = 'FinishPlayback' }
    Start-Worker 'w1' -Tray | Out-Null
    Wait-WorkerState 'w1' 'Available' 120 | Out-Null
    $rc = Invoke-WorkerRelease 'w1'
    $w = Wait-WorkerState 'w1' @('Unavailable') 20
    $p = Start-Shim (New-PlaybackArgs -InputPath $H264 -Session 't04' -NoForkOptions) 't04'
    $rej = Wait-LogLine 'shim-t04' 'not offloaded' 20
    Start-Sleep 3
    Finish-Shim $p | Out-Null
    # Conversion : acceptée (le worker est connecté, seulement indisponible) ; le serveur encode tranche par tranche en
    # attendant son retour, et rien ne part sur le worker indisponible.
    $out4 = Join-Path $Paths.Out 'conv-t04.mp4'
    $c = Start-Shim (New-ConversionArgs -InputPath $H264 -OutputPath $out4) 't04conv'
    $accC = Wait-LogLine 'shim-t04conv' 'confiée aux workers' 20
    $local = $null; $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and -not $local) { $e = Encode-Summary $out4; if ($e -and $e.Worker -and $e.Worker -notin @('w1', 'w2')) { $local = "$($e.Worker) : $($e.Note)" }; Start-Sleep 1 }
    $onW1 = @(Log-Excerpt 'coordinator' 'running on w1 .*conv' 5).Count
    Finish-Shim $c | Out-Null
    $persist = (Get-Content (Join-Path (WorkerHome 'w1') 'worker.json') -Raw | ConvertFrom-Json).Available
    $ok = $rc -eq 0 -and $w.State -eq 'Unavailable' -and $rej -match 'Unavailable' -and $accC -and $local -and $onW1 -eq 0
    Add-Result 'T04' 'Worker devenu indisponible : refuse les nouvelles tâches' $(if ($ok) { 'PASS' } else { 'FAIL' }) ("release code $rc ; état coordinateur : $($w.State) ($($w.Reason)) ; Available persisté=$persist | lecture : « $rej » | conversion : « $accC » puis « $local » ; tâches de conversion données à w1 : $onW1")
    Stop-Worker 'w1'
    Set-TestWorkerConfig 'w1' @{ Available = $true }
}

# Analyse d'une lecture passée d'un worker à un autre.
function Check-Handover([string]$log, [string]$label) {
    $segs = @(Get-SegmentLines $log)
    $jobs = @(Get-ShimJobs $log)
    $resume = Select-String -Path (Join-Path $Paths.Logs "$log.log") -Pattern 'resuming at segment (\d+) \(([\d.]+)s\)' | Select-Object -First 1
    $problems = @(Test-SegmentContinuity $segs 0.010)
    $junction = $null
    if ($resume) {
        $idx = [int]$resume.Matches[0].Groups[1].Value
        $prev = $segs | Where-Object Index -eq ($idx - 1) | Select-Object -First 1
        $next = $segs | Where-Object Index -eq $idx | Select-Object -First 1
        if ($prev -and $next) { $junction = ("SegmentComplete : segment {0} End={1:F6} -> segment {2} Start={3:F6} End={4:F6}" -f $prev.Index, $prev.End, $next.Index, $next.Start, $next.End) }
        $session = $log -replace '^shim-', ''
        $mj = Test-MediaJunction (Join-Path $Paths.Out $session) $session $idx
        if ($mj) {
            $junction += (" ; fichiers .ts : fin {0}={1:F3}s, début {2}={3:F3}s, écart {4} ms" -f ($idx - 1), $mj.PrevEnd, $idx, $mj.NextFirst, $mj.GapMs)
            if ([math]::Abs($mj.GapMs) -gt 10 -or $mj.Dup) { $problems += "jonction média $($mj.GapMs) ms (doublons $($mj.Dup))" }
        } else { $problems += 'jonction média non mesurable' }
        if ($next -and $next.Start -eq 0) { $junction += ' ; NB : Start=0 rapporté au 1er segment repris' }
    }
    @{ Segments = $segs; Jobs = $jobs; Problems = $problems; Junction = $junction; Resume = $(if ($resume) { $resume.Line.Trim() }) }
}

function T05 {
    # ---- a) « Libérer le PC » (commande release, toujours immédiate) ----
    Stop-Worker 'w2'; Stop-Worker 'w1'
    Set-TestWorkerConfig 'w1' @{ Available = $true; Reclaim = 'FinishPlayback' }
    Start-Worker 'w1' -Tray | Out-Null
    Wait-WorkerState 'w1' 'Available' 120 | Out-Null
    $s = Start-Shim (New-PlaybackArgs -InputPath $H264 -Session 't05a' -Width 1920 -Height 1080 -Preset medium) 't05a'
    Wait-Segments 'shim-t05a' 3 120 | Out-Null
    $job1 = (@(Get-ShimJobs 'shim-t05a'))[0]
    Start-Worker 'w2' | Out-Null
    Wait-WorkerState 'w2' 'Available' 120 | Out-Null
    $before = @(Get-JobFfmpeg $job1.Job).Count
    $nBefore = @(Get-SegmentLines 'shim-t05a').Count
    $rc = Invoke-WorkerRelease 'w1'
    $tRel = Get-Date
    $cont = Wait-LogLine 'shim-t05a' 'continues on worker' 60
    $gone = $false; for ($i = 0; $i -lt 20 -and -not $gone; $i++) { Start-Sleep -Milliseconds 500; $gone = @(Get-JobFfmpeg $job1.Job).Count -eq 0 }
    $tGone = ((Get-Date) - $tRel).TotalSeconds
    try { Wait-Segments 'shim-t05a' ($nBefore + 5) 120 | Out-Null } catch { }
    $code = Finish-Shim $s
    $h = Check-Handover 'shim-t05a'
    $okA = $rc -eq 0 -and $job1.Worker -eq 'w1' -and $cont -match 'w2' -and $gone -and $h.Problems.Count -eq 0 -and $code -eq 0
    Add-Result 'T05a' 'Reprise du PC : « Libérer » (immédiat)' $(if ($okA) { 'PASS' } else { 'FAIL' }) ("job initial sur $($job1.Worker) ; ffmpeg du job sur w1 avant=$before, arrêté=$gone (≤{0:0.0}s) ; « $cont » ; $($h.Resume) ; $($h.Junction) ; {1} segments, indices {2}..{3}, anomalies : {4} ; code $code" -f $tGone, $h.Segments.Count, $h.Segments[0].Index, $h.Segments[-1].Index, $(if ($h.Problems) { $h.Problems -join ' ; ' } else { 'aucune' }))
    Stop-Worker 'w1'; Stop-Worker 'w2'

    # ---- b) Mode « laisser finir la lecture » (Reclaim = FinishPlayback), déclenché par la fin de plage horaire ----
    $now = Get-Date
    $win = @(@{ Days = @(0, 1, 2, 3, 4, 5, 6); Start = $now.AddMinutes(-5).ToString('HH:mm:ss'); End = $now.AddSeconds(25).ToString('HH:mm:ss') })
    Set-TestWorkerConfig 'w1' @{ Available = $true; Reclaim = 'FinishPlayback'; Schedule = $win }
    Start-Worker 'w1' | Out-Null
    Wait-WorkerState 'w1' 'Available' 40 | Out-Null
    $s = Start-Shim (New-PlaybackArgs -InputPath $H264 -Session 't05b' -Width 1920 -Height 1080 -Preset slow) 't05b'
    Wait-Segments 'shim-t05b' 2 120 | Out-Null
    $jobB = (@(Get-ShimJobs 'shim-t05b'))[0]
    $drain = Wait-WorkerState 'w1' @('Draining') 90
    $nAtDrain = @(Get-SegmentLines 'shim-t05b').Count
    $ffAtDrain = @(Get-JobFfmpeg $jobB.Job).Count
    $p2 = Start-Shim (New-PlaybackArgs -InputPath $H264 -Session 't05b2' -NoForkOptions) 't05b2'
    $rej = Wait-LogLine 'shim-t05b2' 'not offloaded' 20
    Finish-Shim $p2 | Out-Null
    Start-Sleep 15
    $nAfter = @(Get-SegmentLines 'shim-t05b').Count
    $stillSame = @(Get-ShimJobs 'shim-t05b').Count -eq 1 -and $ffAtDrain -ge 1 -and -not (Wait-LogLine 'shim-t05b' 'continues on|resuming at' 1)
    $code = Finish-Shim $s
    $after = Wait-WorkerState 'w1' @('Unavailable') 30
    $hb = Check-Handover 'shim-t05b'
    $okB = $drain.State -eq 'Draining' -and $nAfter -gt $nAtDrain -and $stillSame -and $rej -match 'Draining' -and $after.State -eq 'Unavailable' -and $hb.Problems.Count -eq 0
    Add-Result 'T05b' 'Reprise du PC : drainage (laisser finir la lecture)' $(if ($okB) { 'PASS' } else { 'FAIL' }) ("état w1 : $($drain.State) ($($drain.Reason)) ; lecture en cours : $nAtDrain -> $nAfter segments en 15 s sur le même job $($jobB.Job) ($stillSame) ; nouvelle lecture : « $rej » ; après 'q' : $($after.State) ($($after.Reason)) ; anomalies segments : $(if ($hb.Problems) { $hb.Problems -join ' ; ' } else { 'aucune' })")
    Stop-Worker 'w1'

    # ---- c) Reclaim = ReleaseImmediately, déclenché par la fin de plage horaire ----
    $now = Get-Date
    $win = @(@{ Days = @(0, 1, 2, 3, 4, 5, 6); Start = $now.AddMinutes(-5).ToString('HH:mm:ss'); End = $now.AddSeconds(30).ToString('HH:mm:ss') })
    Set-TestWorkerConfig 'w1' @{ Available = $true; Reclaim = 'ReleaseImmediately'; Schedule = $win }
    Start-Worker 'w1' | Out-Null
    Wait-WorkerState 'w1' 'Available' 40 | Out-Null
    $s = Start-Shim (New-PlaybackArgs -InputPath $H264 -Session 't05c' -Width 1920 -Height 1080 -Preset slow) 't05c'
    Wait-Segments 'shim-t05c' 2 120 | Out-Null
    $jobC = (@(Get-ShimJobs 'shim-t05c'))[0]
    Start-Worker 'w2' | Out-Null
    Wait-WorkerState 'w2' 'Available' 120 | Out-Null
    $cont = Wait-LogLine 'shim-t05c' 'continues on worker' 90
    $n = @(Get-SegmentLines 'shim-t05c').Count
    try { Wait-Segments 'shim-t05c' ($n + 4) 90 | Out-Null } catch { }
    $code = Finish-Shim $s
    $hc = Check-Handover 'shim-t05c'
    $wlog = Log-Excerpt (WorkerLog 'w1') 'PC repris' 1
    $okC = $jobC.Worker -eq 'w1' -and $cont -match 'w2' -and $hc.Problems.Count -eq 0 -and $code -eq 0
    Add-Result 'T05c' 'Reprise du PC : mode « libérer immédiatement »' $(if ($okC) { 'PASS' } else { 'FAIL' }) ("w1 : « $wlog » ; « $cont » ; $($hc.Resume) ; $($hc.Junction) ; anomalies : $(if ($hc.Problems) { $hc.Problems -join ' ; ' } else { 'aucune' }) ; code $code")
    Stop-Worker 'w1'; Stop-Worker 'w2'
    Set-TestWorkerConfig 'w1' @{ Available = $true; Reclaim = 'FinishPlayback' }
}

function T06 {
    Ensure-Workers @('w1', 'w2')
    $t0 = Get-Date
    $s = Start-Shim (New-PlaybackArgs -InputPath $H264 -Session 't06') 't06'
    $segs = Wait-Segments 'shim-t06' 5 120
    $firstLine = Wait-LogLine 'shim-t06' 'SegmentComplete=.*Index=0 ' 1
    $first = ([datetime]::ParseExact($firstLine.Substring(0, 12), 'HH:mm:ss.fff', $null) - [datetime]::ParseExact($t0.ToString('HH:mm:ss.fff'), 'HH:mm:ss.fff', $null)).TotalSeconds
    $job = (@(Get-ShimJobs 'shim-t06'))[0]
    $ffBefore = @(Get-JobFfmpeg $job.Job).Count
    $tq = Get-Date
    $code = Finish-Shim $s
    $exitSec = ((Get-Date) - $tq).TotalSeconds
    # T07 : le ffmpeg du worker pour ce job doit disparaître en quelques secondes
    $gone = $false; $tg = Get-Date
    for ($i = 0; $i -lt 20 -and -not $gone; $i++) { $gone = @(Get-JobFfmpeg $job.Job).Count -eq 0; if (-not $gone) { Start-Sleep -Milliseconds 250 } }
    $goneSec = ((Get-Date) - $tq).TotalSeconds
    $wEnd = Log-Excerpt "worker-$($job.Worker)*" "job $($job.Job): ended" 1
    # Déplacement (« seek ») : nouveau ffmpeg dans le même dossier de session, -ss 60 et -segment_start_number 20
    $s2 = Start-Shim (New-PlaybackArgs -InputPath $H264 -Session 't06' -Ss 60 -StartNumber 20) 't06-seek'
    $seek = Wait-Segments 'shim-t06-seek' 3 120
    $job2 = (@(Get-ShimJobs 'shim-t06-seek'))[0]
    $code2 = Finish-Shim $s2
    $gone2 = $false; for ($i = 0; $i -lt 20 -and -not $gone2; $i++) { $gone2 = @(Get-JobFfmpeg $job2.Job).Count -eq 0; if (-not $gone2) { Start-Sleep -Milliseconds 250 } }
    $seekMedia = Get-SegMediaRange (Join-Path $Paths.Out 't06\t06_21.ts'); $seekOk = $seek[0].Index -eq 20 -and (Approx $seek[0].End 63.0 0.05) -and (@(Test-SegmentContinuity $seek).Count -eq 0)
    $okA = $segs[0].Index -eq 0 -and @(Test-SegmentContinuity $segs).Count -eq 0 -and $code -eq 0
    Add-Result 'T06' 'Lecture distante : démarrage, arrêt « q », déplacement' $(if ($okA -and $seekOk -and $code2 -eq 0) { 'PASS' } else { 'FAIL' }) ("job $($job.Job) sur $($job.Worker), 1er segment en {0:0.0}s ; 'q' -> sortie du relais en {1:0.0}s (code $code) | déplacement : job $($job2.Job) sur $($job2.Worker) (même worker : $($job2.Worker -eq $job.Worker)), 1er segment index $($seek[0].Index) Start={2:F3}s End={3:F3}s (Duration rapportée {4:F1}s), segment 21 = [{5:F3}..{6:F3}] s dans le fichier, code $code2" -f $first, $exitSec, $seek[0].Start, $seek[0].End, ($seek[0].End - $seek[0].Start), (Prop $seekMedia 'First'), (Prop $seekMedia 'End'))
    Add-Result 'T07' 'Processus distant arrêté après la fin de lecture' $(if ($ffBefore -ge 1 -and $gone -and $gone2) { 'PASS' } else { 'FAIL' }) ("ffmpeg du job avant 'q' : $ffBefore ; disparu {0:0.0}s après 'q' : $gone ; journal worker : « $wEnd » ; job du déplacement arrêté : $gone2" -f $goneSec)
}

function T08 {
    Ensure-Workers @('w1', 'w2')
    $s = Start-Shim (New-PlaybackArgs -InputPath $H264 -Session 't08' -Width 1920 -Height 1080 -Preset medium) 't08'
    Wait-Segments 'shim-t08' 4 120 | Out-Null
    $job = (@(Get-ShimJobs 'shim-t08'))[0]
    $victim = $job.Worker
    $other = if ($victim -eq 'w1') { 'w2' } else { 'w1' }
    $ff = @(Get-JobFfmpeg $job.Job)
    $n = @(Get-SegmentLines 'shim-t08').Count
    $tk = Get-Date
    Stop-Worker $victim -Hard          # seul EmbyRemoteWorker.exe est tué ; son ffmpeg doit suivre (Job Object)
    $cont = Wait-LogLine 'shim-t08' 'continues on worker' 60
    $contSec = ((Get-Date) - $tk).TotalSeconds
    $gone = $false; for ($i = 0; $i -lt 20 -and -not $gone; $i++) { $gone = @(Get-JobFfmpeg $job.Job).Count -eq 0; if (-not $gone) { Start-Sleep -Milliseconds 250 } }
    try { Wait-Segments 'shim-t08' ($n + 5) 120 | Out-Null } catch { }
    $code = Finish-Shim $s
    $h = Check-Handover 'shim-t08'
    $drop = Log-Excerpt 'coordinator' "Worker $victim dropped" 1
    # Redémarrage : reconnexion et retour à « disponible »
    $tr = Get-Date
    Start-Worker $victim | Out-Null
    $back = Wait-WorkerState $victim 'Available' 120
    $backSec = ((Get-Date) - $tr).TotalSeconds
    $clean = Log-Excerpt (WorkerLog $victim) 'dossier de tâche abandonné supprimé' 1
    $ok = $cont -match $other -and $gone -and $h.Problems.Count -eq 0 -and $code -eq 0 -and $back.State -eq 'Available'
    Add-Result 'T08' 'Déconnexion / reconnexion d''un worker' $(if ($ok) { 'PASS' } else { 'FAIL' }) ("$victim tué (PID seul) pendant le job $($job.Job) ; ffmpeg orphelin : $(-not $gone) ; « $drop » ; « $cont » après {0:0.0}s ; $($h.Resume) ; $($h.Junction) ; anomalies : $(if ($h.Problems) { $h.Problems -join ' ; ' } else { 'aucune' }) ; code $code | redémarré : $($back.State) en {1:0.0}s ; nettoyage : « $clean »" -f $contSec, $backSec)
}

function T09 {
    # a) worker tué en pleine conversion, un autre worker disponible
    Ensure-Workers @('w1', 'w2')
    $out = Join-Path $Paths.Out 'conv-t09a.mp4'
    $s = Start-Shim (New-ConversionArgs -InputPath $H264 -OutputPath $out -Width 1920 -Height 1080) 't09a'
    $e = $null
    $deadline = (Get-Date).AddSeconds(300)
    while ((Get-Date) -lt $deadline) { $e = Encode-Summary $out; if ($e -and $e.Chunks -ge 2 -and $e.Worker) { break }; Start-Sleep 1 }
    $victim = $e.Worker; $chunksAtKill = $e.Chunks
    $existedBefore = Test-Path $out
    Stop-Worker $victim -Hard
    $code = Wait-Exit $s 900
    $chk = Check-Output $out $SourceH264 1
    $e2 = Encode-Summary $out
    $lost = @(Log-Excerpt 'coordinator' 'Job .* lost' 50)[-1]
    $resumed = (Get-Content (Join-Path $Paths.Work 'coord\data\encodes.json') -Raw -ErrorAction SilentlyContinue)
    $ok = $code -eq 0 -and $chk.Ok -and -not $existedBefore -and (Prop $e2 'Status') -eq 'Completed'
    Add-Result 'T09a' 'Worker tué pendant une conversion (autre worker dispo)' $(if ($ok) { 'PASS' } else { 'FAIL' }) ("$victim tué après $chunksAtKill morceaux ; sortie présente avant assemblage : $existedBefore ; « $lost » ; final : worker $(Prop $e2 'Worker'), $(Prop $e2 'Chunks') morceaux, $(Prop $e2 'Status'), note « $(Prop $e2 'Note') » ; code $code ; $($chk.Text)")
    Start-Worker $victim | Out-Null
    Wait-WorkerState $victim 'Available' 120 | Out-Null

    # b) seul worker tué : repli sur le serveur (EncodeLocalFallback), puis retour au worker quand il revient
    Stop-Worker 'w2'
    $out = Join-Path $Paths.Out 'conv-t09b.mp4'
    $s = Start-Shim (New-ConversionArgs -InputPath $H264 -OutputPath $out -Width 1920 -Height 1080) 't09b'
    $deadline = (Get-Date).AddSeconds(300)
    while ((Get-Date) -lt $deadline) { $e = Encode-Summary $out; if ($e -and $e.Chunks -ge 1 -and $e.Worker -eq 'w1') { break }; Start-Sleep 1 }
    Stop-Worker 'w1' -Hard
    $local = $null; $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and -not $local) { $e = Encode-Summary $out; if ($e -and $e.Worker -and $e.Worker -notin @('w1', 'w2')) { $local = "$($e.Worker) : $($e.Note)" }; Start-Sleep 1 }
    Start-Sleep 5
    Start-Worker 'w1' | Out-Null
    $code = Wait-Exit $s 900
    $chk = Check-Output $out $SourceH264 1
    $e2 = Encode-Summary $out
    $hand = Log-Excerpt 'coordinator' 'stopping the local encode to hand over' 1
    $ok = $code -eq 0 -and $chk.Ok -and $local
    Add-Result 'T09b' 'Seul worker tué pendant une conversion (repli serveur)' $(if ($ok) { 'PASS' } else { 'FAIL' }) ("repli : « $local » ; retour au worker : « $hand » ; final : $(Prop $e2 'Status'), worker $(Prop $e2 'Worker'), $(Prop $e2 'Chunks') morceaux ; code $code ; $($chk.Text)")
    Wait-WorkerState 'w1' 'Available' 120 | Out-Null
}

function T10 {
    Ensure-Workers @('w1', 'w2')
    $res = @()
    # a) lecture avec incrustation des sous-titres SRT externes (+ référence sans sous-titres)
    $s = Start-Shim (New-PlaybackArgs -InputPath $H264 -Session 't10a' -SubtitleFile $SRT) 't10a'
    $segA = Wait-Segments 'shim-t10a' 3 120
    $jobA = (@(Get-ShimJobs 'shim-t10a'))[0]
    Finish-Shim $s | Out-Null
    $r = Start-Shim (New-PlaybackArgs -InputPath $H264 -Session 't10ref') 't10ref'
    Wait-Segments 'shim-t10ref' 3 120 | Out-Null
    Finish-Shim $r | Out-Null
    $cmd = Log-Excerpt "worker-$($jobA.Worker)*" "$($jobA.Job): ffmpeg" 1
    $subsInCmd = "$cmd" -match "subtitles@f2=filename='aux/\d+\.srt':fontsdir='fonts'"
    # PSNR du bas de l'image (zone des sous-titres) à t=2,5 s entre la lecture sous-titrée et la référence
    $fa = Join-Path $Paths.Out 't10a\t10a_0.ts'; $fr = Join-Path $Paths.Out 't10ref\t10ref_0.ts'
    $psnr = & $Paths.ServerFfmpeg -hide_banner -i $fa -i $fr -filter_complex '[0:v]trim=start_frame=60:end_frame=61,crop=iw:ih/4:0:ih*3/4[a];[1:v]trim=start_frame=60:end_frame=61,crop=iw:ih/4:0:ih*3/4[b];[a][b]psnr' -f null - 2>&1 | Select-String 'PSNR.*average:([\d.inf]+)'
    $psnrVal = if ($psnr) { $psnr.Matches[0].Groups[1].Value } else { '?' }
    $probeA = Probe $fa
    $okA = $subsInCmd -and $probeA -and ($psnrVal -ne 'inf') -and ([double]::TryParse($psnrVal, [Globalization.NumberStyles]::Float, [cultureinfo]::InvariantCulture, [ref]$null)) -and [double]::Parse($psnrVal, [cultureinfo]::InvariantCulture) -lt 30
    Add-Result 'T10a' 'Lecture : sous-titres incrustés (SRT externe)' $(if ($okA) { 'PASS' } else { 'FAIL' }) ("job $($jobA.Job) sur $($jobA.Worker) ; filtre traduit dans la commande worker : $subsInCmd ; segment 0 : $(Describe-Streams $probeA) ; PSNR bas d'image vs référence sans sous-titres : $psnrVal dB (attendu < 30)")

    # b) lecture avec la 2e piste audio (0:2, sinus 880 Hz ; la 1re est à 440 Hz)
    $s = Start-Shim (New-PlaybackArgs -InputPath $H264 -Session 't10b' -AudioStream 2) 't10b'
    Wait-Segments 'shim-t10b' 4 120 | Out-Null
    Finish-Shim $s | Out-Null
    $hzB = Get-ToneHz (Join-Path $Paths.Out 't10b\t10b_1.ts')
    $hzRef = Get-ToneHz (Join-Path $Paths.Out 't10ref\t10ref_1.ts')
    Add-Result 'T10b' 'Lecture : choix de la piste audio' $(if ($hzB -and (Approx $hzB 880 40) -and (Approx $hzRef 440 30)) { 'PASS' } else { 'FAIL' }) ("piste 0:2 -> {0} Hz (attendu 880) ; référence 0:1 -> {1} Hz (attendu 440)" -f $hzB, $hzRef)

    # c) conversion avec les deux pistes audio
    $out = Join-Path $Paths.Out 'conv-t10c.mp4'
    $s = Start-Shim (New-ConversionArgs -InputPath $H264 -OutputPath $out -AudioStreams 1, 2) 't10c'
    $code = Wait-Exit $s 900
    $chk = Check-Output $out $SourceH264 2
    $hz = @((Get-ToneHz $out 0), (Get-ToneHz $out 1))
    $remote = [bool](Wait-LogLine 'shim-t10c' 'confiée aux workers' 1)
    Add-Result 'T10c' 'Conversion : deux pistes audio' $(if ($code -eq 0 -and $chk.Ok -and $remote -and (Approx $hz[0] 440 30) -and (Approx $hz[1] 880 40)) { 'PASS' } else { 'FAIL' }) ("distante : $remote ; code $code ; $($chk.Text) ; tonalités pistes : $($hz -join ' Hz / ') Hz")

    # d) conversion avec sous-titres incrustés
    $out = Join-Path $Paths.Out 'conv-t10d.mp4'
    $s = Start-Shim (New-ConversionArgs -InputPath $H264 -OutputPath $out -SubtitleFile $SRT) 't10d'
    $code = Wait-Exit $s 900
    $chk = Check-Output $out $SourceH264 1
    $how = Wait-LogLine 'shim-t10d' 'remote-transcode: conversion' 1
    Add-Result 'T10d' 'Conversion : sous-titres incrustés' $(if ($code -eq 0 -and $chk.Ok) { 'PASS' } else { 'FAIL' }) ("« $how » ; code $code ; $($chk.Text)")

    # e) conversion HDR10 -> SDR (supertonemap d'Emby traduit en zscale+tonemap sur le worker)
    $out = Join-Path $Paths.Out 'conv-t10e.mp4'
    $s = Start-Shim (New-ConversionArgs -InputPath $HDR -OutputPath $out -Hdr) 't10e'
    $code = Wait-Exit $s 900
    $chk = Check-Output $out $SourceHdr 1
    $remote = Wait-LogLine 'shim-t10e' 'remote-transcode: conversion' 1
    $tm = (Select-String -Path (Join-Path $Paths.Logs 'worker-w*.log') -Pattern 'tonemap@f4=tonemap=hable' | Select-Object -First 1)
    # Comparaison à la chaîne de référence amont (zscale linéaire + tonemap hable) appliquée directement à la source.
    $ps = & $Paths.ServerFfmpeg -hide_banner -i $out -ss 30 -i $HDR -filter_complex '[0:v]trim=start=30,setpts=PTS-STARTPTS[a];[1:v]scale=1280:720,zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable,zscale=t=bt709:m=bt709:r=tv,format=yuv420p[b];[a][b]psnr' -frames:v 25 -f null - 2>&1 | Select-String 'PSNR y:([\d.]+)'
    $y = if ($ps) { [double]::Parse($ps.Matches[0].Groups[1].Value, [cultureinfo]::InvariantCulture) } else { -1 }
    $vs = if ($chk.Probe) { $chk.Probe.Streams | Where-Object codec_type -eq 'video' | Select-Object -First 1 } else { $null }
    Add-Result 'T10e' 'Conversion : tone mapping HDR10 -> SDR' $(if ($code -eq 0 -and $chk.Ok -and $tm -and $y -gt 35) { 'PASS' } else { 'FAIL' }) ("« $remote » ; traduction zscale/tonemap vue dans le journal worker : $([bool]$tm) ; code $code ; $($chk.Text) ; pix_fmt $(Prop $vs 'pix_fmt') ; PSNR Y vs chaîne de référence zscale+tonemap amont (25 images à t=30 s) : $y dB (attendu > 35)")

    # f) lecture HDR10 avec tone mapping
    $s = Start-Shim (New-PlaybackArgs -InputPath $HDR -Session 't10f' -Hdr) 't10f'
    $segF = $null; try { $segF = Wait-Segments 'shim-t10f' 3 120 } catch { }
    $codeF = Finish-Shim $s
    $pf = Probe (Join-Path $Paths.Out 't10f\t10f_1.ts')
    Add-Result 'T10f' 'Lecture : tone mapping HDR10 -> SDR' $(if ($segF -and $pf) { 'PASS' } else { 'FAIL' }) ("$(@($segF).Count) segments ; segment 1 : $(Describe-Streams $pf) ; code $codeF ; $(Log-Excerpt 'shim-t10f' 'worker reported|not offloaded' 1)")
}

function T11 {
    Add-Result 'T11' 'Direct Play' 'NON TESTABLE' 'Décision prise par Emby (aucun ffmpeg lancé) : non testable sans serveur Emby. Le relais ne voit que les commandes ffmpeg ; un remux (-c:v copy) reste local (vérifié par les tests unitaires Remux_stays_local / Remux_and_playback_stay_with_emby).'
}

# curl -k : code HTTP seulement. Les en-têtes WebSocket déclenchent la vérification faite avant la mise à niveau.
function Http([string]$method, [string]$url, [string]$bearer, [string]$clientIp, [switch]$Ws, [string[]]$extra = @()) {
    $a = @('-k', '-s', '--noproxy', '*', '-o', 'NUL', '-w', '%{http_code}', '--http1.1', '-X', $method, '--max-time', '10')
    if ($bearer) { $a += @('-H', "Authorization: Bearer $bearer") }
    if ($clientIp) { $a += @('-H', "X-Real-IP: $clientIp") }
    if ($Ws) { $a += @('-H', 'Connection: Upgrade', '-H', 'Upgrade: websocket', '-H', 'Sec-WebSocket-Version: 13', '-H', 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==') }
    $a += $extra
    (& curl.exe @a $url) -join ''
}

function T12 {
    Ensure-Workers @('w1') -Only
    $WB = "https://127.0.0.1:$WorkerPort"; $SB = "http://127.0.0.1:$ShimPort"
    $lines = @(); $fails = 0
    function Check([string]$label, [string]$got, [string]$expected) {
        $script:t12ok = $script:t12ok -and ($got -eq $expected)
        "$label : $got (attendu $expected)$(if ($got -ne $expected) { ' <-- ÉCART' })"
    }
    $script:t12ok = $true
    # 1) jeton de worker invalide / absent
    $lines += Check 'ws/worker jeton invalide' (Http GET "$WB/ws/worker" 'deadbeef' '10.1.0.1' -Ws) '401'
    $lines += Check 'ws/worker sans jeton' (Http GET "$WB/ws/worker" '' '10.1.0.1' -Ws) '401'
    # 2) worker révoqué (API d'administration, comme la page du plugin)
    $w3 = New-TestWorker 'w3'
    $Bench.Ids['w3'] = $w3.Id
    $tok3 = (([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($w3.Code))) | ConvertFrom-Json).token
    Start-Worker 'w3' | Out-Null
    Wait-WorkerState 'w3' 'Available' 120 | Out-Null
    $admin = (Get-Content (Join-Path $Paths.CoordDir 'data\admin.secret') -Raw).Trim()
    $lines += Check 'POST /admin/workers/{w3}/revoke' (Http POST "$SB/admin/workers/$($w3.Id)/revoke" $admin '') '200'
    Start-Sleep 4
    $st = ((Get-Status).workers | Where-Object Id -eq $w3.Id).State
    $lines += Check 'état de w3 après révocation' $st 'Revoked'
    $lines += Check 'ws/worker jeton de w3 révoqué' (Http GET "$WB/ws/worker" $tok3 '10.1.0.3' -Ws) '401'
    $refused = Log-Excerpt 'coordinator' 'Refused worker connection' 3
    $w3log = Log-Excerpt (WorkerLog 'w3') 'connection to coordinator lost|401' 2
    Stop-Worker 'w3'
    # 3) jeton de job
    $s = Start-Shim (New-PlaybackArgs -InputPath $H264 -Session 't12' -Width 1920 -Height 1080 -Preset medium) 't12'
    Wait-Segments 'shim-t12' 2 120 | Out-Null
    $job = (@(Get-ShimJobs 'shim-t12'))[0]
    $cmdline = (@(Get-JobFfmpeg $job.Job))[0].CommandLine
    $jt = if ($cmdline -match 'Bearer ([0-9a-f]+)') { $Matches[1] } else { '' }
    $lines += Check 'GET files/0 mauvais jeton de job' (Http GET "$WB/jobs/$($job.Job)/files/0" 'bad' '10.1.0.4') '401'
    $lines += Check 'GET files/0 sans jeton' (Http GET "$WB/jobs/$($job.Job)/files/0" '' '10.1.0.4') '401'
    $lines += Check 'GET files/0 jeton d''un autre job inexistant' (Http GET "$WB/jobs/0123456789abcdef/files/0" $jt '10.1.0.4') '401'
    $lines += Check 'GET files/0 bon jeton (plage 0-99)' (Http GET "$WB/jobs/$($job.Job)/files/0" $jt '10.1.0.4' -extra @('-H', 'Range: bytes=0-99')) '206'
    $lines += Check 'GET files/7 (fichier hors plan) bon jeton' (Http GET "$WB/jobs/$($job.Job)/files/7" $jt '10.1.0.4') '404'
    $lines += Check 'PUT segments/999 mauvais jeton' (Http PUT "$WB/jobs/$($job.Job)/segments/999?start=0&end=1" 'bad' '10.1.0.4' -extra @('--data-binary', 'x')) '410'
    Finish-Shim $s | Out-Null
    Start-Sleep 2
    $lines += Check 'GET files/0 bon jeton après fin du job' (Http GET "$WB/jobs/$($job.Job)/files/0" $jt '10.1.0.4') '401'
    # 4) fichiers hors AllowedRoots : jamais confiés à un worker (donc jamais servis)
    $secret = Join-Path $Paths.Work 'secret'
    New-Item -ItemType Directory -Force $secret | Out-Null
    if (-not (Test-Path "$secret\prive.mkv")) { & $Paths.WorkerFfmpeg -hide_banner -loglevel error -y -f lavfi -i testsrc2=size=640x360:rate=25 -f lavfi -i sine -t 20 -c:v libx264 -c:a aac "$secret\prive.mkv" }
    Copy-Item $SRT "$secret\prive.srt" -Force
    $cases = @(
        @{ n = 'entrée hors racines'; a = (New-PlaybackArgs -InputPath "$secret\prive.mkv" -Session 't12x1' -NoForkOptions) },
        @{ n = 'entrée avec ..'; a = ((New-PlaybackArgs -InputPath "$secret\prive.mkv" -Session 't12x2' -NoForkOptions) | ForEach-Object { if ($_ -eq (U "$secret\prive.mkv")) { (U $Paths.Media) + '/../secret/prive.mkv' } else { $_ } }) },
        @{ n = 'sous-titres hors racines'; a = (New-PlaybackArgs -InputPath $H264 -Session 't12x3' -SubtitleFile "$secret\prive.srt" -NoForkOptions) },
        @{ n = 'conversion hors racines'; a = (New-ConversionArgs -InputPath "$secret\prive.mkv" -OutputPath (Join-Path $Paths.Out 'conv-t12x4.mp4')) }
    )
    $i = 0
    foreach ($c in $cases) {
        $i++
        $h = Start-Shim $c.a "t12x$i"
        $why = Wait-LogLine "shim-t12x$i" 'not offloaded|conversion sur le serveur|confiée|running on worker' 20
        Finish-Shim $h | Out-Null
        $remote = "$why" -match 'confiée|running on worker'
        if ($remote) { $script:t12ok = $false }
        $lines += "$($c.n) : « $why »$(if ($remote) { ' <-- ÉCART' })"
    }
    # 5) relais sans secret
    $lines += Check 'ws/shim sans secret' (Http GET "$SB/ws/shim" '' '' -Ws) '401'
    $lines += Check 'ws/shim mauvais secret' (Http GET "$SB/ws/shim" 'bad' '' -Ws) '401'
    $h = Start-Shim (New-PlaybackArgs -InputPath $H264 -Session 't12ns' -NoForkOptions) 't12ns' $Paths.ShimNoSecret
    $why = Wait-LogLine 'shim-t12ns' 'secret unavailable|unreachable|running on' 20
    Finish-Shim $h | Out-Null
    $lines += "relais sans fichier secret : « $why »"
    if ("$why" -notmatch 'secret unavailable') { $script:t12ok = $false }
    # 6) séparation des ports et administration
    $lines += Check '/status sur le port workers' (Http GET "$WB/status" '' '10.1.0.5') '404'
    $lines += Check '/ws/worker sur le port local' (Http GET "$SB/ws/worker" '' '' -Ws) '404'
    $lines += Check '/admin/status sans secret' (Http GET "$SB/admin/status" '' '') '401'
    $lines += Check '/admin/status secret admin' (Http GET "$SB/admin/status" $admin '') '200'
    $lines += Check 'POST /admin/workers/pair sans secret' (Http POST "$SB/admin/workers/pair" '' '' -extra @('-H', 'Content-Type: application/json', '--data', '{"name":"intrus"}')) '401'
    # 7) limitation des échecs : 10 échecs en 10 min -> 429 pendant 15 min (par adresse client)
    $codes = foreach ($k in 1..11) { Http GET "$WB/ws/worker" "bad$k" '10.9.9.9' -Ws }
    $lines += Check 'tentatives 1..10 jeton invalide' (($codes | Select-Object -First 10 | Sort-Object -Unique) -join ',') '401'
    $lines += Check '11e tentative' $codes[10] '429'
    $lines += Check 'même client, bon jeton de job ou non : bloqué' (Http GET "$WB/jobs/x/files/0" '' '10.9.9.9') '429'
    $lines += Check 'autre client non bloqué' (Http GET "$WB/ws/worker" 'bad' '10.9.9.8' -Ws) '401'
    $lines += Check 'w1 toujours connecté' (Get-WorkerState 'w1').State 'Available'
    Add-Result 'T12' 'Accès non autorisés refusés' $(if ($script:t12ok) { 'PASS' } else { 'FAIL' }) (($lines + @("coordinateur : $($refused -join ' / ')", "w3 : $($w3log -join ' / ')")) -join "`n")
}









