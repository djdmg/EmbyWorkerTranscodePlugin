# Tests du « démarrage rapide » (LocalFirstSeconds = 20) : la lecture démarre sur le ffmpeg du serveur — ici le faux
# ffmpeg d'Emby (fake-emby-ffmpeg : ffmpeg amont + lignes SegmentComplete du fork) — puis passe à un worker au segment
# exact. Chargé par run-tests.ps1 après lib.ps1 et tests.ps1 ; nécessite Initialize-LocalFirstShim.
#   pwsh -File run-tests.ps1 -Only LF          # LFa, LFc, LFd, LFe, LFb, LFb4, LFf
#   pwsh -File run-tests.ps1 -Only LFa,LFb

# Copies de la source pour les scénarios « serveur lent » : la vitesse mesurée est retenue par le coordinateur pour
# chaque fichier, une copie évite d'influencer les autres scénarios.
$H264Slow = Join-Path $Paths.Media 'film_h264_lent.mkv'
$H264Hang = Join-Path $Paths.Media 'film_h264_bloque.mkv'
$H264Mid = Join-Path $Paths.Media 'film_h264_1x5.mkv'
$H264Q = Join-Path $Paths.Media 'film_h264_q.mkv'
$H264Advice = Join-Path $Paths.Media 'film_h264_avis.mkv'
$H264Advice0 = Join-Path $Paths.Media 'film_h264_avis0.mkv'

# Préparation (appelée par run-tests.ps1) : copies créées et lues une fois (le premier accès à un gros fichier neuf est
# lent sous Windows, antivirus), puis une lecture « à blanc » de chaque fichier pour que le coordinateur ait déjà sondé
# la source. Sans cela, l'avis ShimStartAdvice peut dépasser les 2 s qu'attend le relais, qui part alors en ffmpeg
# local pur sans bascule (défaut démontré par LFf, volontairement laissé à froid).
function Initialize-LocalFirstMedia {
    foreach ($f in $H264Slow, $H264Hang, $H264Mid, $H264Q, $H264Advice, $H264Advice0) {
        if (-not (Test-Path $f)) { Copy-Item $H264 $f }
        & $Paths.ServerFfprobe -v error -show_format $f | Out-Null
    }
    Set-FakeFfmpeg
    $i = 0
    foreach ($f in $H264, $H264Slow, $H264Hang, $H264Mid, $H264Q) {
        $i++
        $h = Start-Shim (New-PlaybackArgs -InputPath $f -Session "lfwarm$i") "lfwarm$i" $Paths.ShimLocalFirst
        $deadline = (Get-Date).AddSeconds(6)
        while ((Get-Date) -lt $deadline -and -not (LF-Line "shim-lfwarm$i" 'remote-transcode:')) { Start-Sleep -Milliseconds 100 }
        $line = LF-Line "shim-lfwarm$i" 'remote-transcode:'
        Finish-Shim $h 15 | Out-Null
        Write-Host "préchauffage $([IO.Path]::GetFileName($f)) : $(if ($line) { $line } else { 'aucun avis (relais parti en ffmpeg local pur)' })"
    }
}

function LF-Time([string]$line) { [datetime]::ParseExact($line.Substring(0, 12), 'HH:mm:ss.fff', $null) }

# Secondes entre $t0 et l'horodatage (banc) d'une ligne de journal.
function LF-Since([datetime]$t0, [string]$line) {
    if (-not $line) { return $null }
    [math]::Round(((LF-Time $line) - [datetime]::ParseExact($t0.ToString('HH:mm:ss.fff'), 'HH:mm:ss.fff', $null)).TotalSeconds, 2)
}

# Première (ou dernière) ligne d'un journal de work\logs correspondant au motif, sans attendre.
function LF-Line([string]$log, [string]$pattern, [switch]$Last) {
    $m = @(Select-String -Path (Join-Path $Paths.Logs "$log.log") -Pattern $pattern -ErrorAction SilentlyContinue)
    if (-not $m) { return $null }
    if ($Last) { $m[-1].Line.Trim() } else { $m[0].Line.Trim() }
}

