# Vérification rapide du banc : coordinateur + w1, une lecture distante de quelques segments puis 'q'.
. "$PSScriptRoot\lib.ps1"
try {
    Initialize-Bench @('w1') -Start
    Write-Host ((Get-Status).workers | Format-Table Name, State, Reason, Encoders | Out-String)
    $args_ = New-PlaybackArgs -InputPath "$($Paths.Media)\film_h264.mkv" -Session smoke
    $s = Start-Shim $args_ 'smoke'
    Wait-Segments 'shim-smoke' 3 120 | Format-Table | Out-String | Write-Host
    $s.Send('q')
    "exit: $($s.Wait(20000)) code=$(if($s.HasExited){$s.ExitCode})"
} finally {
    Stop-Bench
}
