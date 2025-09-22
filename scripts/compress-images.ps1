param(
  [string]$ImagesDir = "$(Split-Path -Parent $PSScriptRoot)\landing\assets\images",
  [string]$OutDir = "$(Split-Path -Parent $PSScriptRoot)\landing\assets\images\optimized",
  [switch]$Replace,
  [switch]$ToWebP,
  [ValidateRange(1,100)][int]$Quality = 82,
  [switch]$InstallTools
)

$ErrorActionPreference = 'Stop'

# Ensure Unicode I/O
try {
  [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($true)
  [Console]::InputEncoding  = [System.Text.UTF8Encoding]::new($true)
} catch {}

function New-EnsuredDirectory($path){ if(!(Test-Path $path)){ New-Item -ItemType Directory -Force -Path $path | Out-Null } }

# Tools detection (PATH or local tools folder)
$ToolsDir = Join-Path $PSScriptRoot 'tools'
New-EnsuredDirectory $ToolsDir

function Find-Tool([string]$name){
  $exe = $null
  try { $cmd = Get-Command $name -ErrorAction Stop; if($cmd){ $exe = $cmd.Source } } catch {}
  if($exe){ return $exe }
  $local = Join-Path $ToolsDir $name
  if(Test-Path $local){ return $local }
  $localExe = Join-Path $ToolsDir ($name + '.exe')
  if(Test-Path $localExe){ return $localExe }
  return $null
}

# Optional download of tools
function Install-Tool-PNGQuant(){
  $url = 'https://github.com/kornelski/pngquant/releases/latest/download/pngquant-windows.zip'
  $zip = Join-Path $env:TEMP 'pngquant.zip'
  Invoke-WebRequest -Uri $url -OutFile $zip
  Expand-Archive -Path $zip -DestinationPath $ToolsDir -Force
}
function Install-Tool-OptiPNG(){
  $url = 'https://downloads.sourceforge.net/project/optipng/OptiPNG/optipng-0.7.7/optipng-0.7.7-win32.zip'
  $zip = Join-Path $env:TEMP 'optipng.zip'
  Invoke-WebRequest -Uri $url -OutFile $zip
  Expand-Archive -Path $zip -DestinationPath $ToolsDir -Force
}
function Install-Tool-LibWebP(){
  $url = 'https://storage.googleapis.com/downloads.webmproject.org/releases/webp/libwebp-1.3.2-windows-x64.zip'
  $zip = Join-Path $env:TEMP 'libwebp.zip'
  Invoke-WebRequest -Uri $url -OutFile $zip
  Expand-Archive -Path $zip -DestinationPath $ToolsDir -Force
  # Try to locate cwebp/img2webp inside extracted tree
  Get-ChildItem -Path $ToolsDir -Recurse -File -Include cwebp.exe,img2webp.exe | ForEach-Object {
    Copy-Item $_.FullName -Destination (Join-Path $ToolsDir $_.Name) -Force
  }
}

if($InstallTools){
  Write-Host "Installing tools into $ToolsDir ..."
  Install-Tool-PNGQuant
  Install-Tool-OptiPNG
  Install-Tool-LibWebP
}

$pngquant = Find-Tool 'pngquant'
$optipng  = Find-Tool 'optipng'
$cwebp    = Find-Tool 'cwebp'
$img2webp = Find-Tool 'img2webp'

Write-Host "Tools:" -ForegroundColor Cyan
Write-Host " pngquant:`t$pngquant"
Write-Host " optipng :`t$optipng"
Write-Host " cwebp   :`t$cwebp"
Write-Host " img2webp:`t$img2webp"

New-EnsuredDirectory $ImagesDir
New-EnsuredDirectory $OutDir

$pngs  = Get-ChildItem -Path $ImagesDir -File -Include *.png -Recurse
$webps = Get-ChildItem -Path $ImagesDir -File -Include *.webp -Recurse

Write-Host "Found $($pngs.Count) PNG and $($webps.Count) WebP files in $ImagesDir" -ForegroundColor Yellow

if (($pngs.Count + $webps.Count) -eq 0) {
  Write-Host "Nothing to process. Exiting." -ForegroundColor DarkYellow
  exit 0
}

# Compress PNGs
foreach($f in $pngs){
  $rel = $f.FullName.Substring($ImagesDir.Length).TrimStart('\\')
  $out = if($Replace){ $f.FullName } else { Join-Path $OutDir $rel }
  New-EnsuredDirectory (Split-Path -Parent $out)

  if($pngquant){
    Write-Host "[pngquant] $rel -> $(Resolve-Path (Split-Path -Parent $out) -ErrorAction SilentlyContinue)" -ForegroundColor Green
    $tmpOut = if($Replace){ $f.FullName } else { ($out -replace '\.png$','-fs8.png') }
    & $pngquant --quality ${Quality}-${Quality} --speed 1 --force --output "$tmpOut" -- "$($f.FullName)" 2>$null
    if(-not $Replace){ Copy-Item "$tmpOut" "$out" -Force }
  }
  elseif($optipng){
    Write-Host "[optipng] $rel" -ForegroundColor Green
    if($Replace){ & $optipng -o7 -- "$($f.FullName)" | Out-Null }
    else { Copy-Item "$($f.FullName)" "$out" -Force; & $optipng -o7 -- "$out" | Out-Null }
  }
  else {
    Write-Warning "No PNG compressor found. Skipping $rel"
  }
}

# Recompress static WebP
foreach($f in $webps){
  $rel = $f.FullName.Substring($ImagesDir.Length).TrimStart('\\')
  $out = if($Replace){ $f.FullName } else { Join-Path $OutDir $rel }
  New-EnsuredDirectory (Split-Path -Parent $out)

  if($cwebp){
    Write-Host "[cwebp] $rel" -ForegroundColor Green
    $tmp = [System.IO.Path]::GetTempFileName() + '.webp'
    & $cwebp -q $Quality -- "$($f.FullName)" -o "$tmp" 2>$null
    if($Replace){ Move-Item "$tmp" "$($f.FullName)" -Force } else { Move-Item "$tmp" "$out" -Force }
  }
  else {
    Write-Warning "cwebp not found. Skipping $rel"
  }
}

# Optional PNG->WebP conversion
if($ToWebP -and $cwebp){
  foreach($f in $pngs){
    $rel = [System.IO.Path]::ChangeExtension($f.FullName.Substring($ImagesDir.Length).TrimStart('\\'), '.webp')
    $out = if($Replace){ [System.IO.Path]::ChangeExtension($f.FullName, '.webp') } else { Join-Path $OutDir $rel }
    New-EnsuredDirectory (Split-Path -Parent $out)
    Write-Host "[cwebp] PNG->WebP $($f.Name)" -ForegroundColor Green
    & $cwebp -q $Quality -- "$($f.FullName)" -o "$out" 2>$null
  }
}

Write-Host "Done. Output: $OutDir" -ForegroundColor Cyan