# Lance une lecture « à la Emby » sur le relais shim-localfirst avec les réglages donnés au faux ffmpeg.
function LF-Start([string]$session, [hashtable]$fake = @{}, [hashtable]$playback = @{}) {
    Set-FakeFfmpeg $fake
    $p = @{ InputPath = $H264; Session = $session }
    foreach ($k in $playback.Keys) { $p[$k] = $playback[$k] }
    $a = New-PlaybackArgs @p
    $t0 = Get-Date
    $h = Start-Shim $a $session $Paths.ShimLocalFirst
    [pscustomobject]@{ H = $h; T0 = $t0; Log = "shim-$session"; Session = $session }
}

# ffmpeg (amont) et faux ffmpeg encore vivants pour cette session (leur ligne de commande contient le dossier de sortie).
function LF-SessionProcs([string]$session) {
    @(Get-CimInstance Win32_Process -Filter "Name='ffmpeg.exe' OR Name='fake-emby-ffmpeg.exe'" | Where-Object { $_.CommandLine -like "*/out/$session/*" })
}

# Nombre de lancements du faux ffmpeg (= ffmpeg du serveur) pour cette session.
function LF-FakeRuns([string]$session) {
    @(Select-String -Path (Join-Path $Paths.Logs 'fake-emby-ffmpeg.log') -Pattern "in : .*/out/$session/" -ErrorAction SilentlyContinue).Count
}

function LF-CacheBlocks([string]$worker) {
    $f = @(Get-ChildItem (Join-Path (WorkerHome $worker) 'cache\inputs') -Recurse -Filter '*.blk' -ErrorAction SilentlyContinue)
    $bytes = 0; foreach ($x in $f) { $bytes += $x.Length }
    [pscustomobject]@{ Count = $f.Count; MB = [math]::Round($bytes / 1MB, 1) }
}

# Analyse de la bascule serveur -> worker : segments, point de reprise, délai de démarrage du worker, jonction réelle.
# La jonction d'un run sans -ss (serveur, depuis 0) vers un run avec -ss (worker) recule de ~80 ms (D1, connu et sans
# effet sur Emby Web, cf. docs\03) : toléré entre -100 et +10 ms, sans image en double.
function LF-Handover($r) {
    $log = $r.Log
    $segs = @(Get-SegmentLines $log)
    $resume = LF-Line $log 'resuming at segment (\d+) \(([\d.]+)s\)'
    $res = [ordered]@{ Segs = $segs; Resume = $resume; Problems = @(Test-SegmentContinuity $segs 0.010); Junction = ''; Idx = $null; Pos = $null; WorkerStart = $null; LocalSegs = $segs.Count; Playlist = '' }
    if ($resume -and $resume -match 'resuming at segment (\d+) \(([\d.]+)s\)') {
        $idx = [int]$Matches[1]; $res.Idx = $idx
        $res.Pos = [double]::Parse($Matches[2], [cultureinfo]::InvariantCulture)
        $res.LocalSegs = @($segs | Where-Object Index -lt $idx).Count
        $first = LF-Line $log "SegmentComplete=\S* Index=$idx "
        if ($first) { $res.WorkerStart = [math]::Round(((LF-Time $first) - (LF-Time $resume)).TotalSeconds, 2) }
        if ($idx -gt 0) {
            $mj = Test-MediaJunction (Join-Path $Paths.Out $r.Session) $r.Session $idx
            if ($mj) {
                $res.Junction = "jonction {0}->{1} : fin {2:F3}s, début {3:F3}s, écart {4} ms, doublons {5}" -f ($idx - 1), $idx, $mj.PrevEnd, $mj.NextFirst, $mj.GapMs, $mj.Dup
                if ($mj.Dup -or $mj.GapMs -gt 10 -or $mj.GapMs -lt -100) { $res.Problems += "jonction média $($mj.GapMs) ms (doublons $($mj.Dup))" }
            } else { $res.Problems += 'jonction média non mesurable' }
        }
    }
    # Liste m3u8 tenue par le relais (segments du serveur notés + segments du worker) : mêmes fichiers, même ordre.
    $m3u8 = Join-Path $Paths.Out "$($r.Session)\$($r.Session).m3u8"
    $listed = @(Get-Content $m3u8 -ErrorAction SilentlyContinue | Where-Object { $_ -like '*.ts' })
    $expected = @($segs | ForEach-Object File)
    $res.Playlist = "m3u8 : $($listed.Count) entrées"
    if (($listed -join ',') -ne ($expected -join ',')) { $res.Problems += "m3u8 ($($listed.Count) entrées) différente des segments annoncés ($($expected.Count))" }
    [pscustomobject]$res
}

