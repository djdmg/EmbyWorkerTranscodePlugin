# Starts a dedicated HLS transcoding session on Emby, downloads a few segments,
# optionally seeks, then stops the session. Used to observe how Emby drives ffmpeg.
param(
    [string]$Server = 'https://emby.example.com/emby',
    [Parameter(Mandatory)][string]$ApiKey,
    [string]$ItemId = '151718',
    [string]$UserId = 'f1bb5524878545c1af35dc6f0af0e854',
    [int]$Segments = 3,
    [long]$SeekSeconds = 0,
    [int]$VideoBitrate = 1500000,
    [int]$MaxWidth = 854,
    [string]$VideoCodec = 'h264'
)
$ErrorActionPreference = 'Stop'
$h = @{ 'X-Emby-Token' = $ApiKey }
function Get-Text([string]$url) {
    $r = Invoke-WebRequest $url -Headers $h
    if ($r.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($r.Content) } else { $r.Content }
}
function Get-Uris([string]$m3u8) { $m3u8 -split "`n" | ForEach-Object Trim | Where-Object { $_ -and -not $_.StartsWith('#') } }

$dev = 'dev-transcode-probe'
$h['X-Emby-Device-Id'] = $dev
$h['X-Emby-Client'] = 'DevTranscodeProbe'
$h['X-Emby-Device-Name'] = 'DevTranscodeProbe'
$h['X-Emby-Client-Version'] = '0.1'
# Device profile without any direct play/stream option, so Emby must transcode like a real client would.
$profile = @{
    Name = 'DevTranscodeProbe'; MaxStreamingBitrate = $VideoBitrate + 128000
    DirectPlayProfiles = @()
    TranscodingProfiles = @(@{ Type = 'Video'; Container = 'ts'; Protocol = 'hls'; VideoCodec = $VideoCodec; AudioCodec = 'aac'; Context = 'Streaming'; MaxAudioChannels = '2'; BreakOnNonKeyFrames = $true })
    CodecProfiles = @(@{ Type = 'Video'; Codec = $VideoCodec; Conditions = @(@{ Condition = 'LessThanEqual'; Property = 'Width'; Value = "$MaxWidth" }) })
    SubtitleProfiles = @(@{ Format = 'vtt'; Method = 'Hls' })
}
$startTicks = $SeekSeconds * 10000000
$body = @{ DeviceProfile = $profile } | ConvertTo-Json -Depth 8
$pi = Invoke-RestMethod -Method Post "$Server/Items/$ItemId/PlaybackInfo?UserId=$UserId&MaxStreamingBitrate=$($VideoBitrate + 128000)&StartTimeTicks=$startTicks&IsPlayback=true&AutoOpenLiveStream=true" -Headers $h -Body $body -ContentType 'application/json'
$src = $pi.MediaSources[0]
$ps = $pi.PlaySessionId
if (-not $src.TranscodingUrl) { throw "Emby did not choose transcoding (DirectPlay=$($src.SupportsDirectPlay))" }
Write-Host "PlaySession $ps; reasons: $($src.TranscodeReasons -join ',')"
$master = Get-Text ("$Server" + ($src.TranscodingUrl -replace '^/emby', '') + "&api_key=$ApiKey")
$main = @(Get-Uris $master)[0]
$playlist = Get-Text "$Server/Videos/$ItemId/$main"
$segs = @(Get-Uris $playlist)
Write-Host "Playlist: $($segs.Count) segments; first: $($segs[0])"
foreach ($s in $segs | Select-Object -First $Segments) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $code = curl.exe -s -m 120 -o NUL -w "%{http_code} %{size_download}" -H "X-Emby-Token: $ApiKey" "$Server/Videos/$ItemId/$s"
    Write-Host ("{0} -> {1} in {2} ms" -f ($s -split '\?')[0], $code, $sw.ElapsedMilliseconds)
}
if ($SeekSeconds -gt 0) {
    # Requesting a far segment makes Emby restart ffmpeg at that position, like a client seek.
    $idx = [int][math]::Floor($SeekSeconds / 3)
    foreach ($s in $segs | Select-Object -Skip $idx -First $Segments) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $code = curl.exe -s -m 120 -o NUL -w "%{http_code} %{size_download}" -H "X-Emby-Token: $ApiKey" "$Server/Videos/$ItemId/$s"
        Write-Host ("seek {0} -> {1} in {2} ms" -f ($s -split '\?')[0], $code, $sw.ElapsedMilliseconds)
    }
}
Invoke-RestMethod -Method Delete "$Server/Videos/ActiveEncodings?DeviceId=$dev&PlaySessionId=$ps" -Headers $h | Out-Null
Write-Host "Stopped $ps"
