# Arrête tous les processus démarrés par le banc (PID notés dans work\pids.txt, vérifiés par nom et heure de démarrage).
# Ne touche jamais au worker de production (C:\emby\artifacts\worker\...).
. "$PSScriptRoot\lib.ps1"
Stop-AllMine
Get-Process EmbyRemoteWorker -ErrorAction SilentlyContinue | Select-Object Id, Path | Format-Table | Out-String | Write-Host
