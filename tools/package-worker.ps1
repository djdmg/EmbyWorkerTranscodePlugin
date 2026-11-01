# Builds the distributable worker: self-contained EmbyRemoteWorker.exe + bundled FFmpeg (GPL) + notices, zipped.
# Nothing has to be installed on the target PC (no .NET, no FFmpeg).
#   pwsh tools\package-worker.ps1
param(
    [string]$FfmpegVersion = '9.0.2',
    # SHA-256 of gyan.dev's ffmpeg-<version>-full_build.zip. "full" rather than "essentials" since worker 0.7.15: only it
    # has libplacebo (HDR -> SDR on the GPU through Vulkan).
    [string]$FfmpegSha256 = '759d0a9831c436a0eb331ad36f236c06cb04aaa0005da46571f0e9d3d9206f6b',
    # Never the folder a worker runs from (artifacts\worker\EmbyRemoteWorker on the development PC): its locked exe
    # would silently stay at the old version.
    [string]$Out = "$PSScriptRoot\..\artifacts\worker-package"
)
$ErrorActionPreference = 'Stop'
$env:PATH = "C:\Program Files\dotnet;$env:PATH"
$name = "ffmpeg-$FfmpegVersion-full_build"
$cache = Join-Path $env:LOCALAPPDATA "EmbyRemoteWorker-build\$name"
$zip = "$cache.zip"

# 1. FFmpeg, downloaded once into its own folder and verified.
New-Item -ItemType Directory -Force (Split-Path $zip) | Out-Null
if (-not (Test-Path $zip)) {
    Invoke-WebRequest "https://github.com/GyanD/codexffmpeg/releases/download/$FfmpegVersion/$name.zip" -OutFile $zip
}
$actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $FfmpegSha256) { Remove-Item $zip; throw "FFmpeg checksum mismatch: $actual" }
if (-not (Test-Path $cache)) { Expand-Archive $zip -DestinationPath (Split-Path $cache) }

# 2. Self-contained single-file worker.
$app = Join-Path $Out 'EmbyRemoteWorker'
Remove-Item $app -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path $app) { throw "Impossible de vider $app (un worker tourne-t-il depuis ce dossier ?)" }
dotnet publish "$PSScriptRoot\..\src\RemoteTranscode.Worker" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $app | Out-Null
if ($LASTEXITCODE) { throw "dotnet publish a échoué ($LASTEXITCODE)" }
# The packaged exe must be the version just built.
$expected = ([xml](Get-Content "$PSScriptRoot\..\src\RemoteTranscode.Worker\RemoteTranscode.Worker.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$built = (Get-Item (Join-Path $app 'EmbyRemoteWorker.exe')).VersionInfo.FileVersion
if (-not $built.StartsWith("$expected.")) { throw "EmbyRemoteWorker.exe en version $built au lieu de $expected" }
Get-ChildItem $app -Filter *.pdb | Remove-Item

# 3. Bundled FFmpeg and its licence (GPLv3 build).
$ff = New-Item -ItemType Directory -Force (Join-Path $app 'ffmpeg')
Copy-Item "$cache\bin\ffmpeg.exe" $ff
Copy-Item "$cache\LICENSE" "$ff\LICENSE.txt"
Copy-Item "$cache\README.txt" "$ff\README-build.txt"
@"
FFmpeg $FfmpegVersion — build « full » de gyan.dev (https://www.gyan.dev/ffmpeg/builds/), avec libplacebo (tone mapping HDR sur le GPU)
Licence : GNU GPL v3 (voir LICENSE.txt). Ce FFmpeg est un programme séparé, exécuté par Emby Remote Worker.

Sources correspondantes :
  - FFmpeg : https://github.com/FFmpeg/FFmpeg/tree/n$FfmpegVersion  (https://ffmpeg.org/download.html)
  - Configuration et bibliothèques du build : README-build.txt et https://www.gyan.dev/ffmpeg/builds/
  - Archive d'origine : https://github.com/GyanD/codexffmpeg/releases/tag/$FfmpegVersion
    SHA-256 : $FfmpegSha256
Sur demande, une copie des sources correspondantes peut être fournie avec ce logiciel.
"@ | Set-Content "$ff\SOURCES.txt" -Encoding utf8

@"
Emby Remote Worker
==================
Lancer EmbyRemoteWorker.exe : l'icône apparaît dans la zone de notification.
Au premier lancement, la fenêtre Paramètres s'ouvre : coller le code d'association fourni par le serveur.
Rien d'autre à installer : .NET et FFmpeg sont inclus (dossier ffmpeg\, voir ffmpeg\SOURCES.txt pour la licence).

Désinstallation : Quitter depuis l'icône, décocher « Démarrer avec Windows » auparavant si activé,
puis supprimer ce dossier et %LOCALAPPDATA%\EmbyRemoteWorker (configuration, journaux).
"@ | Set-Content "$app\LISEZMOI.txt" -Encoding utf8

# 4. Zip.
$archive = Join-Path $Out "EmbyRemoteWorker-win-x64.zip"
Remove-Item $archive -ErrorAction SilentlyContinue
Compress-Archive -Path $app -DestinationPath $archive
Get-ChildItem $app -Recurse -File | Select-Object @{n='Fichier';e={$_.FullName.Substring($app.Length + 1)}}, @{n='Mo';e={[math]::Round($_.Length / 1MB, 1)}} | Format-Table -AutoSize
"Archive : $archive ({0:N0} Mo)" -f ((Get-Item $archive).Length / 1MB)
