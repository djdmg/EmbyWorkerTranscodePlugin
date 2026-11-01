# Mise en veille simulée d'un worker : le processus du worker et ses ffmpeg sont gelés (NtSuspendProcess), comme
# pendant une veille, puis dégelés. Le coordinateur doit constater la perte et confier le travail ailleurs ; au réveil,
# le worker ne doit rien livrer de périmé, arrêter ses ffmpeg et redevenir disponible.
#   pwsh -File run-tests.ps1 -Only T13

Add-Type -Namespace Bench -Name Freeze -MemberDefinition @'
[DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr handle);
[DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr handle);
'@

# Le worker et ses ffmpeg (processus enfants).
function Get-WorkerTree([string]$name) {
    $h = $Bench.Workers[$name]
    $ids = @($h.Pid)
    $ids += @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($h.Pid)" | ForEach-Object { [int]$_.ProcessId })
    $ids
}

function Set-Frozen([int[]]$ids, [bool]$frozen) {
    foreach ($id in $ids) {
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        if (-not $p) { continue }
        if ($frozen) { [void][Bench.Freeze]::NtSuspendProcess($p.Handle) } else { [void][Bench.Freeze]::NtResumeProcess($p.Handle) }
    }
}

function T13 {
    $sleepSec = 40

    # a) Lecture : le worker « s'endort » pendant la lecture, un autre worker est libre.
    Ensure-Workers @('w1', 'w2')
    $s = Start-Shim (New-PlaybackArgs -InputPath $H264 -Session 't13a' -Width 1920 -Height 1080 -Preset medium) 't13a'
    Wait-Segments 'shim-t13a' 4 120 | Out-Null
    $job = (@(Get-ShimJobs 'shim-t13a'))[0]
    $victim = $job.Worker
    $other = if ($victim -eq 'w1') { 'w2' } else { 'w1' }
    $tree = Get-WorkerTree $victim
    $tk = Get-Date
    Set-Frozen $tree $true
    $cont = Wait-LogLine 'shim-t13a' 'continues on worker' 60
    $contSec = ((Get-Date) - $tk).TotalSeconds
    $remaining = $sleepSec - ((Get-Date) - $tk).TotalSeconds
    if ($remaining -gt 0) { Start-Sleep -Seconds $remaining }
    $n = @(Get-SegmentLines 'shim-t13a').Count
    Set-Frozen $tree $false
    # Au réveil : son ffmpeg de lecture doit s'arrêter, rien ne doit être livré en double, le worker revient.
    $gone = $false; for ($i = 0; $i -lt 120 -and -not $gone; $i++) { $gone = @(Get-JobFfmpeg $job.Job).Count -eq 0; if (-not $gone) { Start-Sleep -Milliseconds 250 } }
    $back = $null; try { $back = Wait-WorkerState $victim 'Available' 120 } catch { }
    try { Wait-Segments 'shim-t13a' ($n + 5) 120 | Out-Null } catch { }
    $code = Finish-Shim $s
    $h = Check-Handover 'shim-t13a'
    $drop = Log-Excerpt 'coordinator' "Worker $victim" 1
    $stale = @(Log-Excerpt (WorkerLog $victim) 'no longer wants|410' 5)
    $ok = $cont -match $other -and $gone -and $h.Problems.Count -eq 0 -and $code -eq 0 -and $back.State -eq 'Available'
    Add-Result 'T13a' 'Veille d''un worker pendant une lecture' $(if ($ok) { 'PASS' } else { 'FAIL' }) (
        "$victim gelé $sleepSec s (worker + $($tree.Count - 1) ffmpeg) ; « $cont » après {0:0.0} s ; $($h.Resume) ; $($h.Junction) ; anomalies : $(if ($h.Problems) { $h.Problems -join ' ; ' } else { 'aucune' }) ; code $code | réveil : ffmpeg du job arrêté : $gone ; état : $($back.State) ; coordinateur : « $drop » ; worker : « $($stale -join ' / ') »" -f $contSec)

    # b) Conversion : le worker s'endort au milieu d'une tranche, un autre worker est libre.
    Ensure-Workers @('w1', 'w2')
    $out = Join-Path $Paths.Out 'conv-t13b.mp4'
    $s = Start-Shim (New-ConversionArgs -InputPath $H264 -OutputPath $out -Width 1920 -Height 1080) 't13b'
    $e = $null
    $deadline = (Get-Date).AddSeconds(300)
    while ((Get-Date) -lt $deadline) { $e = Encode-Summary $out; if ($e -and $e.Chunks -ge 1 -and $e.Worker) { break }; Start-Sleep 1 }
    $victim = $e.Worker; $chunksAt = $e.Chunks
    $tree = Get-WorkerTree $victim
    Set-Frozen $tree $true
    Start-Sleep -Seconds $sleepSec
    Set-Frozen $tree $false
    $code = Wait-Exit $s 900
    $chk = Check-Output $out $SourceH264 1
    $e2 = Encode-Summary $out
    $lost = @(Log-Excerpt 'coordinator' 'Job .* lost' 50)[-1]
    $verify = Log-Excerpt 'shim-t13b' 'source .*sultat' 1
    $back = $null; try { $back = Wait-WorkerState $victim 'Available' 120 } catch { }
    $leftFf = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($Bench.Workers[$victim].Pid)" | Where-Object Name -eq 'ffmpeg.exe').Count
    $ok = $code -eq 0 -and $chk.Ok -and (Prop $e2 'Status') -eq 'Completed' -and $back.State -eq 'Available' -and $leftFf -eq 0
    Add-Result 'T13b' 'Veille d''un worker pendant une conversion' $(if ($ok) { 'PASS' } else { 'FAIL' }) (
        "$victim gelé $sleepSec s après $chunksAt morceau(x) ; « $lost » ; final : $(Prop $e2 'Status'), $(Prop $e2 'Chunks') morceaux, note « $(Prop $e2 'Note') » ; vérification : « $verify » ; code $code ; $($chk.Text) | réveil : $($back.State), ffmpeg restants : $leftFf")
}

