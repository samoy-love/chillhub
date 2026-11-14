[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselinePackageManifest,
    [Parameter(Mandatory)][string]$NewPackageManifest,
    [Parameter(Mandatory)][string]$Output,
    [ValidateSet('unknown','solid-zlib','solid-lzma-64MiB')][string]$BaselineInstallerCompression = 'unknown'
)
$ErrorActionPreference = 'Stop'
$before = Get-Content -LiteralPath $BaselinePackageManifest -Raw | ConvertFrom-Json
$after = Get-Content -LiteralPath $NewPackageManifest -Raw | ConvertFrom-Json
function Comparison([string]$Name, [long]$Old, [long]$New) {
    [ordered]@{ name=$Name; beforeBytes=$Old; afterBytes=$New; savedBytes=($Old-$New); reductionPercent=if($Old){[Math]::Round(100.0*($Old-$New)/$Old,2)}else{$null} }
}
$rows = @('ChillHub.exe','ChillHub.Updater.exe','ChillHub.SecurityHelper.exe','mpv/mpv.exe') | ForEach-Object {
    $name=$_
    $old=@($before.files | Where-Object path -eq $name)
    $new=@($after.files | Where-Object path -eq $name)
    if($old.Count -ne 1 -or $new.Count -ne 1){throw "Нет однозначной пары $name"}
    foreach($item in @(@($before,$old[0]),@($after,$new[0]))){
        $file=Join-Path $item[0].payload $name
        if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $item[1].sha256){throw "Изменился $file"}
    }
    Comparison $name $old[0].size $new[0].size
}
$rows += Comparison 'installed payload' ($before.files | Measure-Object size -Sum).Sum ($after.files | Measure-Object size -Sum).Sum
$installerComparison = 'unavailable'
foreach($kind in @('installer','zip')){
    $oldRoot=Split-Path ([IO.Path]::GetFullPath($BaselinePackageManifest))
    $newRoot=Split-Path ([IO.Path]::GetFullPath($NewPackageManifest))
    $oldName=if($kind -eq 'installer'){"ChillHub-$($before.version)-Native-Setup.exe"}else{"ChillHub-$($before.version)-native-win-x64.zip"}
    $newName=if($kind -eq 'installer'){"ChillHub-$($after.version)-Native-Setup.exe"}else{"ChillHub-$($after.version)-native-win-x64.zip"}
    $old=Get-Item -LiteralPath (Join-Path $oldRoot $oldName) -ErrorAction SilentlyContinue
    $new=Get-Item -LiteralPath (Join-Path $newRoot $newName) -ErrorAction SilentlyContinue
    if($kind -eq 'installer'){
        $oldInfo=Get-Content -LiteralPath (Join-Path $oldRoot 'installer-info.json') -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
        $newInfo=Get-Content -LiteralPath (Join-Path $newRoot 'installer-info.json') -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
        $oldCompression=if($oldInfo.compression){$oldInfo.compression}else{$BaselineInstallerCompression}
        if($oldCompression -eq 'unknown' -or $oldCompression -ne $newInfo.compression){$installerComparison='excluded: compressor missing or different';continue}
        $installerComparison="same compressor: $oldCompression"
    }
    if($old -and $new){$rows += Comparison $kind $old.Length $new.Length}
}
$report=[ordered]@{
    schemaVersion=1; measuredUtc=[DateTime]::UtcNow.ToString('o');
    baselineVersion=$before.version; newVersion=$after.version;
    baselineManifest=[IO.Path]::GetFullPath($BaselinePackageManifest); newManifest=[IO.Path]::GetFullPath($NewPackageManifest);
    unit='bytes on disk; this is not RAM, CPU, GPU or startup timing';
    profile='Release; ThinLTO; codegen-units=1; stripped symbols; panic=unwind';
    architecture='Launcher: WGPU UI. Helpers: separately built software-only AuxiliaryWindow. Benchmark feature disabled in package.';
    installerComparison=$installerComparison;
    comparisons=@($rows)
}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Output -Encoding utf8NoBOM
$report | ConvertTo-Json -Depth 6