function LF-Range($segs) { $s = @($segs); if ($s.Count) { "$($s[0].Index)..$($s[$s.Count - 1].Index)" } else { 'aucun' } }

function LF-Problems($h) { if ($h.Problems) { $h.Problems -join ' ; ' } else { 'aucune' } }

# Attend qu'aucun worker de test ne soit disponible pour le coordinateur.
function LF-WaitNoWorker([int]$timeoutSec = 40) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (-not @((Get-Status).workers | Where-Object { -not $_.Revoked -and $_.State -in 'Available', 'Busy' })) { return $true }
        Start-Sleep -Milliseconds 500
    }
    $false
}

# ---------------------------------------------------------------------------------------------------------------
# a) Worker libre : 1er segment par le serveur, bascule après ~20 s de vidéo, continuité, préchargement.
function LFa {
    Stop-Worker 'w2'; Stop-Worker 'w1'          # w1 redémarré : cache d'entrée vide, le préchargement est mesurable
    Ensure-Workers @('w1') -Only
    $r = LF-Start 'lfa'
    # Instantané du cache de w1 juste avant la bascule (avant que le job du worker ne lise quoi que ce soit).
    $cache = [pscustomobject]@{ Count = 0; MB = 0 }; $resume = $null
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline -and -not $resume -and -not $r.H.HasExited) {
        $resume = LF-Line $r.Log 'resuming at segment'
        if (-not $resume) { $cache = LF-CacheBlocks 'w1'; Start-Sleep -Milliseconds 150 }
    }
    $back = Wait-LogLine $r.Log 'back on worker' 60
    $n = @(Get-SegmentLines $r.Log).Count
    try { Wait-Segments $r.Log ($n + 5) 120 | Out-Null } catch { }
    $job = @(Get-ShimJobs $r.Log) | Select-Object -First 1
    $ffJob = if ($job) { @(Get-JobFfmpeg $job.Job).Count } else { 0 }
    $code = Finish-Shim $r.H
    Start-Sleep 2
    $left = @(LF-SessionProcs 'lfa').Count
    $jobGone = if ($job) { @(Get-JobFfmpeg $job.Job).Count -eq 0 } else { $false }
    $h = LF-Handover $r
    $firstLine = LF-Line $r.Log 'SegmentComplete='
    $first = LF-Since $r.T0 $firstLine
    $serverStart = LF-Line $r.Log 'marrage sur le serveur'
    $progress = [bool](LF-Line $r.Log 'frame=.*time=.*speed=')
    $wlog = WorkerLog 'w1'
    $pre = LF-Line $wlog 'préchargement de'
    $preDone = LF-Line $wlog 'préchargés'
    $coordPre = LF-Line 'coordinator' 'fetches ahead' -Last
    $speed = LF-Line 'coordinator' 'Server speed at playback start.*film_h264\.mkv' -Last
    $fakeRuns = LF-FakeRuns 'lfa'
    $ok = $serverStart -and $firstLine -and $first -lt 6 -and $back -and (Prop $job 'Worker') -eq 'w1' -and $h.Idx -gt 0 -and $h.Pos -ge 19 -and $h.Pos -le 32 -and
        $h.Problems.Count -eq 0 -and $pre -and $cache.Count -gt 0 -and $code -eq 0 -and $left -eq 0 -and $jobGone -and $fakeRuns -eq 1
    Add-Result 'LFa' 'Démarrage rapide : serveur puis worker libre' $(if ($ok) { 'PASS' } else { 'FAIL' }) (
        "« $serverStart » ($(LF-Since $r.T0 $serverStart) s après le lancement) ; 1er segment (serveur) {0:0.00} s après le lancement ; progression serveur relayée : $progress ; bascule : « $($h.Resume) » puis « $back » ({1:0.00} s après le lancement) ; {2} segments serveur, 1er segment du worker {3:0.00} s après la décision ; $($h.Junction) ; {4} segments au total, indices $(LF-Range $h.Segs), $($h.Playlist), anomalies : $(LF-Problems $h) | préchargement w1 : « $pre » / « $preDone » ; coordinateur : « $coordPre » ; cache w1 avant bascule : {7} blocs ({8} Mo) | vitesse retenue : « $speed » | 'q' : code $code, ffmpeg du job arrêté : $jobGone, processus serveur restants : $left, lancements du faux ffmpeg : $fakeRuns" -f
        $first, (LF-Since $r.T0 $back), $h.LocalSegs, $h.WorkerStart, $h.Segs.Count, 0, 0, $cache.Count, $cache.MB)
    $script:LFPrefetchStart = $h.WorkerStart
}

