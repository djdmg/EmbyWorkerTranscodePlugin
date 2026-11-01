# Builds the self-contained Emby plugin: the ffmpeg relay is published for each supported platform, compressed and
# embedded in the DLL, next to the coordinator (compiled in). Output: artifacts\plugin\RemoteTranscode.EmbyPlugin.dll
#
# -IssueReportToken (default: the ISSUE_REPORT_TOKEN environment variable, set from the repository secret of the same
# name by GitHub Actions): GitHub token embedded in the DLL for the automatic failure reports. Never written to the
# repository nor printed. Without it the reports are inactive in the built plugin. Anyone can extract it from a
# published DLL: it must be a fine-grained token limited to this repository with the Issues permission only.
param(
    [string[]] $Runtimes = @('linux-x64', 'linux-arm64', 'linux-musl-x64', 'win-x64'),
    [string] $IssueReportToken = $env:ISSUE_REPORT_TOKEN
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$packages = Join-Path $root 'artifacts\plugin-shims'
$out = Join-Path $root 'artifacts\plugin'

Remove-Item $packages -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $packages, $out | Out-Null

foreach ($rid in $Runtimes) {
    $publish = Join-Path $root "artifacts\shim-$rid"
    & $dotnet publish (Join-Path $root 'src\RemoteTranscode.Shim') -c Release -r $rid -o $publish --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "publication du relais $rid en échec" }
    $exe = Get-ChildItem $publish -Filter 'remote-ffmpeg*' | Where-Object { $_.Extension -in '', '.exe' } | Select-Object -First 1
    $target = Join-Path $packages "$rid.gz"
    $in = [IO.File]::OpenRead($exe.FullName)
    $file = [IO.File]::Create($target)
    $gz = New-Object IO.Compression.GZipStream($file, [IO.Compression.CompressionLevel]::Optimal)
    $in.CopyTo($gz); $gz.Dispose(); $file.Dispose(); $in.Dispose()
    '{0,-16} {1,6:0.0} Mo -> {2,6:0.0} Mo' -f $rid, ($exe.Length / 1MB), ((Get-Item $target).Length / 1MB)
}

$buildArgs = @((Join-Path $root 'src\RemoteTranscode.EmbyPlugin'), '-c', 'Release', '--nologo', '-v', 'q')
if ($IssueReportToken -and $IssueReportToken.Trim()) {
    # Stored reversed and in base64 ("b64r:", decoded by FailureReporter.DecodeToken): not a protection, only so that a
    # plain search of the DLL for a GitHub token pattern does not find it.
    $chars = $IssueReportToken.Trim().ToCharArray(); [array]::Reverse($chars)
    $buildArgs += "-p:IssueReportToken=b64r:$([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(-join $chars)))"
    'signalement des échecs sur GitHub : jeton fourni, actif dans ce plugin'
} else {
    # Always passed (empty here): an incremental build never keeps the token of a previous build.
    $buildArgs += '-p:IssueReportToken='
    'signalement des échecs sur GitHub : pas de jeton (ISSUE_REPORT_TOKEN), inactif dans ce plugin'
}
& $dotnet build @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'compilation du plugin en échec' }
Copy-Item (Join-Path $root 'src\RemoteTranscode.EmbyPlugin\bin\Release\net8.0\RemoteTranscode.EmbyPlugin.dll') $out -Force
$dll = Get-Item (Join-Path $out 'RemoteTranscode.EmbyPlugin.dll')
'plugin : {0} ({1:0.0} Mo)' -f $dll.FullName, ($dll.Length / 1MB)
