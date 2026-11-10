[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [string]$BinaryDirectory,
    [string]$MpvDirectory,
    [switch]$Zip
)
$ErrorActionPreference = 'Stop'
if (!$SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$version = [regex]::Match((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Cargo.toml') -Raw), '(?m)^version\s*=\s*"([^"]+)"').Groups[1].Value
if (!$BinaryDirectory) {
    $infoPath = Join-Path $PSScriptRoot 'dist/build-info.json'
    if (!(Test-Path -LiteralPath $infoPath)) { throw 'Нет build-info.json. Соберите build.ps1 или задайте -BinaryDirectory.' }
    $info = Get-Content -LiteralPath $infoPath -Raw | ConvertFrom-Json
    if ($info.version -ne $version) { throw 'Сведения сборки относятся к другой версии.' }
    if ($info.schemaVersion -ge 2 -and ($info.component -ne 'All' -or $info.benchmark)) { throw 'Пакет требует полной обычной сборки без benchmark feature.' }
    $BinaryDirectory = $info.binaryDirectory
    foreach($binary in $info.binaries){
        if((Get-FileHash -LiteralPath (Join-Path $BinaryDirectory $binary.name) -Algorithm SHA256).Hash -ne $binary.sha256){throw "Файл изменён после сборки: $($binary.name)"}
    }
}
$BinaryDirectory = (Resolve-Path -LiteralPath $BinaryDirectory).Path
if (!$MpvDirectory) {
    $MpvDirectory = Join-Path $env:LOCALAPPDATA 'chillhub-tools/mpv/20261109'
    if (!(Test-Path -LiteralPath (Join-Path $MpvDirectory 'source.json')) -or
        !(Test-Path -LiteralPath (Join-Path $MpvDirectory 'mpv.exe'))) {
        $null = & (Join-Path $PSScriptRoot 'scripts/prepare-video.ps1') -Destination $MpvDirectory
    }
}
$MpvDirectory = (Resolve-Path -LiteralPath $MpvDirectory).Path
$artifactRoot = Join-Path $PSScriptRoot ('dist/packages/' + $version + '-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$payload = Join-Path $artifactRoot 'ChillHub'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
$names = [ordered]@{ 'ChillHub.exe'='ChillHub.exe'; 'ChillHub-Updater.exe'='ChillHub.Updater.exe'; 'ChillHub-SecurityHelper.exe'='ChillHub.SecurityHelper.exe' }
foreach ($item in $names.GetEnumerator()) { Copy-Item -LiteralPath (Join-Path $BinaryDirectory $item.Key) -Destination (Join-Path $payload $item.Value) }
foreach ($relative in @(& (Join-Path $repo 'scripts/native-package-assets.ps1') -Directory $PSScriptRoot)) {
    $destination = Join-Path $payload $relative
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $relative) -Destination $destination
}
# Optimized image variants have derived art/<game>/hero|tile-N paths in the UI.
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'art') -Destination (Join-Path $payload 'art') -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'runtime/web-video') -Destination (Join-Path $payload 'web-video') -Recurse
New-Item -ItemType Directory -Path (Join-Path $payload 'Data'),(Join-Path $payload 'mpv') | Out-Null
foreach ($file in @('games.json','changelog.json')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Data/$file") -Destination (Join-Path $payload "Data/$file") }
foreach ($file in @('mpv.exe','d3dcompiler_43.dll','source.json')) { Copy-Item -LiteralPath (Join-Path $MpvDirectory $file) -Destination (Join-Path $payload 'mpv') }
$bundledSourcePath = Join-Path $payload 'mpv/source.json'
$bundledSource = Get-Content -LiteralPath $bundledSourcePath -Raw | ConvertFrom-Json
$bundledSource.executable = 'mpv.exe'
$bundledSource | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $bundledSourcePath -Encoding utf8NoBOM
if (Test-Path -LiteralPath (Join-Path $MpvDirectory 'doc')) { Copy-Item -LiteralPath (Join-Path $MpvDirectory 'doc') -Destination (Join-Path $payload 'mpv/doc') -Recurse }
$validation = & (Join-Path $repo 'scripts/native-verify-package.ps1') -Directory $payload -Version $version
$manifest = @(Get-ChildItem -LiteralPath $payload -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path=[IO.Path]::GetRelativePath($payload,$_.FullName).Replace('\','/'); size=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
[ordered]@{ schemaVersion=1; version=$version; createdUtc=[DateTime]::UtcNow.ToString('o'); payload=$payload; validation=$validation; files=$manifest } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $artifactRoot 'package-manifest.json') -Encoding utf8NoBOM
if ($Zip) {
    $zipPath = Join-Path $artifactRoot "ChillHub-$version-native-win-x64.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $zipFiles = @($archive.Entries | Where-Object { !$_.FullName.EndsWith('/') })
        if ($zipFiles.Count -ne $manifest.Count) { throw 'Состав ZIP не совпадает с payload.' }
        foreach ($file in $manifest) {
            $entry = $archive.GetEntry($file.path)
            if (!$entry -or $entry.Length -ne $file.size) { throw "В ZIP отсутствует или повреждён $($file.path)" }
        }
    } finally { $archive.Dispose() }
}
# The review shortcut is outside the install/update payload. It never runs here,
# and its future config/games/cache folder is separate from the installed files.
$previewData = Join-Path $artifactRoot ('preview-state-' + [Guid]::NewGuid().ToString('N'))
$previewLink = Join-Path $artifactRoot 'ChillHub-Preview.lnk'
$shell = New-Object -ComObject WScript.Shell
try {
    $shortcut = $shell.CreateShortcut($previewLink)
    $shortcut.TargetPath = Join-Path $payload 'ChillHub.exe'
    $shortcut.Arguments = '--preview-directory "' + $previewData + '"'
    $shortcut.WorkingDirectory = $payload
    $shortcut.IconLocation = (Join-Path $payload 'ChillHub.exe') + ',0'
    $shortcut.Description = 'Проверочная копия с отдельными настройками и папкой игр'
    $shortcut.Save()
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
} finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
[ordered]@{ payload=$payload; manifest=(Join-Path $artifactRoot 'package-manifest.json'); version=$version; validation=$validation; previewShortcut=$previewLink; previewDirectory=$previewData; previewStarted=$false } | ConvertTo-Json -Depth 5
