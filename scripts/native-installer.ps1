[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageManifest,
    [string]$MakeNsis,
    [switch]$FastCompress
)
$ErrorActionPreference = 'Stop'
$manifestPath = (Resolve-Path -LiteralPath $PackageManifest).Path
$package = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($package.schemaVersion -ne 1 -or !$package.validation.valid) { throw 'Нужен проверенный package-manifest.json Native.' }
$payload = (Resolve-Path -LiteralPath $package.payload).Path
$null = & (Join-Path $PSScriptRoot 'native-verify-package.ps1') -Directory $payload -Version $package.version
$actualFiles = @(Get-ChildItem -LiteralPath $payload -Recurse -File -Force)
if ($actualFiles.Count -ne $package.files.Count) { throw 'Состав payload изменился после упаковки.' }
$uniquePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $package.files) {
    if (!$uniquePaths.Add($file.path)) { throw "Повторяющийся файл в manifest: $($file.path)" }
    $path = [IO.Path]::GetFullPath((Join-Path $payload $file.path))
    if (!$path.StartsWith($payload.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Путь за пределами payload.' }
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "Повреждён payload: $($file.path)" }
}
if (!$MakeNsis) {
    $command = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if ($command) { $MakeNsis=$command.Source }
    else {
        $MakeNsis = @("${env:ProgramFiles(x86)}/NSIS/makensis.exe", "$env:ProgramFiles/NSIS/makensis.exe") | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
}
if (!$MakeNsis -or !(Test-Path -LiteralPath $MakeNsis)) { throw 'NSIS не найден. Задайте -MakeNsis; глобальная установка не нужна.' }
if ($package.version -notmatch '^\d+\.\d+\.\d+$') { throw 'Установщику нужна числовая версия major.minor.patch.' }
$output = Join-Path (Split-Path $manifestPath) "ChillHub-$($package.version)-Native-Setup.exe"
if (Test-Path -LiteralPath $output) { throw "Файл уже существует: $output. Создайте новую упаковку." }
& (Join-Path $PSScriptRoot 'native-installer-artwork.ps1') -OutputDirectory (Split-Path $manifestPath)
$parameters = @('/V2','/DNATIVE_CLIENT',"/DNATIVE_OUTPUT=$output","/DPAYLOAD_DIR=$payload","/DAPP_VERSION=$($package.version)","/DAPP_VERSION_NUMERIC=$($package.version).0")
$parameters += "/DNATIVE_SIDEBAR=$(Join-Path (Split-Path $manifestPath) 'installer-sidebar.bmp')"
if ($FastCompress) { $parameters += '/DFAST_COMPRESS' }
$parameters += Join-Path $PSScriptRoot 'installer.nsi'
& $MakeNsis @parameters
if ($LASTEXITCODE -or !(Test-Path -LiteralPath $output)) { throw "NSIS завершился с ошибкой $LASTEXITCODE" }
$result = [ordered]@{ installer=$output; version=$package.version; sha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant(); size=(Get-Item -LiteralPath $output).Length; compiler=(& $MakeNsis /VERSION | Out-String).Trim(); compression=if($FastCompress){'solid-zlib'}else{'solid-lzma-64MiB'}; nativeClient=$true; installed=$false }
$json = $result | ConvertTo-Json -Depth 5
$json | Set-Content -LiteralPath (Join-Path (Split-Path $manifestPath) 'installer-info.json') -Encoding utf8NoBOM
$json
