[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
# This gate modifies HKCU and shell shortcuts. It belongs exclusively on a
# disposable hosted Windows runner, never on a developer's working profile.
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Native installer smoke requires a disposable GitHub-hosted runner.'
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$stage = Join-Path $env:RUNNER_TEMP ('chillhub-mpv-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
$archive = Join-Path $stage 'mpv.7z'
$source = 'https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261109/mpv-x86_64-20261109-git-8bc468896e.7z'
$archiveHash = '7e9588d62c5b830ac2413a3a475b5588e445406e0466dd6333477e631237ec4a'
$exeHash = 'ec653e7b902fea224721ba1139bc7d6e03a79dc69b8b97b52eb7534825346c03'
Invoke-WebRequest -Uri $source -OutFile $archive -MaximumRetryCount 3 -RetryIntervalSec 3
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $archiveHash) { throw 'mpv archive hash mismatch' }
$mpv = Join-Path $stage 'payload'
$sevenZip = (Get-Command 7z.exe -ErrorAction Stop).Source
& $sevenZip x $archive "-o$mpv" -y -bso0
if ($LASTEXITCODE) { throw 'Could not extract the verified mpv archive' }
if ((Get-FileHash -LiteralPath (Join-Path $mpv 'mpv.exe') -Algorithm SHA256).Hash -ine $exeHash) { throw 'mpv executable hash mismatch' }
[ordered]@{
    executable = 'mpv.exe'; distributor = 'shinchiro/mpv-winbuild-cmake';
    officialListing = 'https://mpv.io/installation/'; source = $source;
    archiveSha256 = $archiveHash; executableSha256 = $exeHash;
    architecture = 'x86_64 (baseline, not x86_64-v3)'; preparedUtc = [DateTime]::UtcNow.ToString('o')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $mpv 'source.json') -Encoding utf8NoBOM
$package = & (Join-Path $repo 'launcher/ChillHub.Native/package.ps1') -SkipBuild -MpvDirectory $mpv | ConvertFrom-Json
& (Join-Path $repo 'scripts/native-package-tests.ps1') -PackageManifest $package.manifest
$manifest = Get-Content -LiteralPath $package.manifest -Raw | ConvertFrom-Json
& (Join-Path $repo 'scripts/native-installer.ps1') -PackageManifest $package.manifest -FastCompress
$info = Get-Content -LiteralPath (Join-Path (Split-Path $package.manifest) 'installer-info.json') -Raw | ConvertFrom-Json
& (Join-Path $repo 'scripts/ci/smoke-installer.ps1') -Setup $info.installer -ExpectedVersion $manifest.version
