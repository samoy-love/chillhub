[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Directory).Path
if (!('ChillHubPackageResources' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ChillHubPackageResources {
  [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
  [DllImport("kernel32", SetLastError=true)] static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
  [DllImport("kernel32")] static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
  [DllImport("kernel32")] static extern IntPtr LockResource(IntPtr resource);
  [DllImport("kernel32")] static extern uint SizeofResource(IntPtr module, IntPtr resource);
  [DllImport("kernel32")] static extern bool FreeLibrary(IntPtr module);
  public static byte[] Read(string path, int type) {
    IntPtr module=LoadLibraryEx(path,IntPtr.Zero,0x22);
    if(module==IntPtr.Zero) throw new Exception("Cannot read PE resources: "+path);
    try {
      IntPtr item=FindResource(module,(IntPtr)1,(IntPtr)type);
      if(item==IntPtr.Zero) throw new Exception("Missing PE resource "+type+": "+path);
      byte[] bytes=new byte[SizeofResource(module,item)];
      Marshal.Copy(LockResource(LoadResource(module,item)),bytes,0,bytes.Length);
      return bytes;
    } finally { FreeLibrary(module); }
  }
}
'@
}
$required = @('ChillHub.exe','ChillHub.Updater.exe','ChillHub.SecurityHelper.exe','mpv/mpv.exe','mpv/source.json','Data/games.json','Data/changelog.json','web-video/WebView2Loader.dll','web-video/source.json','web-video/LICENSE-Microsoft-WebView2.txt')
foreach ($path in $required) { if (!(Test-Path -LiteralPath (Join-Path $root $path) -PathType Leaf)) { throw "В пакете отсутствует $path" } }
foreach ($name in $required[0..2]) {
    $path = Join-Path $root $name
    $stream = [IO.File]::OpenRead($path)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw "Не PE: $name" }
        $stream.Position=0x3c; $offset=$reader.ReadUInt32(); $stream.Position=$offset
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw "Нужен x64 PE: $name" }
    } finally { $stream.Dispose() }
    # LOAD_LIBRARY_AS_DATAFILE | AS_IMAGE_RESOURCE: no executable entrypoint runs.
    [xml]$manifest = [Text.Encoding]::UTF8.GetString([ChillHubPackageResources]::Read($path,24)).Trim([char]0)
    $level = $manifest.SelectSingleNode("//*[local-name()='requestedExecutionLevel']")
    if ($level.level -ne 'asInvoker' -or $level.uiAccess -ne 'false') { throw "Неверные привилегии $name" }
    if ($manifest.SelectSingleNode("//*[local-name()='longPathAware']").InnerText -ne 'true') { throw "Нет longPathAware: $name" }
    if ($manifest.SelectSingleNode("//*[local-name()='dpiAwareness']").InnerText -ne 'PerMonitorV2') { throw "Нет PerMonitorV2: $name" }
    if ([ChillHubPackageResources]::Read($path,14).Length -lt 6) { throw "Нет значка: $name" }
    $actual = [Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion
    if ($actual -ne $Version -and $actual -ne "$Version.0") { throw "Версия $name ($actual) отличается от $Version" }
}
$source = Get-Content -LiteralPath (Join-Path $root 'mpv/source.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath (Join-Path $root 'mpv/mpv.exe') -Algorithm SHA256).Hash -ne $source.executableSha256) { throw 'Хеш mpv не совпадает с provenance.' }
$webSource = Get-Content -LiteralPath (Join-Path $root 'web-video/source.json') -Raw | ConvertFrom-Json
if ($webSource.runtimeBundled -ne $false -or $webSource.platform -ne 'win-x64' -or $webSource.source -notmatch '^https://api\.nuget\.org/v3-flatcontainer/microsoft\.web\.webview2/[0-9.]+/microsoft\.web\.webview2\.[0-9.]+\.nupkg$') { throw 'Неверное происхождение WebView2Loader.' }
if ((Get-FileHash -LiteralPath (Join-Path $root 'web-video/WebView2Loader.dll') -Algorithm SHA256).Hash -ne $webSource.loaderSha256) { throw 'Хеш WebView2Loader не совпадает с provenance.' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root 'web-video/WebView2Loader.dll')).FileVersion -ne $webSource.version) { throw 'Версия WebView2Loader не совпадает с provenance.' }
$null = & (Join-Path $PSScriptRoot 'native-package-assets.ps1') -Directory $root
$entries = @(Get-ChildItem -LiteralPath $root -Recurse -Force)
foreach ($file in $entries) {
    $relative = [IO.Path]::GetRelativePath($root,$file.FullName).Replace('\','/')
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Ссылка в payload: $relative" }
    $officialLoader = $relative -cin @('web-video/WebView2Loader.dll','web-video/LICENSE-Microsoft-WebView2.txt')
    if ($relative -match '(?i)(^|/)(config\.json|playtime[^/]*\.json|pending[^/]*\.json|client\.log|launcher\.version|launcher\.update-status|\.updating|cache|logs|qa-state|qa-games|SelfUpdate)(/|$)' -or (!$officialLoader -and $relative -match '(?i)(WebView2|coreclr|hostfxr|hostpolicy|\.runtimeconfig\.json$|\.deps\.json$)')) { throw "В payload попал пользовательский файл или внешний runtime: $relative" }
}
$files = @($entries | Where-Object { !$_.PSIsContainer })
[ordered]@{ valid=$true; version=$Version; fileCount=$files.Count; bytes=($files | Measure-Object Length -Sum).Sum; resourceValidation='PE data resources only, no process execution'; runtime='native x64; bundled mpv; optional installed WebView2 used only for YouTube'; webView2RuntimeBundled=$false; webView2LoaderSha256=$webSource.loaderSha256 }
