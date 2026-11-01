# Builds the all-in-one per-user setup: EmbyRemoteWorker-Setup-<version>.exe embeds a zip of the packaged app folder
# (artifacts\worker-package\EmbyRemoteWorker, produced by package-worker.ps1). Only the .NET 8 SDK is used.
#   pwsh tools\build-installer.ps1 [-Version 0.7.7]
# Code signing (optional): the worker exe and the setup are signed and timestamped with a code-signing certificate,
# taken from the current user's store (-CertThumbprint) or a .pfx file (-PfxPath, password asked).
#   pwsh tools\build-installer.ps1 -CertThumbprint 0123ABCD...
#   pwsh tools\build-installer.ps1 -PfxPath C:\certs\codesign.pfx
param(
    # Default: the worker's version in its .csproj (the setup and the app it carries share a number).
    [string]$Version,
    [string]$App = "$PSScriptRoot\..\artifacts\worker-package\EmbyRemoteWorker",
    [string]$Out = "$PSScriptRoot\..\artifacts\installer",
    [string]$CertThumbprint,
    [string]$PfxPath,
    [string]$TimestampServer = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
if (-not $Version) {
    $Version = ([xml](Get-Content "$PSScriptRoot\..\src\RemoteTranscode.Worker\RemoteTranscode.Worker.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$env:PATH = "C:\Program Files\dotnet;$env:PATH"
Add-Type -AssemblyName System.IO.Compression
if (-not (Test-Path (Join-Path $App 'EmbyRemoteWorker.exe'))) { throw "Application introuvable : $App (lancer package-worker.ps1)" }

$cert = $null
if ($CertThumbprint) {
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object Thumbprint -eq $CertThumbprint.Replace(' ', '').ToUpperInvariant()
    if (-not $cert) { throw "Certificat de signature de code introuvable : $CertThumbprint (magasin de l'utilisateur)" }
} elseif ($PfxPath) {
    $cert = Get-PfxCertificate -FilePath $PfxPath
    if (-not $cert.HasPrivateKey) { throw "Le fichier $PfxPath ne contient pas de clé privée" }
}
# Signs and timestamps a file; a signature that is not valid stops the build.
function Sign-File([string]$path) {
    if (-not $cert) { return }
    $r = Set-AuthenticodeSignature -FilePath $path -Certificate $cert -TimestampServer $TimestampServer -HashAlgorithm SHA256
    if ($r.Status -ne 'Valid') { throw "Signature de $path : $($r.Status) ($($r.StatusMessage))" }
    "Signé : $path"
}
if (-not $cert) { 'Aucun certificat (-CertThumbprint ou -PfxPath) : installeur et worker non signés.' }
# The worker exe is signed before it goes into the payload.
Sign-File (Join-Path $App 'EmbyRemoteWorker.exe')

# 1. Payload zip in a staging folder, never in the source tree. Files are opened with read/write/delete sharing
#    because EmbyRemoteWorker.exe may be running from that folder.
$stage = Join-Path ([IO.Path]::GetTempPath()) "EmbyRemoteWorker-installer-$PID"
New-Item -ItemType Directory -Force $stage | Out-Null
$payload = Join-Path $stage 'payload.zip'
try {
    $zipStream = [IO.File]::Create($payload)
    $zip = [IO.Compression.ZipArchive]::new($zipStream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $root = (Resolve-Path $App).Path.TrimEnd('\')
        foreach ($file in Get-ChildItem $root -Recurse -File) {
            $name = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
            $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $file.LastWriteTime
            $src = [IO.File]::Open($file.FullName, 'Open', 'Read', [IO.FileShare]'ReadWrite, Delete')
            $dst = $entry.Open()
            try { $src.CopyTo($dst) } finally { $dst.Dispose(); $src.Dispose() }
        }
    } finally { $zip.Dispose(); $zipStream.Dispose() }
    "Payload : {0:N1} Mo" -f ((Get-Item $payload).Length / 1MB)

    # 2. Single-file self-contained setup with the payload embedded.
    $publish = Join-Path $stage 'publish'
    dotnet publish "$PSScriptRoot\..\src\RemoteTranscode.Installer" -c Release -r win-x64 --self-contained `
        "-p:PayloadZip=$payload" "-p:Version=$Version" -o $publish --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish a échoué ($LASTEXITCODE)" }

    New-Item -ItemType Directory -Force $Out | Out-Null
    $setup = Join-Path $Out "EmbyRemoteWorker-Setup-$Version.exe"
    Copy-Item (Join-Path $publish 'EmbyRemoteWorker-Setup.exe') $setup -Force
    Sign-File $setup
    "Installeur : $setup ({0:N1} Mo)" -f ((Get-Item $setup).Length / 1MB)
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
