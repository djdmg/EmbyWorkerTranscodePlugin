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
    # Le shim se termine juste après avoir envoyé son verdict : le coordinateur peut ne pas l'avoir encore traité.
    $e = Encode-Summary $out; $until = (Get-Date).AddSeconds(10)
    while ((Prop $e 'Status') -eq 'Assembling' -and (Get-Date) -lt $until) { Start-Sleep -Milliseconds 300; $e = Encode-Summary $out }
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
    $fc = @($c.format.PSObject.Properties | Where-Object Name -in 'format_name', 'nb_streams', 'start_time' | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ';'
    $fa = @($a.format.PSObject.Properties | Where-Object Name -in 'format_name', 'nb_streams', 'start_time' | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ';'
    $dFormat = [math]::Abs([double]::Parse($c.format.duration, $inv) - [double]::Parse($a.format.duration, $inv)) * 1000
    if ($fc -ne $fa -or $dFormat -gt 1) {
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
    # Le banc garde l'audio converti à l'avance désactivé (lib.ps1), bien qu'il soit activé par défaut : activé pour ce test seulement.
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

# Conversion HDR avec le tone mapping sur le GPU (plugin 0.7.26, worker 0.7.15) : w1 seul connecté, avec le ffmpeg
# « full » (libplacebo) et la capacité validée au démarrage. Le coordinateur fige la conversion en mode libplacebo :
# toutes les tranches vont à w1 (jamais au serveur, dont le ffmpeg n'a pas la capacité), chaque graphe envoyé contient
# libplacebo et aucun zscale, et la commande de w1 nomme le device Vulkan validé. Sortie contrôlée à l'image près.
#   pwsh -File run-tests.ps1 -Only T21
function T21 {
    Ensure-Workers @('w1') -Only
    $wlog = WorkerLog 'w1'
    $cap = Log-Excerpt $wlog 'tone mapping GPU validé|capacités reprises du cache' 2
    # 8 min sans trou de 21 ms à chaque raccord (vidéo et audio bouclés séparément) : film_hdr10_x4.mkv en a trois, que
    # toute conversion comble par 2 images de plus (12002 au lieu de 12000, avec zscale comme avec libplacebo).
    $long = Join-Path $Paths.Media 'film_hdr10_x4v.mkv'
    if (-not (Test-Path $long)) { & $Paths.ServerFfmpeg -v error -y -stream_loop 3 -an -i $HDR -stream_loop 3 -vn -i $HDR -map 0:v -map 1:a -c copy $long }
    $out = Join-Path $Paths.Out 'conv-t21.mp4'
    $s = Start-Shim (New-ConversionArgs -InputPath $long -OutputPath $out -Hdr) 't21'
    $code = Wait-Exit $s 1800
    $r = Get-ConversionRunners $out
    $mode = Prop $r.Summary 'ToneMapping'
    $jobs = @(Select-String -Path (Join-Path $Paths.Logs 'coordinator.log') -Pattern "Background job (\w+) running on (\S+) \(start segment \d+, conversion $($r.Id) " -ErrorAction SilentlyContinue |
        ForEach-Object { $_.Matches[0].Groups[1].Value })
    $lines = @($jobs | ForEach-Object { Log-Excerpt $wlog "job $_`: ffmpeg " 1 })
    $gpuLines = @($lines | Where-Object { $_ -match 'libplacebo@\S*=tonemapping=[\w.]+:peak_detect=0' -and $_ -match '-init_hw_device vulkan=vk' -and $_ -notmatch 'zscale@' })
    $device = if ($lines) { ([regex]::Match($lines[0], '-init_hw_device (\S+)')).Groups[1].Value } else { '' }
    $src = Measure-Video $long
    $dst = Measure-Video $out
    $verify = Log-Excerpt 'shim-t21' 'source .*sultat|rejet' 1
    $chk = Check-Output $out (Probe $long).Duration 1
    $ok = $code -eq 0 -and $mode -eq 'Libplacebo' -and $r.LocalRuns -eq 0 -and ($r.Workers -join ',') -eq 'w1' -and $jobs.Count -gt 1 `
        -and $gpuLines.Count -eq $jobs.Count -and $src.Frames -eq $dst.Frames -and (Prop $r.Summary 'Status') -eq 'Completed' `
        -and ($verify -join ' ') -match 'sultat' -and ($verify -join ' ') -notmatch 'rejet' -and $chk.Ok
    Add-Result 'T21' 'Conversion HDR tone mappée sur le GPU (libplacebo) par w1' $(if ($ok) { 'PASS' } else { 'FAIL' }) (
        "capacité de w1 : « $($cap -join ' / ') » | mode : $mode | runs de w1 : $($jobs.Count), dont graphe libplacebo + device Vulkan : $($gpuLines.Count) ($device) | tranches sur le serveur : $($r.LocalRuns), workers : $($r.Workers -join ', ') | images source/sortie : $($src.Frames)/$($dst.Frames) | final : $(Prop $r.Summary 'Status') | contrôle : « $($verify -join ' / ') » | code $code | $($chk.Text)")
    Ensure-Workers @('w1', 'w2')
}

# Vidéo plus courte que l'audio (Narcos: Mexico S02E08, 4K Dolby Vision : vidéo arrêtée à 3403 s, fichier de 3437 s).
# La dernière tranche s'arrête là où la vidéo s'arrête ; ses relances ne produisent rien ou un morceau d'une image déjà
# encodée (worker 0.7.15). En 0.7.25 elle était relancée sans fin, en 0.7.27 une image s'ajoutait à chaque relance.
# Attendu (0.7.28) : conversion terminée, autant d'images que la source, l'audio entier gardé, la fin de la vidéo notée
# par le coordinateur, au plus 2 relances vides.
#   pwsh -File run-tests.ps1 -Only T22
function T22 {
    Ensure-Workers @('w1') -Only
    $short = Join-Path $Paths.Media 'film_hdr10_shortvideo.mkv'
    if (-not (Test-Path $short)) {
        & $Paths.ServerFfmpeg -v error -y -stream_loop 1 -an -i $HDR -stream_loop 2 -vn -i $HDR -map 0:v -map 1:a -c copy -t 274 $short
    }
    $src = Measure-Video $short
    $full = (Probe $short).Duration
    $out = Join-Path $Paths.Out 'conv-t22.mp4'
    $t0 = Get-Date
    $s = Start-Shim (New-ConversionArgs -InputPath $short -OutputPath $out -Hdr) 't22'
    $code = Wait-Exit $s 1800
    $r = Get-ConversionRunners $out
    $ends = Log-Excerpt 'coordinator' 'the video ends at' 1
    $idle = @(Select-String -Path (Join-Path $Paths.Logs 'coordinator.log') -Pattern "conversion $($r.Id) tranche" -ErrorAction SilentlyContinue).Count
    $dst = if (Test-Path $out) { Measure-Video $out } else { $null }
    $outDur = if (Test-Path $out) { (Probe $out).Duration } else { 0 }
    $verify = Log-Excerpt 'shim-t22' 'source .*sultat|rejet' 1
    $ok = $code -eq 0 -and (Prop $r.Summary 'Status') -eq 'Completed' -and $dst -and $dst.Frames -eq $src.Frames `
        -and [math]::Abs($outDur - $full) -le 1.5 -and ($ends -join ' ') -match 'the video ends at' `
        -and ($verify -join ' ') -match 'sultat' -and ($verify -join ' ') -notmatch 'rejet' -and ((Get-Date) - $t0).TotalMinutes -lt 30
    $oText = if ($dst) { '{0} images, vidéo {1:0.000}s, fichier {2:0.0}s' -f $dst.Frames, $dst.Duration, $outDur } else { 'absente' }
    Add-Result 'T22' 'Vidéo plus courte que l''audio : dernière tranche terminée à la fin de la vidéo, sans image en double' $(if ($ok) { 'PASS' } else { 'FAIL' }) (
        "source : $($src.Frames) images, vidéo {0:0.000}s, fichier {1:0.0}s | sortie : $oText | runs lancés : $idle | « $($ends -join ' ') » | final : $(Prop $r.Summary 'Status') | contrôle : « $($verify -join ' / ') » | code $code" -f $src.Duration, $full)
    Ensure-Workers @('w1', 'w2')
}

# ---- EncodeAudioAhead : preuves de synchro (ajouté le 01/11 pour la validation de l'audio converti à l'avance) ----

function Initialize-ClassicShim {
    New-Item -ItemType Directory -Force (Split-Path $Paths.ShimClassic) | Out-Null
    Copy-Item $Paths.Shim $Paths.ShimClassic -Force
    Write-ShimConfig $Paths.ShimClassic -useAudioAhead $false
}

# Audio d'un fichier (1re piste) : du 1er paquet à la fin du dernier.
function Measure-Audio([string]$file) {
    $pk = @(& $Paths.ServerFfprobe -v error -select_streams a:0 -show_entries packet=pts_time,duration_time -of csv=p=0 $file)
    $min = [double]::MaxValue; $max = 0.0; $n = 0
    foreach ($l in $pk) {
        $f = "$l".Split(',')
        if (-not $f[0] -or $f[0] -eq 'N/A') { continue }
        $t = [double]::Parse($f[0], [cultureinfo]::InvariantCulture)
        $d = if ($f.Count -gt 1 -and $f[1] -and $f[1] -ne 'N/A') { [double]::Parse($f[1], [cultureinfo]::InvariantCulture) } else { 0 }
        $n++; if ($t -lt $min) { $min = $t }; if ($t + $d -gt $max) { $max = $t + $d }
    }
    [pscustomobject]@{ Packets = $n; Duration = $max - $min; First = $min }
}

# Conversion par le relais donné (HDR possible) ; rend la sortie, le code et ce que dit le relais.
function Invoke-AheadConversion([string]$src, [string]$name, [string]$exe, [switch]$Hdr) {
    $out = Join-Path $Paths.Out "conv-$name.mp4"
    $s = Start-Shim (New-ConversionArgs -InputPath $src -OutputPath $out -Hdr:$Hdr) $name $exe
    $code = Wait-Exit $s 1800
    [pscustomobject]@{
        Out = $out; Code = $code; Summary = Encode-Summary $out
        Assembly = Get-AssemblySeconds "shim-$name"
        Ahead = [bool](Log-Excerpt "shim-$name" 'audio converti à l''avance par le serveur' 1)
        Verify = @(Log-Excerpt "shim-$name" 'source .*sultat|rejet' 2)
    }
}

# T22 avec EncodeAudioAhead : vidéo arrêtée 34 s avant l'audio. Même conversion en assemblage classique (relais
# shim-classic) et avec l'audio converti à l'avance : mêmes images que la source, audio entier, audio identique.
#   pwsh -File run-tests.ps1 -Only T22A
function T22A {
    Restart-BenchCoordinator @{ EncodeAudioAhead = $true } 'coordinator-before-t22a'
    try { T22ACore } finally { Restart-BenchCoordinator @{} 'coordinator-t22a' }
}

function T22ACore {
    Ensure-Workers @('w1') -Only
    Initialize-ClassicShim
    $short = Join-Path $Paths.Media 'film_hdr10_shortvideo.mkv'
    if (-not (Test-Path $short)) {
        & $Paths.ServerFfmpeg -v error -y -stream_loop 1 -an -i $HDR -stream_loop 2 -vn -i $HDR -map 0:v -map 1:a -c copy -t 274 $short
    }
    $fd = 1 / 25
    $srcV = Measure-Video $short; $srcA = Measure-Audio $short
    $c = Invoke-AheadConversion $short 't22a-classic' $Paths.ShimClassic -Hdr
    $a = Invoke-AheadConversion $short 't22a-ahead' $Paths.Shim -Hdr
    $used = @(Log-Excerpt 'coordinator' 'audio converted ahead and checked' 5) | Select-Object -Last 1
    $cmp = Compare-Conversions $c.Out $a.Out
    $outV = Measure-Video $a.Out; $outA = Measure-Audio $a.Out
    $ok = $c.Code -eq 0 -and $a.Code -eq 0 -and -not $c.Ahead -and $a.Ahead -and $cmp.Ok -and (Prop $a.Summary 'Status') -eq 'Completed' `
        -and $outV.Frames -eq $srcV.Frames -and [math]::Abs($outA.Duration - $srcA.Duration) -le $fd `
        -and ($a.Verify -join ' ') -match 'sultat' -and ($a.Verify -join ' ') -notmatch 'rejet'
    Add-Result 'T22A' 'Vidéo plus courte que l''audio, avec l''audio converti à l''avance : même audio que l''assemblage classique' $(if ($ok) { 'PASS' } else { 'FAIL' }) (
        "source : $($srcV.Frames) images, vidéo {0:0.000}s, audio {1:0.000}s | sortie : $($outV.Frames) images, vidéo {2:0.000}s, audio {3:0.000}s (écart {4:+0.0;-0.0} ms) | audio à l'avance utilisé : $($a.Ahead) « $used » | assemblage classique {5} s, à l'avance {6} s | $($cmp.Text) | contrôle : « $($a.Verify -join ' / ') » | final : $(Prop $a.Summary 'Status')" -f $srcV.Duration, $srcA.Duration, $outV.Duration, $outA.Duration, (($outA.Duration - $srcA.Duration) * 1000), $c.Assembly, $a.Assembly)
    Ensure-Workers @('w1', 'w2')
}

# Source de synchro : 10 min, 23,976 i/s, un éclair blanc de 2 images et un bip de 80 ms (1 kHz) au même instant toutes
# les 360 images (≈ 15 s) à partir de l'image 80 ; vidéo décalée de 7 ms comme Mr. Robot (bip 7 ms avant l'éclair).
function Initialize-SyncMedia {
    $dst = Join-Path $Paths.Media 'film_sync.mkv'
    if (Test-Path $dst) { return $dst }
    $tmp = Join-Path $Paths.Media 'film_sync.tmp.mkv'
    $t0 = '(80*1001/24000)'; $p = '(360*1001/24000)'
    & $Paths.WorkerFfmpeg -hide_banner -v error -y `
        -f lavfi -i "color=c=black:s=1280x720:r=24000/1001:d=600,drawbox=x=0:y=0:w=iw:h=ih:color=white:t=fill:enable='gte(n,80)*lt(mod(n-80,360),2)'" `
        -f lavfi -i "aevalsrc='if(gte(t,$t0)*lt(mod(t-$t0,$p),0.08),0.5*sin(2*PI*1000*t),0)':s=48000:d=600" `
        -c:v libx264 -preset veryfast -g 96 -pix_fmt yuv420p -c:a ac3 -b:a 192k $tmp
    if ($LASTEXITCODE) { throw 'génération de la source de synchro en échec' }
    & $Paths.ServerFfmpeg -hide_banner -v error -y -itsoffset 0.007 -i $tmp -i $tmp -map 0:v -map 1:a -c copy $dst
    if ($LASTEXITCODE) { throw 'remux de la source de synchro en échec' }
    Remove-Item $tmp -Force
    $dst
}

# Instants des éclairs (début de chaque série d'images claires) et des bips (fin de chaque silence) ; écart bip - éclair
# de chaque repère, en ms. Même décalage de départ pour les deux flux (ffmpeg sans -copyts) : l'écart est celui du fichier.
function Measure-SyncMarkers([string]$file) {
    $inv = [cultureinfo]::InvariantCulture
    $flashes = @(); $t = $null; $bright = $false
    foreach ($l in @(& $Paths.ServerFfmpeg -hide_banner -i $file -map 0:v:0 -vf 'scale=64:36,signalstats,metadata=mode=print:key=lavfi.signalstats.YAVG' -f null - 2>&1)) {
        $s = "$l"
        if ($s -match 'pts_time:([-\d.e]+)') { $t = [double]::Parse($Matches[1], $inv) }
        elseif ($s -match 'YAVG=([\d.]+)') {
            $isBright = [double]::Parse($Matches[1], $inv) -gt 128
            if ($isBright -and -not $bright) { $flashes += $t }
            $bright = $isBright
        }
    }
    $beeps = @(& $Paths.ServerFfmpeg -hide_banner -i $file -map 0:a:0 -af 'silencedetect=n=-30dB:d=1' -f null - 2>&1 |
        ForEach-Object { if ("$_" -match 'silence_end: ([-\d.e]+)') { [double]::Parse($Matches[1], $inv) } })
    $offsets = @(foreach ($f in $flashes) {
        $b = $beeps | Sort-Object { [math]::Abs($_ - $f) } | Select-Object -First 1
        if ($null -ne $b -and [math]::Abs($b - $f) -lt 1) { ($b - $f) * 1000 }
    })
    [pscustomobject]@{ Flashes = $flashes.Count; Beeps = $beeps.Count; Offsets = $offsets
        Mean = if ($offsets) { ($offsets | Measure-Object -Average).Average } else { $null }
        Min = if ($offsets) { ($offsets | Measure-Object -Minimum).Minimum } else { $null }
        Max = if ($offsets) { ($offsets | Measure-Object -Maximum).Maximum } else { $null } }
}

# Lip-sync mesuré : source à repères (éclair + bip), conversion classique et avec l'audio converti à l'avance (w1, w2 et
# serveur) ; écart bip - éclair de chaque repère dans la sortie contre la source, et audio identique entre les deux.
#   pwsh -File run-tests.ps1 -Only T23
function T23 {
    Restart-BenchCoordinator @{ EncodeAudioAhead = $true } 'coordinator-before-t23'
    try { T23Core } finally { Restart-BenchCoordinator @{} 'coordinator-t23' }
}

function T23Core {
    Ensure-Workers @('w1', 'w2')
    Initialize-ClassicShim
    $src = Initialize-SyncMedia
    $fd = 1001 / 24000
    $srcV = Measure-Video $src; $srcA = Measure-Audio $src
    $c = Invoke-AheadConversion $src 't23-classic' $Paths.ShimClassic
    $a = Invoke-AheadConversion $src 't23-ahead' $Paths.Shim
    $cmp = Compare-Conversions $c.Out $a.Out
    $ms = Measure-SyncMarkers $src; $mc = Measure-SyncMarkers $c.Out; $ma = Measure-SyncMarkers $a.Out
    $drift = @(for ($i = 0; $i -lt [math]::Min($ms.Offsets.Count, $ma.Offsets.Count); $i++) { $ma.Offsets[$i] - $ms.Offsets[$i] })
    $worst = if ($drift) { ($drift | ForEach-Object { [math]::Abs($_) } | Measure-Object -Maximum).Maximum } else { $null }
    $outV = Measure-Video $a.Out; $outA = Measure-Audio $a.Out
    $ok = $c.Code -eq 0 -and $a.Code -eq 0 -and $a.Ahead -and -not $c.Ahead -and $cmp.Ok -and (Prop $a.Summary 'Status') -eq 'Completed' `
        -and $outV.Frames -eq $srcV.Frames -and [math]::Abs($outA.Duration - $srcA.Duration) -le $fd `
        -and $ms.Offsets.Count -ge 39 -and $ma.Offsets.Count -eq $ms.Offsets.Count -and $null -ne $worst -and $worst -le 10 `
        -and ($a.Verify -join ' ') -match 'sultat' -and ($a.Verify -join ' ') -notmatch 'rejet'
    $f = { param($m) if ($m.Offsets) { '{0} repères, bip-éclair moy {1:+0.0;-0.0} ms [{2:+0.0;-0.0} ; {3:+0.0;-0.0}]' -f $m.Offsets.Count, $m.Mean, $m.Min, $m.Max } else { "aucun repère ($($m.Flashes) éclairs, $($m.Beeps) bips)" } }
    Add-Result 'T23' 'Lip-sync mesuré (éclair + bip) avec l''audio converti à l''avance' $(if ($ok) { 'PASS' } else { 'FAIL' }) (
        "source : $(& $f $ms) | classique : $(& $f $mc) | à l'avance : $(& $f $ma) | écart à la source au pire {0:0.0} ms | images source/sortie $($srcV.Frames)/$($outV.Frames) | audio source {1:0.000}s, sortie {2:0.000}s | assemblage classique {3} s, à l'avance {4} s | workers : $(Prop $a.Summary 'Worker') | $($cmp.Text) | contrôle : « $($a.Verify -join ' / ') »" -f $worst, $srcA.Duration, $outA.Duration, $c.Assembly, $a.Assembly)
}

# Source HEVC 8 bits dont tout le fichier (vidéo et audio) commence à 0,005 s (issue #3 : rejets à +21 et +22 images).
# Après un saut, ffmpeg 9 place les images sur sa grille de sortie : tous les temps listés d'un run de worker sont en retard
# d'une image moins le décalage de la source (+37 ms), les images restant justes. Chaque tranche allongeait la liste
# d'assemblage d'autant. Workers seuls (le serveur masquait le défaut en prenant la moitié des tranches) :
# a) sans incident ; b) w2 tué en pleine tranche : la reprise ne doit ni perdre ni répéter d'image.
#   pwsh -File run-tests.ps1 -Only T24
function T24 {
    $dst = Join-Path $Paths.Media 'film_hevc_tsoffset5.mkv'
    if (-not (Test-Path $dst)) {
        $src = Join-Path $Paths.Media 'film_2398.mkv'
        $tmp = Join-Path $Paths.Media 'film_hevc_tsoffset5.video.mkv'
        & $Paths.WorkerFfmpeg -hide_banner -v error -y -t 480 -i $src -an -vf scale=1920:960 -c:v libx265 -preset ultrafast -x265-params log-level=none -g 96 $tmp
        if ($LASTEXITCODE) { throw 'génération de la source HEVC en échec' }
        & $Paths.ServerFfmpeg -hide_banner -v error -y -i $tmp -t 480 -i $src -map 0:v -map 1:a -c copy -output_ts_offset 0.005 $dst
        if ($LASTEXITCODE) { throw 'remux de la source HEVC en échec' }
        Remove-Item $tmp -Force
    }
    # Le ffprobe 5.1 d'Emby voit ce fichier commencer à 0 quand le ffmpeg 9 des workers le voit à 0,005 s : les bornes des
    # tranches sont calculées avec un zéro, appliquées avec l'autre. Ce faux ffprobe reproduit l'écart du vrai serveur.
    $fakeProbe = [IO.Path]::Combine($Paths.Work, 'bin', 'fake-emby-ffprobe', 'fake-emby-ffprobe.exe')
    if (-not (Test-Path $fakeProbe)) {
        & dotnet publish (Join-Path $PSScriptRoot 'fake-emby-ffprobe') -c Release -o (Split-Path $fakeProbe) --nologo -v q | Out-Null
        if ($LASTEXITCODE) { throw 'publication du faux ffprobe en échec' }
    }
    Set-Content (Join-Path (Split-Path $fakeProbe) 'fake-emby-ffprobe.txt') $Paths.ServerFfprobe -Encoding ascii
    Restart-BenchCoordinator @{ EncodeLocalAlongsideWorkers = $false; ServerFfprobe = $fakeProbe } 'coordinator-before-t24'
    try {
        foreach ($variant in @(@{ Id = 'a'; Kill = $false; Label = 'workers seuls' }, @{ Id = 'b'; Kill = $true; Label = 'w2 tué en pleine tranche' })) {
            Ensure-Workers @('w1', 'w2')
            $name = "t24$($variant.Id)"
            $srcV = Measure-Video $dst
            $out = Join-Path $Paths.Out "conv-$name.mp4"
            $s = Start-Shim (New-ConversionArgs -InputPath $dst -OutputPath $out -InputCodec hevc) $name
            $killed = ''
            if ($variant.Kill) {
                $deadline = (Get-Date).AddSeconds(600)
                while ((Get-Date) -lt $deadline -and -not $s.HasExited) {
                    $e = Encode-Summary $out
                    if ($e -and $e.Chunks -ge 3) { Stop-Worker 'w2' -Hard; $killed = "w2 tué à $($e.Chunks) morceaux"; break }
                    Start-Sleep 1
                }
            }
            $code = Wait-Exit $s 1800
            $r = Get-ConversionRunners $out
            $outV = if (Test-Path $out) { Measure-Video $out } else { $null }
            $verify = Log-Excerpt "shim-$name" 'source .*sultat|rejet' 1
            $assembled = Log-Excerpt 'coordinator' 'assembled from chunks' 100 | Select-Object -Last 1
            $resume = Log-Excerpt 'coordinator' 'resuming at the source frame' 100 | Select-Object -Last 1
            $fd = 1001 / 24000
            $ok = $code -eq 0 -and $outV -and $outV.Frames -eq $srcV.Frames -and [math]::Abs($outV.Duration - $srcV.Duration) -le 1.5 * $fd `
                -and ($verify -join ' ') -match 'sultat' -and ($verify -join ' ') -notmatch 'rejet' -and (Prop $r.Summary 'Status') -eq 'Completed' -and $r.LocalRuns -eq 0
            $oText = if ($outV) { '{0} images, {1:0.000}s' -f $outV.Frames, $outV.Duration } else { 'absente' }
            Add-Result "T24$($variant.Id)" "Source HEVC à start_time 0,005 : $($variant.Label), à l'image près" $(if ($ok) { 'PASS' } else { 'FAIL' }) (
                "source : $($srcV.Frames) images, {0:0.000}s | sortie : $oText | $killed | reprise : « $resume » | workers : $($r.Workers -join ', '), tranches serveur : $($r.LocalRuns) | « $assembled » | contrôle : « $($verify -join ' / ') » | final : $(Prop $r.Summary 'Status') | code $code" -f $srcV.Duration)
        }
    } finally { Restart-BenchCoordinator @{} 'coordinator-t24' }
    Ensure-Workers @('w1', 'w2')
}

# Rapport d'échec complet, sur un vrai rejet : l'ancienne marge de 1 ms (EncodeSeekMarginSeconds) et le faux ffprobe d'Emby
# reproduisent l'issue #3. Le coordinateur écrit le rapport dans un fichier (IssueReportFile). Attendu : le verdict trouve
# le désaccord sur le début du fichier et les images décalées, le rapport contient la recette et le tableau des tranches,
# et rien de personnel (ni chemin, ni nom de fichier, ni titre).
#   pwsh -File run-tests.ps1 -Only T25
function T25 {
    $dst = Join-Path $Paths.Media 'film_hevc_tsoffset5.mkv'
    if (-not (Test-Path $dst)) { throw 'source absente : lancer T24 d''abord' }
    $fakeProbe = [IO.Path]::Combine($Paths.Work, 'bin', 'fake-emby-ffprobe', 'fake-emby-ffprobe.exe')
    Set-Content (Join-Path (Split-Path $fakeProbe) 'fake-emby-ffprobe.txt') $Paths.ServerFfprobe -Encoding ascii
    $report = Join-Path $Paths.Out 'report-t25.md'
    Remove-Item $report -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $Paths.CoordDir 'data\issue-reports.json') -Force -ErrorAction SilentlyContinue
    Restart-BenchCoordinator @{ EncodeLocalAlongsideWorkers = $false; ServerFfprobe = $fakeProbe; EncodeSeekMarginSeconds = 0.001; IssueReportFile = (U $report) } 'coordinator-before-t25'
    try {
        Ensure-Workers @('w1', 'w2')
        $out = Join-Path $Paths.Out 'conv-t25.mp4'
        $s = Start-Shim (New-ConversionArgs -InputPath $dst -OutputPath $out -InputCodec hevc) 't25'
        $deadline = (Get-Date).AddSeconds(900)
        while ((Get-Date) -lt $deadline -and -not (Test-Path $report)) { Start-Sleep 2 }
        $rejected = Log-Excerpt 'shim-t25' 'rejet' 1
        $body = if (Test-Path $report) { Get-Content $report -Raw } else { '' }
        $expected = @('**Verdict:**', 'file-start-disagreement', 'shifted-frames', 'Reproduce without the original file', 'output_ts_offset', 'Slices: listed against measured', 'Source timing fingerprint')
        $missing = @($expected | Where-Object { $body -notmatch [regex]::Escape($_) })
        $leaks = @('film_hevc', 'tsoffset5', $env:USERNAME, 'emby/test', 'emby\test', 'localhost', '127.0.0.1') | Where-Object { $body.Contains($_) }
        $verdict = ($body -split "`n" | Where-Object { $_ -like '*Verdict:*' } | Select-Object -First 1)
        $ok = $body.Length -gt 0 -and $missing.Count -eq 0 -and @($leaks).Count -eq 0 -and "$rejected" -match 'rejet'
        Add-Result 'T25' 'Rapport d''échec complet sur un vrai rejet : verdict, recette, tableau des tranches, rien de personnel' $(if ($ok) { 'PASS' } else { 'FAIL' }) (
            "rejet : « $rejected » | taille du rapport : $($body.Length) | manque : $($missing -join ', ') | fuites : $(@($leaks) -join ', ') | $verdict | rapport : $report")
        Stop-Mine $s.P.Id -Tree
    } finally { Restart-BenchCoordinator @{} 'coordinator-t25' }
    Ensure-Workers @('w1', 'w2')
}

# Source MP4 dont la vidéo commence à 0,124 s et l'audio à 0,062 s (issue #4, source générée avec la recette du rapport) :
# l'encodeur AAC avance l'audio de 1024 échantillons, d'où -0,040667 ; le -r d'Emby cale les temps listés sur sa grille, 20,3 ms sous les bornes des tranches, un peu plus que l'ancienne tolérance
# fixe de 20 ms. Attendu : chaque tranche dure sa longueur dans la liste d'assemblage, résultat à l'image près.
#   pwsh -File run-tests.ps1 -Only T26
function T26 {
    $src = Join-Path $Paths.Media 'film_h264_start124.mp4'
    if (-not (Test-Path $src)) {
        & $Paths.ServerFfmpeg -y -hide_banner -v error -t 420 -f lavfi -i 'testsrc2=size=1280x720:rate=919025/38331' -itsoffset -0.040667 -t 420 -f lavfi -i 'anullsrc=r=48000:cl=stereo' -map 0:v -map 1:a -c:v libx264 -preset ultrafast -g 144 -keyint_min 144 -sc_threshold 0 -bf 2 -pix_fmt yuv420p -profile:v high -c:a:0 aac -output_ts_offset 0.124 $src
        if ($LASTEXITCODE) { throw 'génération de la source en échec' }
    }
    Ensure-Workers @('w1', 'w2')
    $srcV = Measure-Video $src
    $out = Join-Path $Paths.Out 'conv-t26.mp4'
    $s = Start-Shim (New-ConversionArgs -InputPath $src -OutputPath $out -InputFormat 'mov,mp4,m4a,3gp,3g2,mj2' -Rate '23.976024627685547') 't26'
    $code = Wait-Exit $s 1800
    $outV = if (Test-Path $out) { Measure-Video $out } else { $null }
    $verify = Log-Excerpt 'shim-t26' 'source .*sultat|rejet' 1
    $assembled = Log-Excerpt 'coordinator' 'assembled from chunks' 100 | Select-Object -Last 1
    $e = Encode-Summary $out
    $ok = $code -eq 0 -and $outV -and $outV.Frames -eq $srcV.Frames -and ($verify -join ' ') -notmatch 'rejet' -and (Prop $e 'Status') -eq 'Completed'
    $oText = if ($outV) { '{0} images, {1:0.000}s' -f $outV.Frames, $outV.Duration } else { 'absente' }
    Add-Result 'T26' 'Source MP4 à start_time 0,062/0,124 avec le -r d''Emby : à l''image près' $(if ($ok) { 'PASS' } else { 'FAIL' }) (
        "source : $($srcV.Frames) images, {0:0.000}s | sortie : $oText | contrôle : « $verify » | final : $(Prop $e 'Status'), workers $(Prop $e 'Worker') | « $assembled » | code $code" -f $srcV.Duration)
}
# Source à 25 i/s dont le début est lu 21 ms plus tard par le ffmpeg des workers que par celui d'Emby : plus d'une demi-image,
# donc la marge de seek (20 ms) ne suffit pas. Le faux ffprobe reproduit l'écart du vrai serveur. T27a, sans correction de -ss
# (marge imposée de 20 ms, qui coupe aussi l'envoi du début lu par le serveur) : chaque tranche qui se cale commence une image
# trop tard, le défaut constaté le 02/11. Le contrôle avant assemblage (SliceShiftCheck) le voit dans les temps encodés : rien
# n'est assemblé, le résultat est rejeté et la commande d'Emby refait la conversion, à l'image près. T27b, avec correction :
# à l'image près sans rejet.
#   pwsh -File run-tests.ps1 -Only T27
function T27 {
    $dst = Join-Path $Paths.Media 'film_25_start21.mkv'
    if (-not (Test-Path $dst)) {
        $tmp = Join-Path $Paths.Media 'film_25_start21.tmp.mkv'
        & $Paths.WorkerFfmpeg -y -hide_banner -v error -t 300 -f lavfi -i 'testsrc2=size=1280x720:rate=25' -t 300 -f lavfi -i 'anullsrc=r=48000:cl=stereo' -map 0:v -map 1:a -c:v libx264 -preset ultrafast -g 100 -keyint_min 100 -pix_fmt yuv420p -c:a aac $tmp
        if ($LASTEXITCODE) { throw 'génération de la source en échec' }
        & $Paths.ServerFfmpeg -hide_banner -v error -y -i $tmp -map 0 -c copy -output_ts_offset 0.021 $dst
        if ($LASTEXITCODE) { throw 'remux de la source en échec' }
        Remove-Item $tmp -Force
    }
    $fakeProbe = [IO.Path]::Combine($Paths.Work, 'bin', 'fake-emby-ffprobe', 'fake-emby-ffprobe.exe')
    if (-not (Test-Path $fakeProbe)) {
        & dotnet publish (Join-Path $PSScriptRoot 'fake-emby-ffprobe') -c Release -o (Split-Path $fakeProbe) --nologo -v q | Out-Null
        if ($LASTEXITCODE) { throw 'publication du faux ffprobe en échec' }
    }
    Set-Content (Join-Path (Split-Path $fakeProbe) 'fake-emby-ffprobe.txt') $Paths.ServerFfprobe -Encoding ascii
    $srcV = Measure-Video $dst
    try {
        foreach ($variant in @(
                @{ Id = 'a'; Options = @{ EncodeLocalAlongsideWorkers = $false; ServerFfprobe = $fakeProbe; EncodeSeekMarginSeconds = 0.02 }; Fixed = $false; Label = 'sans correction, tranches décalées rejetées, conversion refaite par Emby' },
                @{ Id = 'b'; Options = @{ EncodeLocalAlongsideWorkers = $false; ServerFfprobe = $fakeProbe }; Fixed = $true; Label = 'avec correction, à l''image près' })) {
            # Sur un vrai serveur, coordinateur et relais lisent le même ffprobe : le relais a donc aussi le faux.
            Write-ShimConfig -realFfprobe $fakeProbe
            Restart-BenchCoordinator $variant.Options "coordinator-before-t27$($variant.Id)"
            Ensure-Workers @('w1', 'w2')
            $name = "t27$($variant.Id)"
            $out = Join-Path $Paths.Out "conv-$name.mp4"
            $s = Start-Shim (New-ConversionArgs -InputPath $dst -OutputPath $out -Rate '25') $name
            $code = Wait-Exit $s 1800
            $outV = if (Test-Path $out) { Measure-Video $out } else { $null }
            $verify = Log-Excerpt "shim-$name" 'source .*sultat|rejet' 1
            $assembled = Log-Excerpt 'coordinator' 'assembled from chunks' 100 | Select-Object -Last 1
            $r = Get-ConversionRunners $out
            $exact = $outV -and $outV.Frames -eq $srcV.Frames
            $ok = if ($variant.Fixed) { $code -eq 0 -and $exact -and ($verify -join ' ') -notmatch 'rejet' -and $r.LocalRuns -eq 0 } else { $code -eq 0 -and $exact -and ($verify -join ' ') -match 'rejet.*images' -and (Prop (Encode-Summary $out) 'Status') -eq 'Failed' }
            $oText = if ($outV) { '{0} images, {1:0.000}s' -f $outV.Frames, $outV.Duration } else { 'absente' }
            Add-Result "T27$($variant.Id)" "Source 25 i/s, début lu à 21 ms d'écart : $($variant.Label)" $(if ($ok) { 'PASS' } else { 'FAIL' }) (
                "source : $($srcV.Frames) images, {0:0.000}s | sortie : $oText | contrôle : « $verify » | workers : $($r.Workers -join ', '), tranches serveur : $($r.LocalRuns) | « $assembled » | code $code" -f $srcV.Duration)
        }
    } finally { Write-ShimConfig; Restart-BenchCoordinator @{} 'coordinator-t27' }
    Ensure-Workers @('w1', 'w2')
}
# Preuve de concept : l'audio converti à l'avance sur plusieurs types de fichiers. Chaque source est convertie deux fois,
# assemblage classique et audio à l'avance, puis comparée paquet par paquet (Compare-Conversions : temps, tailles, MD5 du
# compressé et du décodé). Critère : aucun écart, et la vidéo garde le nombre d'images attendu.
#   pwsh -File run-tests.ps1 -Only T28
function New-T28Source([hashtable]$c) {
    $dst = Join-Path $Paths.Media "t28_$($c.Id).$($c.Ext)"
    if (Test-Path $dst) { return $dst }
    $tmp = Join-Path $Paths.Media "t28_$($c.Id).tmp.$($c.Ext)"
    $ff = @('-y', '-hide_banner', '-v', 'error', '-t', '150', '-f', 'lavfi', '-i', "testsrc2=size=1280x720:rate=$($c.Rate)")
    $maps = @('-map', '0:v')
    $n = 1
    foreach ($a in $c.Audio) {
        $ff += @('-t', '150', '-f', 'lavfi', '-i', "sine=frequency=$(300 + 100 * $n):sample_rate=$($a.Hz),aformat=channel_layouts=$($a.Layout)")
        $maps += @('-map', "${n}:a")
        $n++
    }
    $ff += $maps + $c.Video
    for ($i = 0; $i -lt $c.Audio.Count; $i++) { $ff += @("-c:a:$i", $c.Audio[$i].Codec) + @($c.Audio[$i].Options) }
    $ff += @($tmp)
    & $Paths.WorkerFfmpeg @ff
    if ($LASTEXITCODE) { throw "génération de la source $($c.Id) en échec" }
    Move-Item $tmp $dst -Force
    $dst
}

# Vidéo qui commence 0,079 s après l'audio, début de fichier à 0 : l'écart audio-vidéo de la sortie doit rester celui de la
# source. Mesuré sur le premier paquet affiché de chaque flux ; l'amorce de 1024 échantillons de l'AAC est masquée à la lecture.
#   pwsh -File run-tests.ps1 -Only T29
function First-Pts([string]$file, [string]$stream) {
    $v = @(& $Paths.ServerFfprobe -v error -select_streams $stream -read_intervals '%+#60' -show_entries packet=pts_time -of csv=p=0 $file) |
        ForEach-Object { "$_".Split(',')[0] } | Where-Object { $_ -and $_ -ne 'N/A' } | ForEach-Object { [double]::Parse($_, [cultureinfo]::InvariantCulture) } | Measure-Object -Minimum
    $v.Minimum
}

function T29 {
    $src = Join-Path $Paths.Media 'film_video_79ms.mkv'
    if (-not (Test-Path $src)) {
        $base = New-T28Source @{ Id = 'late_base'; Ext = 'mkv'; Rate = '25'; Video = @('-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', '-g', '48'); Audio = @(@{ Hz = 48000; Layout = 'stereo'; Codec = 'ac3'; Options = @('-b:a', '192k') }) }
        & $Paths.ServerFfmpeg -hide_banner -v error -y -itsoffset 0.079 -i $base -i $base -map 0:v -map 1:a -c copy $src
        if ($LASTEXITCODE) { throw 'génération de la source en échec' }
    }
    Ensure-Workers @('w1', 'w2')
    $out = Join-Path $Paths.Out 'conv-t29.mp4'
    $s = Start-Shim (New-ConversionArgs -InputPath $src -OutputPath $out -Rate '25') 't29'
    $code = Wait-Exit $s 1500
    # Le premier paquet d'un flux AC-3 est à -0,005 s pour un flux qui commence à 0 : c'est le début du flux qui compte dans la source.
    $sv = First-Pts $src 'v:0'; $sa = [double]::Parse("$(& $Paths.ServerFfprobe -v error -select_streams a:0 -show_entries stream=start_time -of csv=p=0 $src)".Trim().Split(',')[0], [cultureinfo]::InvariantCulture)
    $ov = First-Pts $out 'v:0'; $oa = (First-Pts $out 'a:0') + 1024 / 48000
    $gap = [math]::Abs(($oa - $ov) - ($sa - $sv)) * 1000
    $ok = $code -eq 0 -and $gap -le 1
    Add-Result 'T29' 'Vidéo commençant 79 ms après l''audio : écart audio-vidéo conservé' $(if ($ok) { 'PASS' } else { 'FAIL' }) (
        "source vidéo {0:0.000} audio {1:0.000} | sortie vidéo {2:0.000} audio {3:0.000} | différence {4:0.0} ms | code $code" -f $sv, $sa, $ov, $oa, $gap)
}

# Contrôle de synchro sur tout le fichier. T30a/b : assemblage vraiment désynchronisé, la durée listée d'un morceau au milieu de la
# liste corrigée de 12 ms (sous la demi-image : le contrôle précédent livrait ce fichier) puis 200 ms, tout ce qui suit est décalé.
# Le contrôle rejette, la commande d'Emby refait la conversion, et le fichier livré a toutes ses images et son écart audio-vidéo.
# T30c : AVI MPEG-4 à B-frames compactées et MP3 ; T30d : MPEG-TS de 300 s (une seule tranche, sans seek). Conversions propres, pas de
# rejet, et la vidéo commence où le décodeur donne la première image.
#   pwsh -File run-tests.ps1 -Only T30
function First-Decoded-Frame([string]$file) {
    $v = @(& $Paths.ServerFfprobe -v error -select_streams v:0 -read_intervals '%+#32' -show_entries frame=best_effort_timestamp_time -of csv=p=0 $file) |
        ForEach-Object { "$_".Split(',')[0] } | Where-Object { $_ -and $_ -ne 'N/A' } | ForEach-Object { [double]::Parse($_, [cultureinfo]::InvariantCulture) } | Measure-Object -Minimum
    $start = [double]::Parse("$(& $Paths.ServerFfprobe -v error -show_entries format=start_time -of csv=p=0 $file)".Trim(), [cultureinfo]::InvariantCulture)
    $v.Minimum - $start
}

function New-T30Source([string]$name, [string[]]$video, [string]$audioCodec, [int]$seconds) {
    $dst = Join-Path $Paths.Media $name
    if (-not (Test-Path $dst)) {
        & $Paths.WorkerFfmpeg -y -hide_banner -v error -t $seconds -f lavfi -i 'testsrc2=size=640x360:rate=25' -t $seconds -f lavfi -i 'sine=frequency=440:sample_rate=48000' -map 0:v -map 1:a @video -pix_fmt yuv420p -c:a $audioCodec $dst
        if ($LASTEXITCODE) { throw "génération de $name en échec" }
    }
    $dst
}

function T30 {
    $x264 = @('-c:v', 'libx264', '-preset', 'ultrafast', '-g', '48')
    $film = New-T30Source 't30_film.mkv' $x264 'ac3' 200
    $srcV = Measure-Video $film
    try {
        foreach ($variant in @(@{ Id = 'a'; Shift = 0.012; Label = '12 ms de trop dans un morceau' }, @{ Id = 'b'; Shift = 0.2; Label = '200 ms de trop dans un morceau' })) {
            Restart-BenchCoordinator @{ EncodeTestListedDurationShift = $variant.Shift } "coordinator-before-t30$($variant.Id)"
            Ensure-Workers @('w1', 'w2')
            $name = "t30$($variant.Id)"
            $out = Join-Path $Paths.Out "conv-$name.mp4"
            $s = Start-Shim (New-ConversionArgs -InputPath $film -OutputPath $out -Rate '25') $name
            $code = Wait-Exit $s 1800
            $outV = if (Test-Path $out) { Measure-Video $out } else { $null }
            $verify = Log-Excerpt "shim-$name" 'rejet' 2
            $sa = [double]::Parse("$(& $Paths.ServerFfprobe -v error -select_streams a:0 -show_entries stream=start_time -of csv=p=0 $film)".Trim().Split(',')[0], [cultureinfo]::InvariantCulture)
            $gap = if ($outV) { [math]::Abs(((First-Pts $out 'a:0') + 1024 / 48000 - (First-Pts $out 'v:0')) - ($sa - (First-Pts $film 'v:0'))) * 1000 } else { -1 }
            $ok = $code -eq 0 -and ($verify -join ' ') -match 'rejet' -and (Prop (Encode-Summary $out) 'Status') -eq 'Failed' -and (Prop (Encode-Summary $out) 'EmbyResult') -and -not (Prop (Encode-Summary $out) 'EmbyResultProblem') -and $outV -and $outV.Frames -eq $srcV.Frames -and $gap -ge 0 -and $gap -le 1
            Add-Result "T30$($variant.Id)" "Assemblage désynchronisé ($($variant.Label)) : rejeté, fichier livré correct" $(if ($ok) { 'PASS' } else { 'FAIL' }) (
                "source $($srcV.Frames) images | sortie $($outV.Frames) images, écart audio-vidéo {0:0.0} ms | contrôle : « $verify » | final : $(Prop (Encode-Summary $out) 'Status') | fichier d'Emby : $(Prop (Encode-Summary $out) 'EmbyResult') / $(Prop (Encode-Summary $out) 'EmbyResultProblem') | code $code" -f $gap)
        }
    } finally { Restart-BenchCoordinator @{} 'coordinator-t30' }
    Ensure-Workers @('w1', 'w2')
    $avi = New-T30Source 't30_xvid_mp3.avi' @('-c:v', 'libxvid', '-bf', '2', '-q:v', '5', '-g', '48') 'libmp3lame' 200
    $ts = New-T30Source 't30_film.ts' $x264 'ac3' 300
    foreach ($c in @(@{ Id = 'c'; Src = $avi; Format = 'avi'; Label = 'AVI MPEG-4 à B-frames compactées, MP3' }, @{ Id = 'd'; Src = $ts; Format = 'mpegts'; Label = 'MPEG-TS H.264, AC-3, 300 s' })) {
        $name = "t30$($c.Id)"
        $out = Join-Path $Paths.Out "conv-$name.mp4"
        $s = Start-Shim (New-ConversionArgs -InputPath $c.Src -OutputPath $out -InputFormat $c.Format -InputCodec 'none' -Rate '25') $name
        $code = Wait-Exit $s 1800
        $verify = Log-Excerpt "shim-$name" 'source .*sultat|rejet|invérifiable' 3
        $first = First-Decoded-Frame $c.Src
        $ov = if (Test-Path $out) { First-Pts $out 'v:0' } else { -1 }
        $r = Get-ConversionRunners $out
        $ok = $code -eq 0 -and ($verify -join ' ') -notmatch 'rejet|invérifiable' -and ($verify -join ' ') -match 'source' -and (Prop (Encode-Summary $out) 'Status') -ne 'Failed' -and [math]::Abs($ov - $first) -le 0.003
        Add-Result "T30$($c.Id)" "$($c.Label) : conversion propre, pas de rejet" $(if ($ok) { 'PASS' } else { 'FAIL' }) (
            "première image source {0:0.000} s, sortie {1:0.000} s | contrôle : « $verify » | workers : $($r.Workers -join ', '), tranches serveur : $($r.LocalRuns) | final : $(Prop (Encode-Summary $out) 'Status') | code $code" -f $first, $ov)
    }
}

# Source à cadence variable avec le -r d'Emby (issue #6). Images de 10 à 160 ms (vérifié à ffprobe avant le test), chacune portant son
# numéro sous forme de 16 cases noires ou blanches : on peut dire quelle image de la source montre chaque image de la sortie.
# T31a : sans le filtre fps, le contrôle rejette ; T31b : le rythme de l'issue ; T31c : images irrégulières au hasard ; T31d : un worker arrêté en cours ;
# T31f : dernière image de 100 ms, avant sa place sur la grille (le filtre de ffmpeg 9.0.2 la perdait : 2 images de moins sur un vrai fichier).
#   pwsh -File run-tests.ps1 -Only T31
function New-T31Source([string]$name, [string]$kind, [int]$frames = 10500, [string[]]$extra = @()) {
    $dst = Join-Path $Paths.Media $name
    if (Test-Path $dst) { return $dst }
    $boxes = (0..15 | ForEach-Object { "drawbox=x=$($_ * 40):y=0:w=40:h=40:c=white:t=fill:enable='mod(floor(n/$([math]::Pow(2, $_))),2)'" }) -join ','
    $expr = if ($kind -eq 'random') {
        "if(eq(N,0),st(0,0),st(0,ld(0)+st(1,random(2))*0+if(lt(ld(1),0.10),3,if(lt(ld(1),0.14),4,if(lt(ld(1),0.56),6,if(lt(ld(1),0.86),12,if(lt(ld(1),0.965),24,48)))))))"
    } else {
        "if(eq(N,0),st(0,0),st(0,ld(0)+if(lt(st(1,random(2)),0.003),3,if(lt(ld(1),0.006),48,if(lt(mod(N,6),2),24,6)))))"
    }
    & $Paths.WorkerFfmpeg -y -hide_banner -v error -f lavfi -i 'testsrc2=size=640x360:rate=25' -f lavfi -i 'sine=frequency=440:sample_rate=48000' `
        -filter_complex "[0:v]drawbox=x=0:y=0:w=640:h=40:c=black:t=fill,$boxes,settb=1/300,setpts='$expr'[v]" -map '[v]' -map 1:a -frames:v $frames -t 420 `
        -fps_mode passthrough -enc_time_base 1/90000 -c:v libx264 -preset ultrafast -g 50 -keyint_min 50 -sc_threshold 0 -bf 2 -pix_fmt yuv420p -profile:v high -crf 20 -threads 4 `
        -c:a aac -video_track_timescale 90000 @extra $dst
    if ($LASTEXITCODE) { throw "génération de $name en échec" }
    $dst
}

# Les écarts entre images d'une source, pour vérifier qu'elle est bien à cadence variable.
function Get-T31Gaps([string]$file) {
    $t = @(& $Paths.ServerFfprobe -v error -select_streams v:0 -show_entries packet=pts_time -of csv=p=0 $file) | ForEach-Object { "$_".Split(',')[0] } | Where-Object { $_ -and $_ -ne 'N/A' } |
        ForEach-Object { [double]::Parse($_, [cultureinfo]::InvariantCulture) } | Sort-Object
    $m = 0..($t.Count - 2) | ForEach-Object { $t[$_ + 1] - $t[$_] } | Measure-Object -Minimum -Maximum
    [pscustomobject]@{ Min = $m.Minimum; Max = $m.Maximum; Last = $t[-1] }
}

# Le rythme de l'issue, sa dernière image à au moins une demi-image avant sa place sur la grille, et durant 100 ms.
function New-T31TailSource {
    $dst = Join-Path $Paths.Media 't31_tail.mp4'
    if (Test-Path $dst) { return $dst }
    foreach ($n in @(10500, 10499)) {
        $tmp = New-T31Source "t31_tail_$n.mp4" 'pattern' $n
        $last = (Get-T31Gaps $tmp).Last
        if ((($last / 0.04) % 1) -ge 0.49) { break }   # sa place sur la grille (arrondie au-dessus) est après elle
    }
    # Même génération (déterministe), la durée de la dernière image changée à l'encodage : une recopie décalait toute la vidéo de 40 ms.
    $ticks = [math]::Round($last * 90000)
    $null = New-T31Source 't31_tail.mp4' 'pattern' $n @('-bsf:v', "setts=duration='if(gte(PTS,$($ticks - 10)),9000,DURATION)'")
    $d = @(& $Paths.ServerFfprobe -v error -select_streams v:0 -show_entries packet=pts_time,duration_time -of csv=p=0 $dst) | Sort-Object { [double]::Parse("$_".Split(',')[0], [cultureinfo]::InvariantCulture) } | Select-Object -Last 1
    if ("$d" -notmatch ',0\.1(0*)?$') { throw "t31_tail.mp4 : dernière image $d, pas 100 ms" }
    $dst
}

function T31 { T31a; T31bcd }

function T31a {
    $pattern = New-T31Source 't31_pattern.mp4' 'pattern'
    $random = New-T31Source 't31_random.mp4' 'random'
    foreach ($src in @($pattern, $random)) {
        $g = Get-T31Gaps $src
        if ($g.Min -gt 0.011 -or $g.Max -lt 0.159) { throw "$src n'a pas des images de 10 à 160 ms : $($g.Min) à $($g.Max)" }
    }
    $mp4 = 'mov,mp4,m4a,3gp,3g2,mj2'
    Restart-BenchCoordinator @{ EncodeFrameRateFilter = $false } 'coordinator-before-t31a'
    try {
        Ensure-Workers @('w1', 'w2')
        $out = Join-Path $Paths.Out 'conv-t31a.mp4'
        $s = Start-Shim (New-ConversionArgs -InputPath $pattern -OutputPath $out -InputFormat $mp4 -Rate '25' -Width 640 -Height 360) 't31a'
        $code = Wait-Exit $s 1800
        $verify = Log-Excerpt 'shim-t31a' 'rejet' 2
        Add-Result 'T31a' 'Cadence variable sans le filtre fps : rejeté, comme dans l''issue #6' $(if ($code -eq 0 -and ($verify -join ' ') -match 'rejet') { 'PASS' } else { 'FAIL' }) "contrôle : « $verify » | final : $(Prop (Encode-Summary $out) 'Status') | code $code"
    } finally { Restart-BenchCoordinator @{} 'coordinator-t31a' }
}

function T31bcd {
    $pattern = New-T31Source 't31_pattern.mp4' 'pattern'
    $random = New-T31Source 't31_random.mp4' 'random'
    # La vidéo seule retardée de 30 ms : l'audio commence toujours à 0.
    $tail = New-T31TailSource
    $offGrid = Join-Path $Paths.Media 't31_offgrid.mp4'
    if (-not (Test-Path $offGrid)) { & $Paths.WorkerFfmpeg -y -hide_banner -v error -itsoffset 0.03 -i $pattern -i $pattern -map 0:v -map 1:a -c copy -video_track_timescale 90000 $offGrid }
    $mp4 = 'mov,mp4,m4a,3gp,3g2,mj2'
    foreach ($c in @(@{ Id = 'b'; Src = $pattern; Label = 'rythme de l''issue' }, @{ Id = 'c'; Src = $random; Label = 'irrégulière au hasard' }, @{ Id = 'd'; Src = $pattern; Label = 'worker arrêté en cours'; Stop = $true }, @{ Id = 'e'; Src = $offGrid; Label = 'première image hors de la grille (30 ms) : sans le filtre, le contrôle tranche'; OffGrid = $true }, @{ Id = 'f'; Src = $tail; Label = 'dernière image de 100 ms hors de la grille' })) {
        Ensure-Workers @('w1', 'w2')
        $name = "t31$($c.Id)"
        $out = Join-Path $Paths.Out "conv-$name.mp4"
        $s = Start-Shim (New-ConversionArgs -InputPath $c.Src -OutputPath $out -InputFormat $mp4 -Rate '25' -Width 640 -Height 360) $name
        $stopped = $false
        if ($c['Stop']) {
            $until = (Get-Date).AddSeconds(120)
            while (-not $s.HasExited -and (Get-Date) -lt $until) {
                $e = Encode-Summary $out
                if ($e -and @($e.runs | Where-Object worker -eq 'w1').Count) { Start-Sleep -Seconds 4; Stop-Worker 'w1'; $stopped = $true; break }
                Start-Sleep -Milliseconds 300
            }
        }
        $code = Wait-Exit $s 1800
        $verify = Log-Excerpt "shim-$name" 'source .*sultat|rejet|invérifiable' 3
        $filtered = @(Log-Excerpt 'coordinator' 'irregular source frames' 20).Count
        $gaps = Get-T31Gaps $c.Src
        $v = if (Test-Path $out) { Measure-Video $out } else { $null }
        $e = Encode-Summary $out; $wait = (Get-Date).AddSeconds(10)
        while ((Prop $e 'Status') -eq 'Assembling' -and (Get-Date) -lt $wait) { Start-Sleep -Milliseconds 300; $e = Encode-Summary $out }
        $expected = [math]::Floor(($gaps.Last + 0.04) / 0.04 + 0.5 + 1e-6)
        # Hors de la grille, les deux issues sont justes tant que le contrôle tranche : on regarde le fichier à part.
        $ok = $code -eq 0 -and $filtered -ge 1 -and ($c['OffGrid'] -or ($verify -join ' ') -notmatch 'rejet|invérifiable') -and ($c['OffGrid'] -or (($verify -join ' ') -match 'source' -and (Prop $e 'Status') -eq 'Completed' -and $v -and [math]::Abs($v.Frames - $expected) -le 1)) -and (-not $c['Stop'] -or $stopped)
        $r = Get-ConversionRunners $out
        Add-Result "T31$($c.Id)" "Cadence variable, $($c.Label) : conversion par tranches, pas de rejet" $(if ($ok) { 'PASS' } else { 'FAIL' }) (
            "images $($v.Frames), attendues $expected, longueur {0:0.000} s | contrôle : « $verify » | workers : $($r.Workers -join ', '), tranches serveur : $($r.LocalRuns), w1 arrêté : $stopped | final : $(Prop $e 'Status') | code $code" -f $v.Duration)
    }
}

function T28 {
    Restart-BenchCoordinator @{ EncodeAudioAhead = $true } 'coordinator-before-t28'
    try { T28Core } finally { Restart-BenchCoordinator @{} 'coordinator-t28' }
}

function T28Core {
    Ensure-Workers @('w1', 'w2')
    New-Item -ItemType Directory -Force (Split-Path $Paths.ShimClassic) | Out-Null
    Copy-Item $Paths.Shim $Paths.ShimClassic -Force
    Write-ShimConfig $Paths.ShimClassic -useAudioAhead $false
    $x264 = @('-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', '-g', '48')
    $x265 = @('-c:v', 'libx265', '-preset', 'ultrafast', '-x265-params', 'log-level=none', '-pix_fmt', 'yuv420p10le', '-g', '48')
    $cases = @(
        @{ Id = 'ac3_51_25'; Label = 'MKV H.264 25 i/s, AC-3 5.1 48 kHz'; Ext = 'mkv'; Rate = '25'; Video = $x264; Audio = @(@{ Hz = 48000; Layout = '5.1'; Codec = 'ac3'; Options = @('-b:a', '448k') }); Tracks = @(1) },
        @{ Id = 'eac3_st_2398'; Label = 'MKV H.264 23,976 i/s, E-AC-3 stéréo 48 kHz'; Ext = 'mkv'; Rate = '24000/1001'; Video = $x264; Audio = @(@{ Hz = 48000; Layout = 'stereo'; Codec = 'eac3'; Options = @('-b:a', '192k') }); Tracks = @(1) },
        @{ Id = 'dts_51_2997'; Label = 'MKV H.264 29,97 i/s, DTS 5.1 48 kHz'; Ext = 'mkv'; Rate = '30000/1001'; Video = $x264; Audio = @(@{ Hz = 48000; Layout = '5.1'; Codec = 'dca'; Options = @('-strict', '-2', '-b:a', '768k') }); Tracks = @(1) },
        @{ Id = 'flac_441_24'; Label = 'MKV H.264 24 i/s, FLAC stéréo 44,1 kHz'; Ext = 'mkv'; Rate = '24'; Video = $x264; Audio = @(@{ Hz = 44100; Layout = 'stereo'; Codec = 'flac'; Options = @() }); Tracks = @(1) },
        @{ Id = 'opus_st_25'; Label = 'MKV H.264 25 i/s, Opus stéréo 48 kHz'; Ext = 'mkv'; Rate = '25'; Video = $x264; Audio = @(@{ Hz = 48000; Layout = 'stereo'; Codec = 'libopus'; Options = @('-b:a', '128k') }); Tracks = @(1) },
        @{ Id = 'mp3_441_mp4'; Label = 'MP4 H.264 25 i/s, MP3 stéréo 44,1 kHz'; Ext = 'mp4'; Rate = '25'; Video = $x264; Audio = @(@{ Hz = 44100; Layout = 'stereo'; Codec = 'libmp3lame'; Options = @('-b:a', '192k') }); Tracks = @(1); Format = 'mov,mp4,m4a,3gp,3g2,mj2' },
        @{ Id = 'two_tracks'; Label = 'MKV H.264 25 i/s, 2 pistes AC-3 48 kHz + FLAC 44,1 kHz'; Ext = 'mkv'; Rate = '25'; Video = $x264; Audio = @(@{ Hz = 48000; Layout = '5.1'; Codec = 'ac3'; Options = @('-b:a', '384k') }, @{ Hz = 44100; Layout = 'stereo'; Codec = 'flac'; Options = @() }); Tracks = @(1, 2) },
        @{ Id = 'hevc10_eac3'; Label = 'MKV HEVC 10 bits 25 i/s, E-AC-3 5.1 48 kHz'; Ext = 'mkv'; Rate = '25'; Video = $x265; Audio = @(@{ Hz = 48000; Layout = '5.1'; Codec = 'eac3'; Options = @('-b:a', '384k') }); Tracks = @(1); Codec = 'hevc' },
        @{ Id = 'late_audio'; Label = 'MKV H.264 25 i/s, audio qui commence 0,5 s après la vidéo'; Ext = 'mkv'; Rate = '25'; Video = $x264; Audio = @(@{ Hz = 48000; Layout = 'stereo'; Codec = 'ac3'; Options = @('-b:a', '192k') }); Tracks = @(1) },
        @{ Id = 'mpegts'; Label = 'MPEG-TS H.264 25 i/s, AC-3 stéréo, début de fichier à 1,4 s'; Ext = 'ts'; Rate = '25'; Video = $x264; Audio = @(@{ Hz = 48000; Layout = 'stereo'; Codec = 'ac3'; Options = @('-b:a', '192k') }); Tracks = @(1); Format = 'mpegts'; ClassicOnly = $true }
    )
    foreach ($c in $cases) {
        if ($c.Id -eq 'late_audio') {
            # Deux entrées pour décaler l'audio seul : la vidéo telle quelle, l'audio repoussé de 0,5 s.
            $dst = Join-Path $Paths.Media 't28_late_audio.mkv'
            if (-not (Test-Path $dst)) {
                $base = New-T28Source @{ Id = 'late_base'; Ext = 'mkv'; Rate = '25'; Video = $x264; Audio = $c.Audio }
                & $Paths.ServerFfmpeg -hide_banner -v error -y -i $base -itsoffset 0.5 -i $base -map 0:v -map 1:a -c copy $dst
                if ($LASTEXITCODE) { throw 'remux late_audio en échec' }
            }
            $src = $dst
        } else { $src = New-T28Source $c }
        $format = if ($c.ContainsKey('Format')) { $c.Format } else { 'matroska,webm' }
        $codec = if ($c.ContainsKey('Codec')) { $c.Codec } else { 'h264' }
        $res = @{}
        foreach ($k in @('classic', 'ahead')) {
            $name = "t28-$($c.Id)-$k"
            $out = Join-Path $Paths.Out "conv-$name.mp4"
            $exe = if ($k -eq 'classic') { $Paths.ShimClassic } else { $Paths.Shim }
            $s = Start-Shim (New-ConversionArgs -InputPath $src -OutputPath $out -AudioStreams $c.Tracks -InputFormat $format -InputCodec $codec -Rate $c.Rate) $name $exe
            $code = Wait-Exit $s 1500
            $res[$k] = [pscustomobject]@{
                Out = $out; Code = $code; Summary = Encode-Summary $out
                Video = if (Test-Path $out) { Measure-Video $out } else { $null }
                Ahead = [bool](Log-Excerpt "shim-$name" 'audio converti à l''avance par le serveur' 1)
                Verify = Log-Excerpt "shim-$name" 'source .*sultat|rejet' 1
            }
        }
        $cl = $res['classic']; $ah = $res['ahead']
        $cmp = if ($cl.Video -and $ah.Video) { Compare-Conversions $cl.Out $ah.Out } else { @{ Ok = $false; Text = 'sortie absente' } }
        $srcV = Measure-Video $src
        $usedAsExpected = $ah.Ahead -ne $c.ContainsKey('ClassicOnly')
        $ok = $cl.Code -eq 0 -and $ah.Code -eq 0 -and $usedAsExpected -and -not $cl.Ahead -and $cmp.Ok `
            -and $cl.Video.Frames -eq $ah.Video.Frames -and ($ah.Verify -join ' ') -notmatch 'rejet'
        Add-Result "T28-$($c.Id)" $c.Label $(if ($ok) { 'PASS' } else { 'FAIL' }) (
            "source $($srcV.Frames) images ; classique $($cl.Video.Frames), à l'avance $($ah.Video.Frames) ; utilisé : $($ah.Ahead) ; audio : « $(Prop $ah.Summary 'Audio') » ; contrôle : « $($ah.Verify -join ' / ') » ; codes $($cl.Code)/$($ah.Code) | $($cmp.Text)")
    }
}