# b) Serveur ralenti (-readrate 0.8) : bascule dès le 2e segment ; 2e lecture du même fichier directement sur le worker.
#    b3) Serveur bloqué au démarrage (aucun segment) : bascule après 4 s.
function LFb {
    foreach ($f in $H264Slow, $H264Hang) { if (-not (Test-Path $f)) { Copy-Item $H264 $f } }
    Ensure-Workers @('w1') -Only
    $r = LF-Start 'lfb' @{ ReadRate = 0.8 } @{ InputPath = $H264Slow }
    $back = Wait-LogLine $r.Log 'back on worker' 60
    $why = LF-Line $r.Log 'trop lent'
    $n = @(Get-SegmentLines $r.Log).Count
    try { Wait-Segments $r.Log ($n + 4) 120 | Out-Null } catch { }
    $code = Finish-Shim $r.H
    $h = LF-Handover $r
    Start-Sleep 1
    $speed = LF-Line 'coordinator' 'Server speed at playback start.*film_h264_lent' -Last
    $okB1 = $why -and $back -and $h.LocalSegs -le 3 -and $h.Problems.Count -eq 0 -and $code -eq 0 -and $speed
    Add-Result 'LFb' 'Démarrage rapide : serveur trop lent (0,8x)' $(if ($okB1) { 'PASS' } else { 'FAIL' }) (
        "« $why » ({0:0.00} s après le lancement) ; « $($h.Resume) » ; « $back » ({1:0.00} s) ; {2} segment(s) serveur ; 1er segment du worker {3:0.00} s après la décision ; $($h.Junction) ; anomalies : $(LF-Problems $h) ; code $code | coordinateur : « $speed »" -f
        (LF-Since $r.T0 $why), (LF-Since $r.T0 $back), $h.LocalSegs, $h.WorkerStart)

    # 2e lecture du même fichier : avis « serveur trop lent pour ce film », aucun lancement du ffmpeg serveur.
    $r2 = LF-Start 'lfb2' @{ ReadRate = 0.8 } @{ InputPath = $H264Slow }
    $direct = Wait-LogLine $r2.Log 'directement sur un worker' 15
    $segs2 = $null; try { $segs2 = Wait-Segments $r2.Log 3 120 } catch { }
    $job2 = @(Get-ShimJobs $r2.Log) | Select-Object -First 1
    $first2 = LF-Since $r2.T0 (LF-Line $r2.Log 'SegmentComplete=')
    $code2 = Finish-Shim $r2.H
    $fake2 = LF-FakeRuns 'lfb2'
    $okB2 = $direct -match 'serveur trop lent pour ce film' -and (Prop $job2 'How') -eq 'running on' -and $segs2 -and $fake2 -eq 0 -and $code2 -eq 0
    Add-Result 'LFb2' 'Démarrage rapide : 2e lecture d''un film trop lent pour le serveur' $(if ($okB2) { 'PASS' } else { 'FAIL' }) (
        "« $direct » ; job $(Prop $job2 'Job') $(Prop $job2 'How') $(Prop $job2 'Worker') ; 1er segment (worker) {0:0.00} s après le lancement ; lancements du ffmpeg serveur : $fake2 ; code $code2" -f $first2)

    # b3) Serveur qui ne produit rien (démarrage retardé de 30 s) : la règle des 4 s doit confier la lecture au worker.
    $r3 = LF-Start 'lfb3' @{ StartDelayMs = 30000 } @{ InputPath = $H264Hang }
    $why3 = Wait-LogLine $r3.Log 'premier segment trop lent' 15
    $back3 = Wait-LogLine $r3.Log 'back on worker' 45
    $segs3 = $null; try { $segs3 = Wait-Segments $r3.Log 3 120 } catch { }
    $code3 = Finish-Shim $r3.H
    Start-Sleep 1
    $left3 = @(LF-SessionProcs 'lfb3').Count
    $h3 = LF-Handover $r3
    $speed3 = LF-Line 'coordinator' 'Server speed at playback start.*film_h264_bloque' -Last
    $backAt = LF-Since $r3.T0 $back3
    $okB3 = $why3 -and $back3 -and $backAt -le 8 -and $segs3 -and $h3.Problems.Count -eq 0 -and $code3 -eq 0 -and $left3 -eq 0
    Add-Result 'LFb3' 'Démarrage rapide : serveur bloqué au démarrage (règle des 4 s)' $(if ($okB3) { 'PASS' } else { 'FAIL' }) (
        "« $why3 » ({0:0.00} s après le lancement) ; « $($h3.Resume) » ; « $back3 » ({1:0.00} s, attendu ≤ 8 s) ; 1er segment (worker) {2:0.00} s après le lancement ; anomalies : $(LF-Problems $h3) ; code $code3 ; processus serveur restants : $left3 | coordinateur : « $speed3 »" -f
        (LF-Since $r3.T0 $why3), $backAt, (LF-Since $r3.T0 (LF-Line $r3.Log 'SegmentComplete=')))
}

