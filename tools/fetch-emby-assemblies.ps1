# Fetches the three Emby Server assemblies the plugin is compiled against (MediaBrowser.Common, .Controller, .Model)
# from Emby's official release package. They are Emby's property and are not stored in this repository.
#   pwsh tools\fetch-emby-assemblies.ps1 [-EmbyVersion 4.10.2.0]
# Output: research\emby-<version>\ (the folder RemoteTranscode.EmbyPlugin.csproj references).
param(
    [string]$EmbyVersion = '4.10.2.0',
    [string]$Out = "$PSScriptRoot\..\research\emby-$EmbyVersion"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$names = 'MediaBrowser.Common.dll', 'MediaBrowser.Controller.dll', 'MediaBrowser.Model.dll'
if (($names | Where-Object { -not (Test-Path (Join-Path $Out $_)) }).Count -eq 0) { "Already present: $Out"; return }

$zip = Join-Path ([IO.Path]::GetTempPath()) "embyserver-netcore_$EmbyVersion.zip"
if (-not (Test-Path $zip)) {
    $url = "https://github.com/MediaBrowser/Emby.Releases/releases/download/$EmbyVersion/embyserver-netcore_$EmbyVersion.zip"
    "Downloading $url"
    Invoke-WebRequest $url -OutFile $zip
}
New-Item -ItemType Directory -Force $Out | Out-Null
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($name in $names) {
        $entry = $archive.Entries | Where-Object { $_.Name -eq $name } | Sort-Object { $_.FullName.Length } | Select-Object -First 1
        if (-not $entry) { throw "$name not found in $zip" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $Out $name), $true)
        "$name <- $($entry.FullName)"
    }
}
finally { $archive.Dispose() }
