[CmdletBinding()]
param([string]$Target, [string]$MpvDirectory, [switch]$BuildInstaller, [string]$MakeNsis, [switch]$ValidationOnly)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../launcher/ChillHub.Native'))
New-Item -ItemType Directory -Path (Join-Path $project 'dist') -Force | Out-Null
$log=Join-Path $project 'dist/ci-tests.log'
& (Join-Path $project 'build.ps1') -FormatCheck
. (Join-Path $PSScriptRoot 'ensure-webview2-bootstrapper.ps1')
$null = Ensure-WebView2Bootstrapper -RedistDir (Join-Path $PSScriptRoot 'Redist') -Required
& (Join-Path $PSScriptRoot 'native-bootstrapper-tests.ps1')
& (Join-Path $project 'build.ps1') -Clippy -Target $Target
$previousQa = $env:CHILLHUB_UI_QA
try {
    $env:CHILLHUB_UI_QA = '1'
    & (Join-Path $project 'build.ps1') -Test -Target $Target *> $log
} catch {
    $failure = $_
    try {
        & (Join-Path $PSScriptRoot 'native-dependency-diagnostics.ps1') -TestLog $log |
            Tee-Object -FilePath (Join-Path $project 'dist/ci-dependencies.log')
    } catch { Write-Warning "Dependency diagnostics failed: $_" }
    throw $failure
} finally { $env:CHILLHUB_UI_QA = $previousQa }
& (Join-Path $project 'build.ps1') -Target $Target
if ($ValidationOnly) { return }
$arguments=@{SkipBuild=$true;Zip=$true}; if($MpvDirectory){$arguments.MpvDirectory=$MpvDirectory}
$package = & (Join-Path $project 'package.ps1') @arguments | ConvertFrom-Json
& (Join-Path $PSScriptRoot 'native-package-tests.ps1') -PackageManifest $package.manifest
& (Join-Path $PSScriptRoot 'native-parity.ps1') -TestLog $log -PackageManifest $package.manifest -Output (Join-Path (Split-Path $package.manifest) 'parity-result.json')
if($BuildInstaller){ & (Join-Path $PSScriptRoot 'native-installer.ps1') -PackageManifest $package.manifest -MakeNsis $MakeNsis }
# This opt-in build entrypoint does not publish or change the production workflow.
