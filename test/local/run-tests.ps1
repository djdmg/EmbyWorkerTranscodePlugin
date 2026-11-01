# Lance les tests d'acceptation locaux. Exemples :
#   pwsh -File run-tests.ps1                  # tous les tests (≈ 30–40 min)
#   pwsh -File run-tests.ps1 -Only T06,T08    # sous-ensemble
# Résultats : work\results.jsonl (une ligne par verdict) et work\results.md ; journaux : work\logs\.
#   pwsh -File run-tests.ps1 -Only LF         # démarrage rapide avec le faux ffmpeg d'Emby (LFa, LFc, LFd, LFe, LFb, LFb4, LFf)
param([string[]]$Only = @('T01', 'T02', 'T03', 'T04', 'T05', 'T06', 'T08', 'T09', 'T10', 'T11', 'T12'))
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ } | ForEach-Object { if ($_ -eq 'LF') { 'LFa', 'LFc', 'LFd', 'LFe', 'LFb', 'LFb4', 'LFf' } else { $_ } })
. "$PSScriptRoot\lib.ps1"
. "$PSScriptRoot\tests.ps1"
. "$PSScriptRoot\tests-localfirst.ps1"
. "$PSScriptRoot\tests-sleep.ps1"

$start = Get-Date
try {
    Initialize-Bench @('w1', 'w2') -Start
    if ($Only | Where-Object { $_ -like 'LF*' }) { Initialize-LocalFirstShim; Initialize-LocalFirstMedia }
    foreach ($t in $Only) {
        Write-Host "==== $t ($(Get-Date -Format HH:mm:ss)) ====" -ForegroundColor Cyan
        try { & $t }
        catch {
            Add-Result $t 'erreur du banc' 'FAIL' "$($_.Exception.Message) @ $($_.InvocationInfo.ScriptLineNumber)"
            # Un test interrompu peut laisser des relais et des workers dans une configuration de test : on repart d'un état propre.
            foreach ($w in @('w1', 'w2', 'w3')) { try { Stop-Worker $w; Set-TestWorkerConfig $w @{ Available = $true; Reclaim = 'FinishPlayback' } } catch { } }
            foreach ($line in @(Get-Content $Paths.Pids -ErrorAction SilentlyContinue)) {
                if ($line -like '*|shim-*') { try { Stop-Mine ([int]$line.Split('|')[0]) -Tree } catch { } }
            }
        }
        Assert-ProductionWorkerUntouched $Bench.Production
    }
} finally {
    Stop-Bench
    $md = @('| Test | Intitulé | Verdict | Détail |', '|---|---|---|---|')
    $md += $Results | ForEach-Object { "| $($_.Id) | $($_.Test) | **$($_.Verdict)** | $(($_.Detail -replace '\|', '/' -replace "`r?`n", '<br>')) |" }
    $md | Set-Content (Join-Path $Paths.Work 'results.md') -Encoding utf8
    Write-Host ("Durée totale : {0:0} min" -f ((Get-Date) - $start).TotalMinutes)
    $Results | Format-Table Id, Verdict, Test -AutoSize | Out-String | Write-Host
}