# c) Tone mapping : directement sur le worker, sans démarrage serveur.
function LFc {
    Ensure-Workers @('w1') -Only
    $r = LF-Start 'lfc' @{} @{ InputPath = $HDR; Hdr = $true }
    $segs = $null; try { $segs = Wait-Segments $r.Log 3 120 } catch { }
    $job = @(Get-ShimJobs $r.Log) | Select-Object -First 1
    $first = LF-Since $r.T0 (LF-Line $r.Log 'SegmentComplete=')
    $code = Finish-Shim $r.H
    $serverStart = LF-Line $r.Log 'marrage sur le serveur'
    $fake = LF-FakeRuns 'lfc'
    $ok = $segs -and (Prop $job 'How') -eq 'running on' -and -not $serverStart -and $fake -eq 0 -and $code -eq 0 -and @(Test-SegmentContinuity $segs).Count -eq 0
    Add-Result 'LFc' 'Démarrage rapide : tone mapping directement sur le worker' $(if ($ok) { 'PASS' } else { 'FAIL' }) (
        "job $(Prop $job 'Job') $(Prop $job 'How') $(Prop $job 'Worker') ; 1er segment {0:0.00} s après le lancement ; démarrage serveur : $([bool]$serverStart) ; lancements du ffmpeg serveur : $fake ; code $code" -f $first)
}