# Échec d'un worker sur une tranche (son ffmpeg s'arrête en erreur, le worker reste connecté : GPU perdu pendant
# l'arrêt de Windows, par exemple). Le worker doit être écarté de cette conversion seulement, la conversion doit se
# terminer avec l'autre worker (ou le serveur), et rien ne doit être rendu à Emby.
#   pwsh -File run-tests.ps1 -Only T14
function T14 {
    Ensure-Workers @('w1', 'w2')
    $src = Join-Path $Paths.Media 'film_2398.mkv'
    $out = Join-Path $Paths.Out 'conv-t14.mp4'
    $s = Start-Shim (New-ConversionArgs -InputPath $src -OutputPath $out) 't14'
    # Attendre que w1 encode une tranche, puis tuer son ffmpeg (pas le worker).
    $killed = $null; $deadline = (Get-Date).AddSeconds(180)
    while ((Get-Date) -lt $deadline -and -not $killed) {
        $ff = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($Bench.Workers['w1'].Pid)" | Where-Object Name -eq 'ffmpeg.exe')
        if ($ff) { Stop-Process -Id $ff[0].ProcessId -Force; $killed = Get-Date }
        else { Start-Sleep -Milliseconds 500 }
    }
    $code = Wait-Exit $s 1200
    $aside = Log-Excerpt 'coordinator' 'set aside for this conversion' 1
    $after = if ($killed) { @(Get-Content (Join-Path $Paths.Logs 'coordinator.log') | Where-Object { $_ -match 'running on w1 .*film_2398' -and [datetime]::ParseExact($_.Substring(0, 12), 'HH:mm:ss.fff', $null) -gt $killed.AddSeconds(1) }).Count } else { -1 }
    $given = @(Log-Excerpt 'coordinator' 'given back to Emby' 3).Count
    $e = Encode-Summary $out
    $verify = Log-Excerpt 'shim-t14' 'source .*sultat|rejet' 1
    $ok = $killed -and $aside -and $after -eq 0 -and $given -eq 0 -and $code -eq 0 -and (Prop $e 'Status') -eq 'Completed' -and $verify -notmatch 'rejet'
    Add-Result 'T14' 'Échec d''un worker sur une tranche : écarté de la conversion seulement' $(if ($ok) { 'PASS' } else { 'FAIL' }) (
        "ffmpeg de w1 tué : $([bool]$killed) | « $aside » | tranches données à w1 ensuite : $after | rendue à Emby : $given | final : $(Prop $e 'Status'), workers $(Prop $e 'Worker'), note « $(Prop $e 'Note') » | contrôle : « $verify » | code $code")
}

# Un seul worker sur une conversion : le serveur encode aussi des tranches en parallèle (EncodeLocalAlongsideWorkers),
# et le worker démarre sa tranche suivante quelques secondes avant la fin de la précédente (EncodeOverlapSeconds :
# deux tranches de w1 en même temps). Le résultat doit passer le contrôle à l'image près.
#   pwsh -File run-tests.ps1 -Only T15
function T15 {
    Ensure-Workers @('w1') -Only
    $src = Join-Path $Paths.Media 'film_2398.mkv'
    $out = Join-Path $Paths.Out 'conv-t15.mp4'
    $s = Start-Shim (New-ConversionArgs -InputPath $src -OutputPath $out) 't15'
    $overlap = 0; $deadline = (Get-Date).AddSeconds(1200)
    while (-not $s.HasExited -and (Get-Date) -lt $deadline) {
        $e = Encode-Summary $out
        if ($e) { $overlap = [math]::Max($overlap, @($e.runs | Where-Object worker -eq 'w1').Count) }
        Start-Sleep -Milliseconds 300
    }
    $code = Wait-Exit $s 60
    $alongside = Log-Excerpt 'coordinator' 'alongside the workers, encoding slice' 1
    $handOver = @(Log-Excerpt 'coordinator' 'stopping the local encode to hand over' 20).Count
    $e = Encode-Summary $out
    $verify = Log-Excerpt 'shim-t15' 'source .*sultat|rejet' 1
    $chk = Check-Output $out (Probe $src).Duration 1
    $ok = $alongside -and $overlap -ge 2 -and $code -eq 0 -and (Prop $e 'Status') -eq 'Completed' -and $verify -notmatch 'rejet' -and $chk.Ok
    Add-Result 'T15' 'Serveur en parallèle d''un worker et tranche suivante démarrée en avance' $(if ($ok) { 'PASS' } else { 'FAIL' }) (
        "serveur : « $alongside » | tranches simultanées de w1 : $overlap | passages au worker : $handOver | final : $(Prop $e 'Status'), workers $(Prop $e 'Worker'), note « $(Prop $e 'Note') » | contrôle : « $verify » | code $code | $($chk.Text)")
}

# Audio converti à l'avance (plugin 0.7.18) : pendant que les workers encodent la vidéo, le serveur convertit l'audio
# avec les options d'Emby ; l'assemblage ne fait plus que recopier les flux. Preuve d'absence de décalage : la même
# source et la même commande d'Emby donnent, avec l'assemblage 0.7.17 (relais shim-classic, UseAudioAhead = false) et
# avec l'audio converti à l'avance, des pistes audio identiques (paquets, horodatages, contenu, paramètres).
#   pwsh -File run-tests.ps1 -Only T16

