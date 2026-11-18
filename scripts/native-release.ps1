[CmdletBinding()]
param([string]$ExpectedVersion = $env:DEPLOY_VERSION)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repo 'launcher/ChillHub.Native'
$version = [regex]::Match((Get-Content (Join-Path $project 'Cargo.toml') -Raw), '(?m)^version\s*=\s*"([^"]+)"').Groups[1].Value
if (!$ExpectedVersion -or $version -ne $ExpectedVersion) {
    throw "Версия релиза должна совпадать с Cargo.toml: $version."
}
& (Join-Path $PSScriptRoot 'ci/ensure-native-rust.ps1')
& (Join-Path $PSScriptRoot 'ci/ensure-nsis.ps1')
# PR owns the full tests. Deployment checks the build and exact payload again.
& (Join-Path $project 'build.ps1')
$package = & (Join-Path $project 'package.ps1') -SkipBuild -Zip | ConvertFrom-Json
$null = & (Join-Path $PSScriptRoot 'native-package-tests.ps1') -PackageManifest $package.manifest
$null = & (Join-Path $PSScriptRoot 'native-installer.ps1') -PackageManifest $package.manifest
$root = Split-Path $package.manifest
$installer = Get-Content (Join-Path $root 'installer-info.json') -Raw | ConvertFrom-Json
$output = Join-Path $PSScriptRoot 'generated_downloads'
New-Item -ItemType Directory -Force -Path $output | Out-Null
Copy-Item -LiteralPath $installer.installer -Destination (Join-Path $output 'ChillHub-Setup.exe')
Copy-Item -LiteralPath (Join-Path $root "ChillHub-$version-native-win-x64.zip") -Destination (Join-Path $output 'ChillHub-launcher-payload.zip')
[ordered]@{ version=$version; installer=$installer.sha256; package=$package.manifest } | ConvertTo-Json