# d) Aucun worker : tout sur le serveur sans erreur. d2) un worker arrive : bascule sans préchargement (comparaison).
function LFd {
    foreach ($w in @('w1', 'w2', 'w3')) { Stop-Worker $w }
    $none = LF-WaitNoWorker 40
    $r = LF-Start 'lfd' @{ ReadRate = 3 }    # 3x : nettement au-dessus du seuil (cf. LFb4 pour 1,5x), et le film ne se termine pas avant d2
    $segs = $null; try { $segs = Wait-Segments $r.Log 12 120 } catch { }
    $advice = LF-Line $r.Log 'marrage sur le serveur|directement sur un worker'
    $remote = LF-Line $r.Log '(running on|back on|continues on) worker'
    $errors = @(Select-String -Path (Join-Path $Paths.Logs "$($r.Log).log") -Pattern 'remote-transcode: .*(unexpected error|could not start|does not answer|unreachable|failed)|Unrecognized option|Invalid loglevel|Error opening|Error while' -ErrorAction SilentlyContinue | ForEach-Object { $_.Line.Trim() })
    $check = LF-Line $r.Log 'hand-back check'
    $prob = @(Test-SegmentContinuity @($segs))
    $fake = LF-FakeRuns 'lfd'
    $okD = $none -and $segs -and @($segs)[0].Index -eq 0 -and -not $remote -and $errors.Count -eq 0 -and $prob.Count -eq 0 -and $fake -eq 1 -and -not $r.H.HasExited
    Add-Result 'LFd' 'Démarrage rapide : aucun worker, tout sur le serveur' $(if ($okD) { 'PASS' } else { 'FAIL' }) (
        "aucun worker disponible : $none ; « $advice » ; $(@($segs).Count) segments (indices $(LF-Range $segs)) par le serveur, anomalies : $(if ($prob) { $prob -join ' ; ' } else { 'aucune' }) ; sondage : « $check » ; job distant : « $remote » ; erreurs : $(if ($errors) { $errors -join ' / ' } else { 'aucune' }) ; lancements du faux ffmpeg : $fake")

    # d2) w1 redémarré (cache vide, aucun préchargement possible) : la lecture lui est rendue au segment exact.
    Set-TestWorkerConfig 'w1' @{ Available = $true; Reclaim = 'FinishPlayback' }
    Start-Worker 'w1' | Out-Null
    Wait-WorkerState 'w1' 'Available' 120 | Out-Null
    $back = Wait-LogLine $r.Log 'back on worker' 40
    $n = @(Get-SegmentLines $r.Log).Count
    try { Wait-Segments $r.Log ($n + 4) 120 | Out-Null } catch { }
    $code = Finish-Shim $r.H
    Start-Sleep 1
    $left = @(LF-SessionProcs 'lfd').Count
    $h = LF-Handover $r
    $pre = LF-Line (WorkerLog 'w1') 'préchargement de'
    $okD2 = $back -and $h.Problems.Count -eq 0 -and $code -eq 0 -and $left -eq 0 -and -not $pre
    Add-Result 'LFd2' 'Démarrage rapide : worker revenu, bascule sans préchargement' $(if ($okD2) { 'PASS' } else { 'FAIL' }) (
        "« $($h.Resume) » ; « $back » ; 1er segment du worker {0:0.00} s après la décision (avec préchargement, LFa : {1}) ; $($h.Junction) ; préchargement : « $pre » ; anomalies : $(LF-Problems $h) ; code $code ; processus serveur restants : $left" -f
        $h.WorkerStart, $(if ($null -ne $script:LFPrefetchStart) { '{0:0.00} s' -f $script:LFPrefetchStart } else { 'non mesuré' }))
}