function Get-AudioPackets([string]$file) {
    @(& $Paths.ServerFfprobe -v error -select_streams a -show_entries packet=stream_index,pts,dts,duration,size,flags -of csv=p=0 $file)
}

function Get-Md5([string[]]$ffArgs) {
    "$(& $Paths.ServerFfmpeg -hide_banner -v error @ffArgs -f md5 - 2>$null)".Trim() -replace '^MD5=', ''
}

function Get-AssemblySeconds([string]$log) {
    $m = @(Log-Excerpt $log 'assemblage terminé en' 5) | Select-Object -Last 1
    if ($m -match 'assemblage terminé en ([\d.]+) s') { [double]::Parse($Matches[1], [cultureinfo]::InvariantCulture) } else { $null }
}

# Compare deux sorties de la même conversion : (a) début et durée de chaque flux (1 ms au plus), tous les champs
# ffprobe des pistes audio (codec, paramètres, débit, étiquettes, dispositions) ; (b) tous les paquets audio (pts, dts,
# durée, taille, drapeaux) ; (c) le contenu audio (flux compressé et décodé, MD5) ; (d) le 1er paquet vidéo.
function Compare-Conversions([string]$classic, [string]$ahead) {
    $inv = [cultureinfo]::InvariantCulture
    $problems = @(); $facts = @()
    $c = (& $Paths.ServerFfprobe -v error -print_format json -show_streams -show_format $classic) | ConvertFrom-Json
    $a = (& $Paths.ServerFfprobe -v error -print_format json -show_streams -show_format $ahead) | ConvertFrom-Json
    if (@($c.streams).Count -ne @($a.streams).Count) { $problems += "flux : $(@($c.streams).Count) / $(@($a.streams).Count)" }
    for ($i = 0; $i -lt [math]::Min(@($c.streams).Count, @($a.streams).Count); $i++) {
        $sc = $c.streams[$i]; $sa = $a.streams[$i]
        $ds = [math]::Abs([double]::Parse($sc.start_time, $inv) - [double]::Parse($sa.start_time, $inv)) * 1000
        $dd = [math]::Abs([double]::Parse($sc.duration, $inv) - [double]::Parse($sa.duration, $inv)) * 1000
        $facts += ("{0}#{1} début {2}/{3} durée {4}/{5}" -f $sc.codec_type, $i, $sc.start_time, $sa.start_time, $sc.duration, $sa.duration)
        if ($ds -gt 1) { $problems += ("{0}#{1} : début décalé de {2:0.###} ms" -f $sc.codec_type, $i, $ds) }
        if ($dd -gt 1) { $problems += ("{0}#{1} : durée différente de {2:0.###} ms" -f $sc.codec_type, $i, $dd) }
        if ($sc.codec_type -eq 'audio' -and ($sc | ConvertTo-Json -Depth 6 -Compress) -ne ($sa | ConvertTo-Json -Depth 6 -Compress)) {
            $diff = @($sc.PSObject.Properties | Where-Object { ($_.Value | ConvertTo-Json -Depth 6 -Compress) -ne ((Prop $sa $_.Name) | ConvertTo-Json -Depth 6 -Compress) } | ForEach-Object Name)
            $problems += "audio#${i} : champs ffprobe différents ($($diff -join ', '))"
        }
    }
    $fc = @($c.format.PSObject.Properties | Where-Object Name -in 'format_name', 'nb_streams', 'start_time', 'duration' | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ';'
    $fa = @($a.format.PSObject.Properties | Where-Object Name -in 'format_name', 'nb_streams', 'start_time', 'duration' | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ';'
    if ($fc -ne $fa) {
        $problems += "conteneur : $($c.format.format_name) $($c.format.start_time) $($c.format.duration) / $($a.format.format_name) $($a.format.start_time) $($a.format.duration)"
    }
    if ((Prop $c.format 'tags' | ConvertTo-Json -Compress) -ne (Prop $a.format 'tags' | ConvertTo-Json -Compress)) { $problems += 'étiquettes du conteneur différentes' }
    $pc = Get-AudioPackets $classic; $pa = Get-AudioPackets $ahead
    $different = 0; for ($i = 0; $i -lt [math]::Min($pc.Count, $pa.Count); $i++) { if ($pc[$i] -ne $pa[$i]) { $different++ } }
    $facts += "paquets audio $($pc.Count)/$($pa.Count), différents : $different, 1er « $($pc[0]) »/« $($pa[0]) », dernier « $($pc[-1]) »/« $($pa[-1]) »"
    if ($pc.Count -ne $pa.Count -or $different -gt 0) { $problems += "paquets audio différents ($different, $($pc.Count)/$($pa.Count))" }
    $copyC = Get-Md5 @('-i', $classic, '-map', '0:a', '-c', 'copy'); $copyA = Get-Md5 @('-i', $ahead, '-map', '0:a', '-c', 'copy')
    $pcmC = Get-Md5 @('-i', $classic, '-map', '0:a'); $pcmA = Get-Md5 @('-i', $ahead, '-map', '0:a')
    $facts += "MD5 audio compressé $($copyC.Substring(0, [math]::Min(8, $copyC.Length)))/$($copyA.Substring(0, [math]::Min(8, $copyA.Length))), décodé $($pcmC.Substring(0, [math]::Min(8, $pcmC.Length)))/$($pcmA.Substring(0, [math]::Min(8, $pcmA.Length)))"
    if (-not $copyC -or $copyC -ne $copyA) { $problems += 'contenu audio compressé différent' }
    if (-not $pcmC -or $pcmC -ne $pcmA) { $problems += 'audio décodé différent' }
    $vc = "$(& $Paths.ServerFfprobe -v error -select_streams v:0 -read_intervals '%+#1' -show_entries packet=pts_time -of csv=p=0 $classic)".Trim()
    $va = "$(& $Paths.ServerFfprobe -v error -select_streams v:0 -read_intervals '%+#1' -show_entries packet=pts_time -of csv=p=0 $ahead)".Trim()
    $facts += "1er paquet vidéo $vc/$va"
    if ($vc -ne $va) { $problems += "1er paquet vidéo $vc / $va" }
    @{ Ok = $problems.Count -eq 0; Text = ($facts -join ' ; ') + $(if ($problems) { ' ; ÉCARTS : ' + ($problems -join ' ; ') } else { ' ; aucun écart' }) }
}

# Une conversion par le relais donné ; retourne le code, la durée de l'assemblage et le contrôle du résultat.
function Invoke-T16Conversion([string]$src, [string]$name, [int[]]$audio, [string]$exe = $Paths.Shim, [scriptblock]$during = $null) {
    $out = Join-Path $Paths.Out "conv-$name.mp4"
    $s = Start-Shim (New-ConversionArgs -InputPath $src -OutputPath $out -AudioStreams $audio) $name $exe
    $extra = if ($during) { & $during $s } else { $null }
    $code = Wait-Exit $s 1500
    $chk = Check-Output $out (Probe $src).Duration $audio.Count
    [pscustomobject]@{
        Out = $out; Code = $code; Check = $chk; Extra = $extra; Summary = Encode-Summary $out
        Assembly = Get-AssemblySeconds "shim-$name"
        Ahead = [bool](Log-Excerpt "shim-$name" 'audio converti à l''avance par le serveur' 1)
        Verify = Log-Excerpt "shim-$name" 'source .*sultat|rejet' 2
    }
}

# Coordinateur redémarré avec d'autres réglages ; son journal précédent est gardé sous le nom $saveAs (le nouveau garde
# le nom « coordinator », que lisent les tests).
function Restart-BenchCoordinator([hashtable]$overrides, [string]$saveAs) {
    Write-CoordinatorConfig $overrides
    Stop-Mine $Bench.Coordinator.Pid -Tree
    Copy-Item (Join-Path $Paths.Logs 'coordinator.log') (Join-Path $Paths.Logs "$saveAs.log") -Force
    $Bench.Coordinator = Start-Coordinator
    foreach ($w in @('w1', 'w2')) { if ($Bench.Workers[$w]) { Wait-WorkerState $w 'Available' 120 | Out-Null } }
}

function T16 {
    # L'audio converti à l'avance est désactivé par défaut (EncodeAudioAhead) : activé pour ce test seulement.
    Restart-BenchCoordinator @{ EncodeAudioAhead = $true } 'coordinator-before-t16'
    try { T16Core } finally { Restart-BenchCoordinator @{} 'coordinator-t16' }
}

function T16Core {
    Ensure-Workers @('w1', 'w2')
    New-Item -ItemType Directory -Force (Split-Path $Paths.ShimClassic) | Out-Null
    Copy-Item $Paths.Shim $Paths.ShimClassic -Force
    Write-ShimConfig $Paths.ShimClassic -useAudioAhead $false
    # Source dont l'audio commence 0,5 s après la vidéo (2 pistes) : c'est là qu'un décalage apparaîtrait.
    $av = Join-Path $Paths.Media 'film_h264_av05.mkv'
    if (-not (Test-Path $av)) {
        & $Paths.ServerFfmpeg -v error -y -i $H264 -itsoffset 0.5 -i $H264 -map 0:0 -map 1:1 -map 1:2 -c copy $av
    }

    # a) Synchro : assemblage 0.7.17 et audio converti à l'avance, même source et même commande.
    $ca = Invoke-T16Conversion $av 't16a-classic' @(1, 2) $Paths.ShimClassic
    $aa = Invoke-T16Conversion $av 't16a-ahead' @(1, 2)
    $cmp = Compare-Conversions $ca.Out $aa.Out
    $used = Log-Excerpt 'coordinator' 'audio converted ahead used' 1
    $ok = $ca.Code -eq 0 -and $aa.Code -eq 0 -and -not $ca.Ahead -and $aa.Ahead -and $used -and $cmp.Ok -and $ca.Check.Ok -and $aa.Check.Ok `
        -and ($aa.Verify -join ' ') -notmatch 'rejet' -and (Prop $aa.Summary 'Status') -eq 'Completed'
    Add-Result 'T16a' 'Audio converti à l''avance : même fichier audio que l''assemblage 0.7.17 (source audio +0,5 s)' $(if ($ok) { 'PASS' } else { 'FAIL' }) (
        "assemblage classique {0} s, avec audio converti {1} s | coordinateur : « $used » | $($cmp.Text) | contrôle : « $($aa.Verify -join ' / ') » | classique : $($ca.Check.Text) | à l'avance : $($aa.Check.Text)" -f $ca.Assembly, $aa.Assembly)

    # b) Durée de l'assemblage sur une source plus longue (11 min 40, AC-3 converti en AAC).
    $src = Join-Path $Paths.Media 'film_2398.mkv'
    $cb = Invoke-T16Conversion $src 't16b-classic' @(1) $Paths.ShimClassic
    $ab = Invoke-T16Conversion $src 't16b-ahead' @(1)
    $cmpb = Compare-Conversions $cb.Out $ab.Out
    $ok = $cb.Code -eq 0 -and $ab.Code -eq 0 -and $ab.Ahead -and $cmpb.Ok -and $ab.Check.Ok -and ($ab.Verify -join ' ') -notmatch 'rejet' -and $ab.Assembly -lt $cb.Assembly
    Add-Result 'T16b' 'Audio converti à l''avance : durée de l''assemblage' $(if ($ok) { 'PASS' } else { 'FAIL' }) (
        "assemblage classique {0} s, avec audio converti {1} s | audio : « $(Prop $ab.Summary 'Audio') » | $($cmpb.Text) | contrôle : « $($ab.Verify -join ' / ') » | $($ab.Check.Text)" -f $cb.Assembly, $ab.Assembly)

    # c) Repli : la conversion de l'audio à l'avance est tuée ; l'assemblage convertit l'audio comme en 0.7.17.
    $long = Join-Path $Paths.Media 'long_30min.mkv'
    $kill = {
        param($s)
        # Tué une fois son démarrage annoncé par le coordinateur (sur ce PC, l'antivirus peut retenir la création du
        # processus plusieurs secondes : tué avant, il ferait échouer le démarrage lui-même, un autre cas de repli).
        $before = @(Log-Excerpt 'coordinator' 'converting the audio ahead' 1000).Count
        $deadline = (Get-Date).AddSeconds(120)
        while ((Get-Date) -lt $deadline -and -not $s.HasExited) {
            if (@(Log-Excerpt 'coordinator' 'converting the audio ahead' 1000).Count -gt $before) {
                $p = @(Get-CimInstance Win32_Process -Filter "Name='ffmpeg.exe' AND ParentProcessId=$($Bench.Coordinator.Pid)" | Where-Object { $_.CommandLine -like '*audio.mp4*' })
                if ($p) { Stop-Process -Id $p[0].ProcessId -Force -ErrorAction SilentlyContinue; return $true }
            }
            Start-Sleep -Milliseconds 150
        }
        $false
    }
    $fc = Invoke-T16Conversion $long 't16c' @(1) $Paths.Shim $kill
    $notUsed = Log-Excerpt 'coordinator' 'audio conversion ahead not used' 3 | Select-Object -Last 1
    $classicAssembly = [bool](Log-Excerpt 'shim-t16c' 'vidéo encodée par les workers, assemblage du fichier' 1)
    $leftover = @(Get-ChildItem (Join-Path $Paths.CoordDir 'encode') -Recurse -Filter 'audio.mp4' -ErrorAction SilentlyContinue).Count
    $ok = $fc.Extra -and $fc.Code -eq 0 -and -not $fc.Ahead -and $classicAssembly -and $notUsed -and $fc.Check.Ok -and ($fc.Verify -join ' ') -notmatch 'rejet' -and (Prop $fc.Summary 'Status') -eq 'Completed' -and $leftover -eq 0
    Add-Result 'T16c' 'Audio converti à l''avance tué : assemblage classique, conversion réussie' $(if ($ok) { 'PASS' } else { 'FAIL' }) (
        "ffmpeg de l'audio tué : $($fc.Extra) | coordinateur : « $notUsed » | audio : « $(Prop $fc.Summary 'Audio') » | assemblage classique : $classicAssembly en $($fc.Assembly) s | contrôle : « $($fc.Verify -join ' / ') » | fichiers audio.mp4 restants : $leftover | final : $(Prop $fc.Summary 'Status') | $($fc.Check.Text)")
}
# Le serveur sait encoder les tranches : une conversion est acceptée même sans aucun worker connecté (plus de
# « conversion sur le serveur » par la commande d'Emby), le serveur commence seul et un worker qui se connecte ensuite
# prend les tranches en attente.

# Tâche de conversion (id) d'une sortie, et noms des machines qui en ont encodé des tranches (journal du coordinateur).
function Get-ConversionRunners([string]$out) {
    $e = Encode-Summary $out
    $id = Prop $e 'Id'
    $workers = @(Select-String -Path (Join-Path $Paths.Logs 'coordinator.log') -Pattern "running on (\S+) \(start segment \d+, conversion $id " -ErrorAction SilentlyContinue |
        ForEach-Object { $_.Matches[0].Groups[1].Value } | Sort-Object -Unique)
    $local = @(Select-String -Path (Join-Path $Paths.Logs 'coordinator.log') -Pattern "Conversion $id`: .*encoding slice \d+ locally" -ErrorAction SilentlyContinue).Count
    [pscustomobject]@{ Id = $id; Workers = $workers; LocalRuns = $local; Summary = $e }
}

# Conversion sans worker connecté ; un worker démarré en cours de route ($startAfter secondes après le début, et pas
# avant que le serveur ait commencé une tranche). Rend le code, les machines, les journaux utiles.
function Invoke-ServerFirstConversion([string[]]$convArgs, [string]$name, [string]$out, [int]$startAfter = 0) {
    foreach ($w in @('w1', 'w2', 'w3')) { if ($Bench.Workers[$w]) { Stop-Worker $w } }
    Start-Sleep -Seconds 2
    $t0 = Get-Date
    $s = Start-Shim $convArgs $name
    $accepted = Wait-LogLine "shim-$name" 'confiée aux workers' 30
    $localSeen = $null
    if ($startAfter -gt 0) {
        $deadline = $t0.AddSeconds(120)
        while ((Get-Date) -lt $deadline -and -not $s.HasExited) {
            if (-not $localSeen -and (Prop (Encode-Summary $out) 'Worker') -match 'NAS') { $localSeen = ((Get-Date) - $t0).TotalSeconds }
            if ($localSeen -and ((Get-Date) - $t0).TotalSeconds -ge $startAfter) { break }
            Start-Sleep -Milliseconds 500
        }
        Start-Worker 'w1' | Out-Null
    }
    $startedAt = ((Get-Date) - $t0).TotalSeconds
    $code = Wait-Exit $s 1800
    [pscustomobject]@{
        Code = $code; Accepted = [bool]$accepted; LocalSeen = $localSeen; WorkerStartedAt = $startedAt
        Emby = Log-Excerpt "shim-$name" 'conversion sur le serveur' 1 | Select-Object -First 1
        Verify = Log-Excerpt "shim-$name" 'source .*sultat|rejet' 2
        Runners = Get-ConversionRunners $out
    }
}

function T17 {
    $src = Join-Path $Paths.Media 'long_30min.mkv'
    $out = Join-Path $Paths.Out 'conv-t17.mp4'
    $r = Invoke-ServerFirstConversion (New-ConversionArgs -InputPath $src -OutputPath $out) 't17' $out 30
    $chk = Check-Output $out (Probe $src).Duration 1
    $status = Prop $r.Runners.Summary 'Status'
    $ok = $r.Accepted -and -not $r.Emby -and $r.Code -eq 0 -and $r.Runners.LocalRuns -gt 0 -and $r.Runners.Workers -contains 'w1' `
        -and $status -eq 'Completed' -and ($r.Verify -join ' ') -notmatch 'rejet' -and $chk.Ok
    Add-Result 'T17' 'Conversion acceptée sans worker connecté : le serveur commence, w1 arrivé ensuite prend des tranches' $(if ($ok) { 'PASS' } else { 'FAIL' }) (
        "acceptée : $($r.Accepted) | commande d'Emby : « $($r.Emby) » | serveur en route après {0:0} s, w1 démarré à {1:0} s | tranches lancées sur le serveur : $($r.Runners.LocalRuns), workers : $($r.Runners.Workers -join ', ') | final : $status, $(Prop $r.Runners.Summary 'Worker') | contrôle : « $($r.Verify -join ' / ') » | code $($r.Code) | $($chk.Text)" -f $r.LocalSeen, $r.WorkerStartedAt)
    Ensure-Workers @('w1', 'w2')
}

# Conversion HDR10 -> SDR « comme Emby » (supertonemap dans le -filter_complex) encodée par le serveur : le ffmpeg
# standard du banc n'a pas supertonemap, la conversion ne réussit que si le serveur applique la même traduction
# (zscale + tonemap) que les workers. a) serveur seul ; b) source de 8 min, w1 démarré en cours de route.
function T18 {
    $noStm = -not (& $Paths.ServerFfmpeg -hide_banner -filters 2>$null | Select-String 'supertonemap')
    $out = Join-Path $Paths.Out 'conv-t18a.mp4'
    $r = Invoke-ServerFirstConversion (New-ConversionArgs -InputPath $HDR -OutputPath $out -Hdr) 't18a' $out
    $chk = Check-Output $out $SourceHdr 1
    $status = Prop $r.Runners.Summary 'Status'
    $ok = $noStm -and $r.Accepted -and -not $r.Emby -and $r.Code -eq 0 -and $r.Runners.LocalRuns -gt 0 -and $r.Runners.Workers.Count -eq 0 `
        -and $status -eq 'Completed' -and ($r.Verify -join ' ') -notmatch 'rejet' -and $chk.Ok
    Add-Result 'T18a' 'Conversion HDR (supertonemap) encodée par le serveur seul, filtre traduit' $(if ($ok) { 'PASS' } else { 'FAIL' }) (
        "ffmpeg du banc sans supertonemap : $noStm | acceptée : $($r.Accepted) | commande d'Emby : « $($r.Emby) » | tranches sur le serveur : $($r.Runners.LocalRuns), workers : $($r.Runners.Workers -join ', ') | final : $status | contrôle : « $($r.Verify -join ' / ') » | code $($r.Code) | $($chk.Text)")

    $long = Join-Path $Paths.Media 'film_hdr10_x4.mkv'
    if (-not (Test-Path $long)) { & $Paths.ServerFfmpeg -v error -y -stream_loop 3 -i $HDR -map 0 -c copy $long }
    $out = Join-Path $Paths.Out 'conv-t18b.mp4'
    $r = Invoke-ServerFirstConversion (New-ConversionArgs -InputPath $long -OutputPath $out -Hdr) 't18b' $out 25
    $chk = Check-Output $out (Probe $long).Duration 1
    $status = Prop $r.Runners.Summary 'Status'
    $ok = $r.Accepted -and -not $r.Emby -and $r.Code -eq 0 -and $r.Runners.LocalRuns -gt 0 -and $r.Runners.Workers -contains 'w1' `
        -and $status -eq 'Completed' -and ($r.Verify -join ' ') -notmatch 'rejet' -and $chk.Ok
    Add-Result 'T18b' 'Conversion HDR : tranches du serveur et de w1 mélangées' $(if ($ok) { 'PASS' } else { 'FAIL' }) (
        "serveur en route après {0:0} s, w1 démarré à {1:0} s | tranches lancées sur le serveur : $($r.Runners.LocalRuns), workers : $($r.Runners.Workers -join ', ') | final : $status, $(Prop $r.Runners.Summary 'Worker') | contrôle : « $($r.Verify -join ' / ') » | code $($r.Code) | $($chk.Text)" -f $r.LocalSeen, $r.WorkerStartedAt)
    Ensure-Workers @('w1', 'w2')
}
# Conversion faite entièrement par le serveur (aucun worker connecté du début à la fin), sur plusieurs tranches :
# chaque tranche doit garder ses propres morceaux et la sortie passer le contrôle à l'image près. Constaté le 01/11 sur
# le serveur réel avec la 0.7.19 : morceaux comptés 4, 8, 12… par tranche et vidéo assemblée trop courte de 3 à 4 images.
#   pwsh -File run-tests.ps1 -Only T19
function T19 {
    foreach ($w in @('w1', 'w2', 'w3')) { if ($Bench.Workers[$w]) { Stop-Worker $w } }
    $src = Join-Path $Paths.Media 'film_2398.mkv'
    $out = Join-Path $Paths.Out 'conv-t19.mp4'
    $s = Start-Shim (New-ConversionArgs -InputPath $src -OutputPath $out) 't19'
    $code = Wait-Exit $s 1800
    $e = Encode-Summary $out
    $assembled = Log-Excerpt 'coordinator' 'assembled from chunks' 1
    $verify = Log-Excerpt 'shim-t19' 'source .*sultat|rejet' 1
    $chk = Check-Output $out (Probe $src).Duration 1
    # The status may read Cancelled when the relay's last message crosses its socket closing (cosmetic, noted 01/11):
    # what matters is the verified file handed to Emby.
    $ok = $code -eq 0 -and $verify -match 'sultat' -and $verify -notmatch 'rejet' -and $chk.Ok
    Add-Result 'T19' 'Conversion faite entièrement par le serveur, sur plusieurs tranches' $(if ($ok) { 'PASS' } else { 'FAIL' }) (
        "final : $(Prop $e 'Status'), workers $(Prop $e 'Worker'), note « $(Prop $e 'Note') », erreur « $(Prop $e 'Error') » | « $assembled » | contrôle : « $verify » | code $code | $($chk.Text)")
}

# Source AV1 10 bits comme Mr. Robot S03E09 (1080p, 23,976 i/s, vidéo décalée de 7 ms : start_time 0,007, audio à 0),
# 8 min tirées de film_2398.mkv. AV1 par QSV si le GPU le permet (≈ 1,5 min), sinon libaom (plus lent).
function Initialize-Av1Media {
    $dst = Join-Path $Paths.Media 'film_av1_10bit.mkv'
    if (Test-Path $dst) { return $dst }
    $src = Join-Path $Paths.Media 'film_2398.mkv'
    $tmp = Join-Path $Paths.Media 'film_av1_10bit.video.mkv'
    $common = @('-hide_banner', '-v', 'error', '-y', '-t', '480', '-i', $src, '-an', '-vf', 'scale=1920:1080,format=p010le', '-g', '96', '-b:v', '3M')
    & $Paths.WorkerFfmpeg @common -c:v av1_qsv -preset faster $tmp
    if ($LASTEXITCODE) { & $Paths.WorkerFfmpeg @common -c:v libaom-av1 -cpu-used 8 -row-mt 1 $tmp }
    if ($LASTEXITCODE) { throw 'génération de la source AV1 en échec' }
    & $Paths.ServerFfmpeg -hide_banner -v error -y -itsoffset 0.007 -i $tmp -t 480 -i $src -map 0:v -map 1:a -c copy $dst
    if ($LASTEXITCODE) { throw 'remux de la source AV1 en échec' }
    Remove-Item $tmp -Force
    $dst
}

# Images et durée de la vidéo d'un fichier (paquets comptés ; de la première image à la fin de la dernière).
function Measure-Video([string]$file) {
    $pk = @(& $Paths.ServerFfprobe -v error -select_streams v:0 -show_entries packet=pts_time,duration_time -of csv=p=0 $file)
    $min = [double]::MaxValue; $max = 0.0; $n = 0
    foreach ($l in $pk) {
        $f = "$l".Split(',')
        if ($f.Count -lt 1 -or -not $f[0] -or $f[0] -eq 'N/A') { continue }
        $t = [double]::Parse($f[0], [cultureinfo]::InvariantCulture)
        $d = if ($f.Count -gt 1 -and $f[1] -and $f[1] -ne 'N/A') { [double]::Parse($f[1], [cultureinfo]::InvariantCulture) } else { 0 }
        $n++
        if ($t -lt $min) { $min = $t }
        if ($t + $d -gt $max) { $max = $t + $d }
    }
    [pscustomobject]@{ Frames = $n; Duration = $max - $min; First = $min }
}

# Morceaux d'une conversion (copie du dossier de travail du coordinateur) : durée listée dans concat.txt contre durée
# réelle (images comptées × durée d'une image), morceau par morceau. Table écrite dans <dossier>\chunks-measured.csv.
function Measure-Chunks([string]$dir, [double]$frame) {
    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($l in Get-Content (Join-Path $dir 'concat.txt')) {
        if ($l -match "^file '(.*)'$") { $rows.Add([pscustomobject]@{ File = Split-Path $Matches[1] -Leaf; Listed = 0.0; Frames = 0; Real = 0.0 }) }
        elseif ($l -match '^duration (.+)$') { $rows[$rows.Count - 1].Listed = [double]::Parse($Matches[1], [cultureinfo]::InvariantCulture) }
    }
    foreach ($r in $rows) {
        $v = Measure-Video (Join-Path $dir $r.File)
        $r.Frames = $v.Frames
        $r.Real = $v.Frames * $frame
    }
    $inv = [cultureinfo]::InvariantCulture
    @('file,listed,frames,real,listed_minus_real') + @($rows | ForEach-Object { [string]::Format($inv, '{0},{1:0.######},{2},{3:0.######},{4:0.######}', $_.File, $_.Listed, $_.Frames, $_.Real, ($_.Listed - $_.Real)) }) |
        Set-Content (Join-Path $dir 'chunks-measured.csv')
    $listed = ($rows | Measure-Object Listed -Sum).Sum
    $real = ($rows | Measure-Object Real -Sum).Sum
    $worst = $rows | Sort-Object { [math]::Abs($_.Listed - $_.Real) } -Descending | Select-Object -First 3
    [pscustomobject]@{
        Count = $rows.Count; Listed = $listed; Real = $real; Frames = ($rows | Measure-Object Frames -Sum).Sum
        Off = @($rows | Where-Object { [math]::Abs($_.Listed - $_.Real) -gt 0.002 }).Count
        Worst = ($worst | ForEach-Object { '{0} {1:+0.000;-0.000}s' -f $_.File, ($_.Listed - $_.Real) }) -join ', '
    }
}

# Source AV1 10 bits à start_time 0,007 (Mr. Robot S03E09, rejeté +12/+14 images sur le serveur réel) convertie par w1
# et le serveur en parallèle (comme T15) : a) w1 en logiciel (libx264), b) w1 en GPU (QSV, comme un worker NVENC). La
# sortie doit avoir exactement les images de la source, et la somme des durées listées des morceaux leur durée réelle :
# le dossier de travail du coordinateur est copié (work\out\spool-t20x) dès que la liste d'assemblage est écrite.
#   pwsh -File run-tests.ps1 -Only T20
function T20 {
    $src = Initialize-Av1Media
    $fd = 1001 / 24000
    $srcV = Measure-Video $src
    foreach ($variant in @(@{ Id = 'a'; Hw = $false; Label = 'w1 logiciel' }, @{ Id = 'b'; Hw = $true; Label = 'w1 GPU' })) {
        $name = "t20$($variant.Id)"
        if ($Bench.Workers['w1']) { Stop-Worker 'w1' }
        Set-TestWorkerConfig 'w1' @{ HardwareEncodingForConversions = $variant.Hw }
        Ensure-Workers @('w1') -Only
        $out = Join-Path $Paths.Out "conv-$name.mp4"
        $copy = Join-Path $Paths.Out "spool-$name"
        Remove-Item $copy -Recurse -Force -ErrorAction SilentlyContinue
        $spoolRoot = Join-Path $Paths.CoordDir 'encode'
        $t0 = Get-Date
        $s = Start-Shim (New-ConversionArgs -InputPath $src -OutputPath $out -InputCodec none) $name
        $copied = $false; $deadline = (Get-Date).AddSeconds(1800)
        while (-not $s.HasExited -and (Get-Date) -lt $deadline) {
            if (-not $copied) {
                $d = Get-ChildItem $spoolRoot -Directory -ErrorAction SilentlyContinue |
                    Where-Object { $_.CreationTime -gt $t0 -and (Test-Path (Join-Path $_.FullName 'concat.txt')) } | Select-Object -First 1
                if ($d) { Copy-Item $d.FullName $copy -Recurse; $copied = $true }
            }
            Start-Sleep -Milliseconds 200
        }
        $code = Wait-Exit $s 60
        $assembled = @(Log-Excerpt 'coordinator' 'assembled from chunks' 100)[-1]
        $verify = Log-Excerpt "shim-$name" 'source .*sultat|rejet' 1
        $encoders = @(Log-Excerpt (WorkerLog 'w1') 'encoder \S+,' 50 | ForEach-Object { if ($_ -match 'encoder (\S+),') { $Matches[1] } }) | Sort-Object -Unique
        $outV = if (Test-Path $out) { Measure-Video $out } else { $null }
        $m = if ($copied) { Measure-Chunks $copy $fd } else { $null }
        $chk = Check-Output $out (Probe $src).Duration 1
        $framesOk = $outV -and $outV.Frames -eq $srcV.Frames -and [math]::Abs($outV.Duration - $srcV.Duration) -le 1.5 * $fd
        $listOk = $m -and [math]::Abs($m.Listed - $srcV.Duration) -le 1.5 * $fd -and $m.Frames -eq $srcV.Frames
        $runners = Get-ConversionRunners $out
        $ok = $code -eq 0 -and $verify -match 'sultat' -and $verify -notmatch 'rejet' -and $chk.Ok -and $framesOk -and $listOk `
            -and $runners.LocalRuns -gt 0 -and $runners.Workers -contains 'w1'
        $mText = if ($m) { "morceaux : $($m.Count), $($m.Frames) images ; somme listée {0:0.000}s, réelle {1:0.000}s ({2:+0.000;-0.000}s) ; écart > 2 ms : $($m.Off) ($($m.Worst))" -f $m.Listed, $m.Real, ($m.Listed - $m.Real) } else { 'dossier de travail non copié' }
        $oText = if ($outV) { '{0} images, {1:0.000}s' -f $outV.Frames, $outV.Duration } else { 'absente' }
        Add-Result "T20$($variant.Id)" "Source AV1 10 bits (start_time 0,007) : $($variant.Label) + serveur, à l'image près" $(if ($ok) { 'PASS' } else { 'FAIL' }) (
            "encodeur w1 : $($encoders -join ', ') | tranches lancées sur le serveur : $($runners.LocalRuns), workers : $($runners.Workers -join ', ') | source : $($srcV.Frames) images, {0:0.000}s | sortie : $oText | $mText | « $assembled » | contrôle : « $verify » | code $code | $($chk.Text)" -f $srcV.Duration)
    }
    Stop-Worker 'w1'
    Set-TestWorkerConfig 'w1' @{ HardwareEncodingForConversions = $false }
    Ensure-Workers @('w1', 'w2')
}
