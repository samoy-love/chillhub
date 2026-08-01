# =============================================================================
# Сборка инсталлятора ChillHub (NSIS) + подготовка «чистого» пакета самообновления.
#
# ГДЕ СОБИРАЕТСЯ ZIP ДЛЯ САМООБНОВЛЕНИЯ (важно, A3/A9):
#   Этот скрипт НЕ формирует манифест content/manifests/launcher/<version>.json.
#   Манифест строит серверная часть (Go-бэкенд) из ZIP-архива, который релиз-инженер
#   загружает через админку (server/admin_ui -> загрузка сборки лаунчера).
#   Поэтому здесь мы делаем максимум возможного на своей стороне: ключ -PackageZip
#   собирает архив из build-вывода, вычищая файлы, которых в манифесте быть не должно
#   (см. New-LauncherPayload). Загружать в админку следует именно этот архив —
#   иначе в манифест снова попадут config.json / launcher.version, апдейтер их не
#   перезапишет (preserve), и лаунчер уйдёт в бесконечный цикл обновления.
# =============================================================================
param(
    [switch]$Publish = $false,
    [string]$Configuration = "Release",
    [string]$Csproj = "launcher/ChillHub/ChillHub.csproj",
    [string]$Installer = "scripts/installer.nsi",
    [string]$MakensisPath,
    [string]$Runtime = "win-x64",
    [switch]$SelfContained,
    [switch]$NoCompress,
    # Собрать ZIP полезной нагрузки для самообновления (для загрузки в админку)
    [switch]$PackageZip,
    # Пропустить компиляцию NSIS (полезно, когда нужен только ZIP)
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

# Исключения пакета лаунчера. Держать синхронно с:
#   - ChillHub.Update.PreserveMatcher.DefaultRules (updater/UpdatePreserve.cs)
#   - списком /x в scripts/installer.nsi
$script:PayloadExcludeFiles = @('config.json', 'launcher.version')
$script:PayloadExcludeGlobs = @('*.pdb')
# Нативные библиотеки не под Windows: runtimes/linux-*, runtimes/osx-*
$script:PayloadExcludeDirGlobs = @('linux-*', 'osx-*')

function Test-PayloadExcluded {
    param([string]$RelativePath)
    $rel = $RelativePath -replace '\\', '/'
    $leaf = Split-Path -Leaf $rel

    foreach ($f in $script:PayloadExcludeFiles) {
        if ($leaf -ieq $f) { return $true }
    }
    foreach ($g in $script:PayloadExcludeGlobs) {
        if ($leaf -like $g) { return $true }
    }
    foreach ($seg in ($rel -split '/')) {
        foreach ($g in $script:PayloadExcludeDirGlobs) {
            if ($seg -like $g) { return $true }
        }
    }
    return $false
}

function New-LauncherPayload {
    <#
      Готовит staging-копию build-вывода без файлов, которых не должно быть в манифесте,
      и упаковывает её в ZIP. Архив плоский (без корневой папки) — так его понимает
      апдейтер без strip-prefix.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$SourceDir,
        [Parameter(Mandatory = $true)][string]$OutZip
    )

    if (-not (Test-Path -LiteralPath $SourceDir)) {
        throw "Build output not found at '$SourceDir'. Build first (or pass -Publish)."
    }

    $staging = Join-Path ([IO.Path]::GetTempPath()) ("chillhub-payload-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    try {
        $srcFull = (Resolve-Path -LiteralPath $SourceDir).Path.TrimEnd('\')
        $skipped = 0
        $copied = 0
        Get-ChildItem -LiteralPath $srcFull -Recurse -File | ForEach-Object {
            $rel = $_.FullName.Substring($srcFull.Length).TrimStart('\')
            if (Test-PayloadExcluded -RelativePath $rel) {
                $skipped++
                Write-Host "  exclude: $rel" -ForegroundColor DarkGray
                return
            }
            $dest = Join-Path $staging $rel
            $destDir = Split-Path -Parent $dest
            if (-not (Test-Path -LiteralPath $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
            Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
            $copied++
        }

        $outDirZip = Split-Path -Parent $OutZip
        if ($outDirZip -and -not (Test-Path -LiteralPath $outDirZip)) { New-Item -ItemType Directory -Path $outDirZip -Force | Out-Null }
        if (Test-Path -LiteralPath $OutZip) { Remove-Item -LiteralPath $OutZip -Force }
        Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $OutZip -CompressionLevel Optimal
        Write-Host "Payload ZIP: $OutZip (files=$copied, excluded=$skipped)" -ForegroundColor Green
    }
    finally {
        try { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction Stop } catch {}
    }
}

# Ensure Unicode I/O
try {
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($true)
    [Console]::InputEncoding  = [System.Text.UTF8Encoding]::new($true)
} catch {}

# Preflight: dotnet SDK and csproj path
try {
    $dc = Get-Command dotnet -ErrorAction Stop
    $dv = (& dotnet --version)
    if ($dv) { Write-Host "dotnet: $dv ($($dc.Path))" -ForegroundColor DarkCyan }
} catch { throw "dotnet SDK not found. Please install .NET 8 SDK and ensure 'dotnet' is in PATH." }

if (-not (Test-Path -LiteralPath $Csproj)) {
    throw "CSProj not found at '$Csproj'. Adjust -Csproj argument."
}

function Find-Makensis {
    param([string]$ExplicitPath)
    # Helper to expand a directory to candidate exe paths
    function Get-ExeFromDir([string]$dir) {
        if (-not $dir) { return $null }
        $exe1 = Join-Path $dir "makensis.exe"
        $exe2 = Join-Path $dir "makensisw.exe"
        return @($exe1, $exe2) | Where-Object { Test-Path $_ }
    }

    # 1) If explicit path provided, accept file; if it's a directory, search inside for exe
    if ($ExplicitPath) {
        if (Test-Path $ExplicitPath) {
            if ((Get-Item $ExplicitPath) -is [System.IO.DirectoryInfo]) {
                $fromDir = Get-ExeFromDir -dir $ExplicitPath
                if ($fromDir -and $fromDir.Count -gt 0) { return $fromDir[0] }
            } else {
                return $ExplicitPath
            }
        }
    }

    # 2) Try PATH (makensis or makensisw)
    $fromPath = @(
        (Get-Command makensis -ErrorAction SilentlyContinue | Select-Object -First 1).Path,
        (Get-Command makensisw -ErrorAction SilentlyContinue | Select-Object -First 1).Path
    ) | Where-Object { $_ -and (Test-Path $_) }
    if ($fromPath -and $fromPath.Count -gt 0) { return $fromPath[0] }

    # 3) Well-known install directories
    $knownDirs = @(
        "C:\Program Files (x86)\NSIS",
        "C:\Program Files\NSIS"
    )
    $fromKnown = $knownDirs | ForEach-Object { Get-ExeFromDir -dir $_ } | Where-Object { $_ }
    if ($fromKnown -and $fromKnown.Count -gt 0) { return $fromKnown[0] }

    # 4) Registry lookup
    $regKeys = @(
        'HKLM:\SOFTWARE\NSIS',
        'HKLM:\SOFTWARE\WOW6432Node\NSIS'
    )
    foreach ($rk in $regKeys) {
        try {
            $installDir = (Get-ItemProperty -Path $rk -ErrorAction SilentlyContinue).InstallDir
            if (-not $installDir) { $installDir = (Get-ItemProperty -Path $rk -ErrorAction SilentlyContinue).'(default)' }
            $fromReg = Get-ExeFromDir -dir $installDir
            if ($fromReg -and $fromReg.Count -gt 0) { return $fromReg[0] }
        } catch { }
    }

    throw "NSIS not found. Install NSIS 3.x (Typical) or supply -MakensisPath to makensis.exe. Looked in PATH and default locations like 'C:\\Program Files (x86)\\NSIS'."
}

Write-Host "[1/3] Restoring .NET packages..." -ForegroundColor Cyan
& dotnet restore $Csproj

if ($Publish) {
    Write-Host "[2/3] Publishing self-contained ($Configuration, $Runtime, SelfContained=$SelfContained)..." -ForegroundColor Cyan
    $sc = if ($SelfContained) { "true" } else { "false" }
    & dotnet publish $Csproj -c $Configuration -r $Runtime --self-contained $sc
    # Compute publish output path (informational)
    $ProjectDir = Split-Path -Parent $Csproj
    $PublishDir = Join-Path $ProjectDir "bin/$Configuration/net8.0-windows/$Runtime/publish"
    Write-Host "Publish output: $PublishDir" -ForegroundColor Cyan
    $BuildOutputDir = $PublishDir
} else {
    Write-Host "[2/3] Building ($Configuration)..." -ForegroundColor Cyan
    & dotnet build $Csproj -c $Configuration
    $ProjectDir = Split-Path -Parent $Csproj
    $BuildOutputDir = Join-Path $ProjectDir "bin/$Configuration/net8.0-windows"
}

# A3/A9: чистый ZIP полезной нагрузки для самообновления (загружается в админку).
if ($PackageZip) {
    Write-Host "[2b/3] Packaging self-update payload ZIP..." -ForegroundColor Cyan
    $zipOutDir = Join-Path (Split-Path $Installer -Parent) "generated_downloads"
    $zipPath = Join-Path $zipOutDir "ChillHub-launcher-payload.zip"
    New-LauncherPayload -SourceDir $BuildOutputDir -OutZip $zipPath
}

if ($SkipInstaller) {
    Write-Host "SkipInstaller: NSIS compilation skipped." -ForegroundColor Yellow
    return
}

$makensis = Find-Makensis -ExplicitPath $MakensisPath
# Resolve to full path and ensure string type
try { $makensis = (Resolve-Path -LiteralPath $makensis).Path } catch { }
if (-not $makensis -or -not (Test-Path $makensis)) { throw "makensis.exe not found or not accessible at '$MakensisPath'" }

# Resolve installer path as well
try { $installerPath = (Resolve-Path -LiteralPath $Installer).Path } catch { $installerPath = $Installer }

Write-Host "[3/3] Compiling NSIS installer with: `$makensis=`"$makensis`"; installer=`"$installerPath`"" -ForegroundColor Cyan

# Ensure prereqs dir exists (optional)
$prereqsDir = Join-Path (Split-Path $Installer -Parent) "prereqs"
if (!(Test-Path $prereqsDir)) { New-Item -ItemType Directory -Path $prereqsDir | Out-Null }

# Ensure NSIS OutFile directory exists (installer.nsi uses OutFile "generated_downloads\\ChillHub-Setup.exe")
$installerDir = Split-Path -Parent $installerPath
$outDir = Join-Path $installerDir "generated_downloads"
if (!(Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

# Build NSIS args (default verbosity)
$nsisArgs = @('/INPUTCHARSET', 'UTF8')
if ($NoCompress) {
    Write-Host "NSIS: building without compression (fast dev build)" -ForegroundColor Yellow
    # Inject a directive override: SetCompress off (at compile-time)
    $nsisArgs += @('/XSetCompress off')
}
$nsisArgs += @("$installerPath")

& "$makensis" @nsisArgs

Write-Host "Done. Look for the generated installer (ChillHub-Setup.exe) near $Installer" -ForegroundColor Green