# e) 'q' pendant la phase serveur : tout s'arrête, aucun ffmpeg orphelin, aucun job distant.
function LFe {
    Ensure-Workers @('w1') -Only
    $r = LF-Start 'lfe' @{ ReadRate = 3 } @{ InputPath = $H264Q }
    $segs = $null; try { $segs = Wait-Segments $r.Log 2 30 } catch { }
    $before = @(LF-SessionProcs 'lfe')
    $tq = Get-Date
    $code = Finish-Shim $r.H 20
    $exitSec = ((Get-Date) - $tq).TotalSeconds
    Start-Sleep 1
    $after = @(LF-SessionProcs 'lfe')
    Start-Sleep 6     # ni le délai de 4 s ni le sondage ne doivent relancer quoi que ce soit après 'q'
    $late = @(LF-SessionProcs 'lfe').Count
    $jobs = @(Get-ShimJobs $r.Log)
    $fakePid = @($before | Where-Object Name -eq 'fake-emby-ffmpeg.exe' | ForEach-Object ProcessId)
    $fakeExit = if ($fakePid.Count) { LF-Line 'fake-emby-ffmpeg' "exit pid=$($fakePid[0]) " -Last } else { $null }
    $ok = $segs -and $before.Count -ge 2 -and $code -eq 0 -and $exitSec -lt 5 -and $after.Count -eq 0 -and $late -eq 0 -and $jobs.Count -eq 0
    Add-Result 'LFe' 'Démarrage rapide : arrêt « q » pendant la phase serveur' $(if ($ok) { 'PASS' } else { 'FAIL' }) (
        "$(@($segs).Count) segments serveur avant 'q' ; processus de la session avant : $(($before | ForEach-Object { "$($_.Name)#$($_.ProcessId)" }) -join ', ') ; 'q' -> sortie du relais en {0:0.00} s (code $code) ; faux ffmpeg : « $fakeExit » ; processus restants : $($after.Count) (puis $late après 6 s) ; jobs distants : $($jobs.Count)" -f $exitSec)
}

# f) Avis du coordinateur : le coordinateur sonde la source (ffprobe) avant de répondre à ShimStartQuery, et le relais
#    n'attend que 2 s. Coordinateur redémarré avec le faux ffmpeg en mode ffprobe (durée de chaque sonde journalisée),
#    puis rétabli. LFf0 : sonde réelle (aucun délai ajouté), fichier jamais sondé ; LFf : sonde ralentie de 3 s.
#    Attendu dans les deux cas : démarrage sur le serveur PUIS bascule vers le worker.
function LFf {
    Ensure-Workers @('w1') -Only
    Write-CoordinatorConfig @{ ServerFfprobe = $Paths.FakeFfmpeg }
    Stop-Mine $Bench.Coordinator.Pid -Tree
    $Bench.Coordinator = Start-Coordinator 'coordinator-lff'
    try {
        Wait-WorkerState 'w1' 'Available' 120 | Out-Null
        foreach ($c in @(@{ Id = 'LFf0'; Delay = 0; File = $H264Advice0; Session = 'lff0' }, @{ Id = 'LFf'; Delay = 3000; File = $H264Advice; Session = 'lff' })) {
            $name = [IO.Path]::GetFileName($c.File)
            $r = LF-Start $c.Session @{ ProbeDelayMs = $c.Delay } @{ InputPath = $c.File }
            $segs = $null; try { $segs = Wait-Segments $r.Log 3 60 } catch { }
            $back = Wait-LogLine $r.Log 'back on worker' 40
            $n = @(Get-SegmentLines $r.Log).Count
            $code = Finish-Shim $r.H
            $lines = @(Select-String -Path (Join-Path $Paths.Logs "$($r.Log).log") -Pattern 'remote-transcode:' -ErrorAction SilentlyContinue | ForEach-Object { $_.Line.Trim() })
            $fakeStart = LF-Line 'fake-emby-ffmpeg' 'start pid=.*' -Last
            $probes = @(Select-String -Path (Join-Path $Paths.Logs 'fake-emby-ffmpeg.log') -Pattern "probe pid=(\d+) .*$([regex]::Escape($name))" -ErrorAction SilentlyContinue)
            $durations = foreach ($p in $probes) {
                $probePid = $p.Matches[0].Groups[1].Value
                $end = LF-Line 'fake-emby-ffmpeg' "probe-end pid=$probePid " -Last
                "$($p.Line.Substring(0, 12)) ($(if ($end -match 'ms=(\d+)') { "$($Matches[1]) ms" } else { '?' }))"
            }
            $ok = [bool]$back -and $lines.Count -gt 0
            Add-Result $c.Id "Démarrage rapide : 1re lecture d'un fichier, sonde ffprobe du coordinateur +$($c.Delay) ms" $(if ($ok) { 'PASS' } else { 'FAIL' }) (
                "sondes ffprobe du coordinateur (début, durée) : $($durations -join ', ') ; lignes remote-transcode du relais : $(if ($lines) { $lines -join ' / ' } else { 'AUCUNE' }) ; ffmpeg serveur lancé {0:0.00} s après le relais ; $n segments, tous par le serveur : $(-not $back) ; bascule : « $back » ; code $code" -f
                (LF-Since $r.T0 $fakeStart))
        }
    } finally {
        Set-FakeFfmpeg
        Write-CoordinatorConfig
        Stop-Mine $Bench.Coordinator.Pid -Tree
        $Bench.Coordinator = Start-Coordinator 'coordinator-after-lff'
        Wait-WorkerState 'w1' 'Available' 120 | Out-Null
    }
}

# b4) Serveur à 1,5x, donc AU-DESSUS du seuil de 1,2x : la lecture doit rester sur le serveur jusqu'à ~20 s de vidéo
#     d'avance, puis basculer, et la vitesse retenue doit être ≈ 1,5x (pas « trop lent »).
function LFb4 {
    Ensure-Workers @('w1') -Only
    $r = LF-Start 'lfb4' @{ ReadRate = 1.5 } @{ InputPath = $H264Mid }
    $back = Wait-LogLine $r.Log 'back on worker' 60
    $n = @(Get-SegmentLines $r.Log).Count
    try { Wait-Segments $r.Log ($n + 3) 120 | Out-Null } catch { }
    $code = Finish-Shim $r.H
    $h = LF-Handover $r
    Start-Sleep 1
    $why = LF-Line $r.Log 'trop lent'
    $first = LF-Line $r.Log 'SegmentComplete='
    $speed = LF-Line 'coordinator' 'Server speed at playback start.*film_h264_1x5' -Last
    $ok = $back -and -not $why -and $h.Pos -ge 19 -and $h.Problems.Count -eq 0 -and $code -eq 0 -and $speed -notmatch ' 0\.\d\dx| 1\.[01]\dx'
    Add-Result 'LFb4' 'Démarrage rapide : serveur à 1,5x (au-dessus du seuil)' $(if ($ok) { 'PASS' } else { 'FAIL' }) (
        "« $(LF-Line $r.Log 'marrage sur le serveur') » ; 1er segment serveur {0:0.00} s après le lancement (faux ffmpeg lancé {1:0.00} s après) ; « $why » ; « $($h.Resume) » ; « $back » ({2:0.00} s) ; {3} segment(s) serveur ; anomalies : $(LF-Problems $h) ; code $code | coordinateur : « $speed »" -f
        (LF-Since $r.T0 $first), (LF-Since $r.T0 (LF-Line 'fake-emby-ffmpeg' 'start pid=.*readrate=1.5' -Last)), (LF-Since $r.T0 $back), $h.LocalSegs)
}
