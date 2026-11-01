# Points Emby's ffmpeg at the remote-transcode dispatcher, or back at its own binary. Applied live, no restart.
#   emby-ffmpeg-path.ps1 -ApiKey <key> -Enable     use /volume4/docker/emby-remote/ffmpeg
#   emby-ffmpeg-path.ps1 -ApiKey <key> -Disable    restore Emby's bundled ffmpeg (original state)
#   emby-ffmpeg-path.ps1 -ApiKey <key>             show the current setting
param(
    [Parameter(Mandatory)][string]$ApiKey,
    [switch]$Enable,
    [switch]$Disable,
    [string]$Server = 'https://emby.example.com/emby',
    [string]$Dispatcher = '/volume4/docker/emby-remote/ffmpeg'
)
$ErrorActionPreference = 'Stop'
$h = @{ 'X-Emby-Token' = $ApiKey }
function Get-Options {
    $r = Invoke-WebRequest "$Server/Encoding/FfmpegOptions" -Headers $h
    $txt = if ($r.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($r.Content) } else { $r.Content }
    ($txt | ConvertFrom-Json).Object
}
$o = Get-Options
if ($Enable -or $Disable) {
    $o.UseCustomEncoderPath = [bool]$Enable
    if ($Enable) { $o | Add-Member -Force NoteProperty CustomEncoderPath $Dispatcher }
    Invoke-WebRequest -Method Post "$Server/Encoding/FfmpegOptions" -Headers $h -ContentType 'application/json' -Body ($o | ConvertTo-Json -Depth 8) | Out-Null
    $o = Get-Options
}
"UseCustomEncoderPath=$($o.UseCustomEncoderPath)  EncoderPath=$($o.EncoderPath)"
