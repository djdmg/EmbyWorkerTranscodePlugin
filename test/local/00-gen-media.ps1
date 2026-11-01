# Génère les médias de test (≈2–3 min) avec le ffmpeg du worker.
#   film_h264.mkv   : H.264 1080p25, 2 pistes audio (fre/eng), 1 piste de sous-titres SRT intégrée (150 s)
#   film_h264.fre.srt : sous-titres externes (même contenu)
#   film_hdr10.mkv  : HEVC 10 bits BT.2020/PQ (étiquettes HDR10, métadonnées mastering/CLL) (120 s)
#   fonts\arial.ttf : dossier de polices (équivalent du fontsdir d'Emby)
param([switch]$Force)
. "$PSScriptRoot\lib.ps1"

$m = $Paths.Media
New-Item -ItemType Directory -Force $m, "$m\fonts" | Out-Null
Copy-Item C:\Windows\Fonts\arial.ttf "$m\fonts\arial.ttf" -Force

$srt = "$m\film_h264.fre.srt"
if ($Force -or -not (Test-Path $srt) -or (Get-Item $srt).Length -eq 0) {
    $sb = [System.Text.StringBuilder]::new()
    for ($i = 0; $i -lt 30; $i++) {
        $s = [TimeSpan]::FromSeconds($i * 5 + 1).ToString('hh\:mm\:ss\,fff')
        $e = [TimeSpan]::FromSeconds($i * 5 + 4).ToString('hh\:mm\:ss\,fff')
        [void]$sb.Append("$($i + 1)`r`n$s --> $e`r`nSous-titre numero $($i + 1) - t=$($i * 5 + 1)s`r`n`r`n")
    }
    [IO.File]::WriteAllText($srt, $sb.ToString(), [Text.UTF8Encoding]::new($false))
}

$ff = $Paths.WorkerFfmpeg
if ($Force -or -not (Test-Path "$m\film_h264.mkv")) {
    $font = ($m -replace '\\', '/' -replace ':', '\:') + '/fonts/arial.ttf'
    & $ff -hide_banner -loglevel error -y `
        -f lavfi -i "testsrc2=size=1920x1080:rate=25,drawtext=fontfile='$font':text='%{pts\:hms}':fontsize=72:fontcolor=white:x=40:y=40" `
        -f lavfi -i "sine=frequency=440:sample_rate=48000" -f lavfi -i "sine=frequency=880:sample_rate=48000" -i $srt `
        -t 150 -map 0:v -map 1:a -map 2:a -map 3:s -c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p -g 50 `
        -c:a aac -b:a 128k -ac 2 -c:s srt `
        -metadata:s:a:0 language=fre -metadata:s:a:0 title=Francais -metadata:s:a:1 language=eng -metadata:s:a:1 title=English `
        -metadata:s:s:0 language=fre "$m\film_h264.mkv"
    if ($LASTEXITCODE) { throw "génération film_h264.mkv en échec" }
}
if ($Force -or -not (Test-Path "$m\film_hdr10.mkv")) {
    & $ff -hide_banner -loglevel error -y -f lavfi -i "testsrc2=size=1920x1080:rate=25,format=yuv420p10le,setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc:range=tv" `
        -f lavfi -i "sine=frequency=660:sample_rate=48000" -t 120 -map 0:v -map 1:a `
        -c:v libx265 -preset ultrafast -pix_fmt yuv420p10le `
        -x265-params "log-level=error:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,400:hdr10=1" `
        -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc -c:a aac -b:a 128k -metadata:s:a:0 language=eng "$m\film_hdr10.mkv"
    if ($LASTEXITCODE) { throw "génération film_hdr10.mkv en échec" }
}
foreach ($f in 'film_h264.mkv', 'film_hdr10.mkv') {
    $p = Probe "$m\$f"
    "{0}: {1:0.00} s, {2}" -f $f, $p.Duration, (Describe-Streams $p)
}

