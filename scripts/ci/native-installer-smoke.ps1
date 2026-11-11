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
$mpv = Join-Path $stage 'payload'
$null = & (Join-Path $repo 'launcher/ChillHub.Native/scripts/prepare-video.ps1') -Destination $mpv
$package = & (Join-Path $repo 'launcher/ChillHub.Native/package.ps1') -SkipBuild -MpvDirectory $mpv | ConvertFrom-Json
& (Join-Path $repo 'scripts/native-package-tests.ps1') -PackageManifest $package.manifest
& (Join-Path $repo 'scripts/native-parity.ps1') -TestLog (Join-Path $repo 'launcher/ChillHub.Native/dist/ci-tests.log') -PackageManifest $package.manifest -Output (Join-Path (Split-Path $package.manifest) 'parity-result.json')
$manifest = Get-Content -LiteralPath $package.manifest -Raw | ConvertFrom-Json
& (Join-Path $repo 'scripts/native-installer.ps1') -PackageManifest $package.manifest -FastCompress
$info = Get-Content -LiteralPath (Join-Path (Split-Path $package.manifest) 'installer-info.json') -Raw | ConvertFrom-Json
& (Join-Path $repo 'scripts/ci/smoke-installer.ps1') -Setup $info.installer -ExpectedVersion $manifest.version
