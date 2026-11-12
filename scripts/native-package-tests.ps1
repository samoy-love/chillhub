[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageManifest)
$ErrorActionPreference = 'Stop'
$package = Get-Content -LiteralPath $PackageManifest -Raw | ConvertFrom-Json
$verify = Join-Path $PSScriptRoot 'native-verify-package.ps1'
$null = & $verify -Directory $package.payload -Version $package.version
$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$fixture = Join-Path $temporaryBase ('chillhub-native-package-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$checks = [Collections.Generic.List[string]]::new()
function Expect-Rejection([string]$Expected) {
    try { $null = & $verify -Directory $fixture -Version $package.version }
    catch { if($_.Exception.Message -like "*$Expected*"){ $checks.Add($Expected); return }; throw }
    throw "Verifier accepted invalid fixture: $Expected"
}
try {
    # Independent copies keep negative fixtures separate from the real package.
    # No executable is started, and no shared file contents can be modified.
    $required = @('ChillHub.exe','ChillHub.Updater.exe','ChillHub.SecurityHelper.exe','mpv/mpv.exe','mpv/source.json','Data/games.json','Data/changelog.json')
    $required += @(& (Join-Path $PSScriptRoot 'native-package-assets.ps1') -Directory $package.payload)
    foreach ($relative in $required) {
        $target=Join-Path $fixture $relative
        New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        $source = Join-Path $package.payload $relative
        Copy-Item -LiteralPath $source -Destination $target
    }
    $null = & $verify -Directory $fixture -Version $package.version
    $checks.Add('valid PE resources and runtime provenance')
    $sentinel=Join-Path $fixture 'config.json'
    Set-Content -LiteralPath $sentinel -Value '{"fixture":true}'
    Expect-Rejection 'config.json'
    Remove-Item -LiteralPath $sentinel
    $sentinel=Join-Path $fixture 'coreclr.dll'
    Set-Content -LiteralPath $sentinel -Value 'not a runtime; rejection fixture'
    (Get-Item -LiteralPath $sentinel).Attributes=[IO.FileAttributes]::Hidden
    Expect-Rejection 'coreclr.dll'
    Remove-Item -LiteralPath $sentinel -Force
    $sentinel=Join-Path $fixture 'WebView2Runtime.dll'
    Set-Content -LiteralPath $sentinel -Value 'not a runtime; rejection fixture'
    Expect-Rejection 'WebView2Runtime.dll'
    Remove-Item -LiteralPath $sentinel
    $sentinel=Join-Path $fixture 'WebView2Loader.dll'
    Set-Content -LiteralPath $sentinel -Value 'forbidden browser loader'
    Expect-Rejection 'WebView2Loader.dll'
    Remove-Item -LiteralPath $sentinel
    Remove-Item -LiteralPath (Join-Path $fixture 'ChillHub.Updater.exe')
    Expect-Rejection 'ChillHub.Updater.exe'
    [ordered]@{passed=$checks.Count; checks=$checks; executablesStarted=0; realUserStateModified=$false} | ConvertTo-Json -Depth 4
} finally {
    $resolved=[IO.Path]::GetFullPath($fixture)
    if (!$resolved.StartsWith($temporaryBase+'\',[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -notlike 'chillhub-native-package-test-*') { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
