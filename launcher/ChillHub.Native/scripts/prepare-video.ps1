[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'chillhub-tools/mpv/20261109')
)
$ErrorActionPreference = 'Stop'
$archiveName = 'mpv-x86_64-20261109-git-8bc468896e.7z'
$source = "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261109/$archiveName"
$expected = '7e9588d62c5b830ac2413a3a475b5588e445406e0466dd6333477e631237ec4a'

# This distributor is linked by https://mpv.io/installation/.
# The pinned SHA256 is the digest published with the GitHub release asset.
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$archive = Join-Path $Destination $archiveName
if (!(Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri $source -OutFile $archive
}
$actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw "mpv archive SHA256 mismatch: $actual" }
& tar.exe -xf $archive -C $Destination
if ($LASTEXITCODE) { throw "Cannot unpack mpv archive, tar exit code $LASTEXITCODE" }
$executable = Join-Path $Destination 'mpv.exe'
if (!(Test-Path -LiteralPath $executable)) { throw 'The archive has no mpv.exe' }

[ordered]@{
    executable = $executable
    distributor = 'shinchiro/mpv-winbuild-cmake'
    officialListing = 'https://mpv.io/installation/'
    source = $source
    archiveSha256 = $actual
    executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    architecture = 'x86_64 (baseline, not x86_64-v3)'
    preparedUtc = [DateTime]::UtcNow.ToString('o')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Destination 'source.json') -Encoding utf8NoBOM
$executable
