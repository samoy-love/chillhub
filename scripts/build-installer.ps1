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
    # Версия, которая попадёт в launcher.version внутри установки (Б8).
    #
    # Раньше версия была захардкожена в scripts/installer.nsi (!define APP_VERSION
    # "1.1.7"), а сюда не передавалась вообще. Любая сборка объявляла себя 1.1.7,
    # видела на сервере более свежий latest.json и уходила в бесконечный цикл
    # обновления. Теперь версия обязана прийти снаружи; см. Resolve-AppVersion —
    # молчаливого дефолта нет ни здесь, ни в .nsi.
    [string]$AppVersion,
    # Собрать ZIP полезной нагрузки для самообновления (для загрузки в админку)
    [switch]$PackageZip,
    # Пропустить компиляцию NSIS (полезно, когда нужен только ZIP)
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

# $ErrorActionPreference DOES NOT APPLY TO NATIVE EXECUTABLES.
#
# It only governs PowerShell cmdlets. `dotnet build` and `makensis` are ordinary
# .exe files: when they fail they set $LASTEXITCODE and PowerShell carries on to
# the next line as if nothing happened. That is how this script used to print a
# green "Done." after a failed compile — and, in CI, how a broken build could
# still upload whatever ChillHub-Setup.exe was left over from a previous run.
#
# Every native invocation below is followed by Assert-NativeSuccess.
#
# The exit code is passed EXPLICITLY rather than defaulted to $LASTEXITCODE
# inside the function: a default is evaluated in the function's own scope, and
# if $LASTEXITCODE happens to be unset there it silently coerces to 0 — a guard
# that always passes is worse than no guard. An explicit $null is treated as
# "the executable never ran", which is also a failure.
function Assert-NativeSuccess {
    param(
        [Parameter(Mandatory = $true)][string]$What,
        [Parameter(Mandatory = $true)][AllowNull()][object]$ExitCode
    )
    if ($null -eq $ExitCode -or "$ExitCode" -eq '') {
        throw "$What did not report an exit code - the executable probably never ran."
    }
    if ([int]$ExitCode -ne 0) {
        throw "$What failed with exit code $ExitCode."
    }
}

# Версия установки (Б8).
#
# Порядок источников — явный параметр, затем метаданные собранного ChillHub.exe.
# Никакого «ну ладно, поставим 1.0.0»: версия из launcher.version сравнивается с
# manifests/launcher/latest.json ПОСИМВОЛЬНО, поэтому неверное значение не
# деградирует, а зацикливает обновление у каждого, кто поставил такой билд.
# Лучше уронить сборку здесь, чем выпустить неправильно помеченный инсталлятор.
#
# 1.0.0 из метаданных считается «версия не задана», а не версией: launcher/ChillHub
# не объявляет <Version> в csproj, поэтому 1.0.0 — это дефолт .NET SDK, который
# получается сам собой и ничего не означает.
function Resolve-AppVersion {
    param(
        [AllowNull()][string]$Explicit,
        [Parameter(Mandatory = $true)][string]$BuildOutputDir
    )

    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        $v = $Explicit.Trim()
        Write-Host "App version: $v (from -AppVersion)" -ForegroundColor Cyan
        return $v
    }

    $exe = Join-Path $BuildOutputDir 'ChillHub.exe'
    $fromExe = $null
    if (Test-Path -LiteralPath $exe) {
        $info = (Get-Item -LiteralPath $exe).VersionInfo
        # ProductVersion несёт InformationalVersion (может быть "1.2.3+sha"),
        # FileVersion — четырёхкомпонентный. Берём первое непустое и режем до
        # трёх компонентов, потому что ровно в таком виде версия лежит в
        # latest.json.
        foreach ($cand in @($info.ProductVersion, $info.FileVersion)) {
            if ([string]::IsNullOrWhiteSpace($cand)) { continue }
            $m = [regex]::Match($cand.Trim(), '^\d+\.\d+\.\d+')
            if ($m.Success) { $fromExe = $m.Value; break }
        }
    }

    if ($fromExe -and $fromExe -ne '1.0.0') {
        Write-Host "App version: $fromExe (from $exe)" -ForegroundColor Cyan
        return $fromExe
    }

    $seen = if ($fromExe) { "'$fromExe' (это дефолт .NET SDK, а не заданная версия)" } else { "метаданные версии отсутствуют" }
    throw @"
Не удалось определить версию сборки: $seen.

Версия обязана быть явной — она пишется в launcher.version и сравнивается с
manifests/launcher/latest.json. Ошибочное значение = бесконечный цикл
самообновления у всех, кто поставит этот билд.

Как чинить (любой из вариантов):
  * передать версию: .\scripts\build-installer.ps1 -AppVersion 1.1.8
  * либо задать <Version> в launcher/ChillHub/ChillHub.csproj, тогда версия
    будет браться из собранного ChillHub.exe автоматически.
"@
}

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
Assert-NativeSuccess "dotnet restore" $LASTEXITCODE

if ($Publish) {
    Write-Host "[2/3] Publishing self-contained ($Configuration, $Runtime, SelfContained=$SelfContained)..." -ForegroundColor Cyan
    $sc = if ($SelfContained) { "true" } else { "false" }
    & dotnet publish $Csproj -c $Configuration -r $Runtime --self-contained $sc
    Assert-NativeSuccess "dotnet publish" $LASTEXITCODE
    # Compute publish output path (informational)
    $ProjectDir = Split-Path -Parent $Csproj
    $PublishDir = Join-Path $ProjectDir "bin/$Configuration/net8.0-windows/$Runtime/publish"
    Write-Host "Publish output: $PublishDir" -ForegroundColor Cyan
    $BuildOutputDir = $PublishDir
} else {
    Write-Host "[2/3] Building ($Configuration)..." -ForegroundColor Cyan
    & dotnet build $Csproj -c $Configuration
    Assert-NativeSuccess "dotnet build" $LASTEXITCODE
    $ProjectDir = Split-Path -Parent $Csproj
    $BuildOutputDir = Join-Path $ProjectDir "bin/$Configuration/net8.0-windows"
}

# A build that "succeeded" but produced no output directory is a failure too.
if (-not (Test-Path -LiteralPath $BuildOutputDir)) {
    throw "Build output directory not found at '$BuildOutputDir' even though the build reported success."
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

# Резолвим версию ДО поиска makensis: если версии нет, падать надо сразу, а не
# после того, как найден компилятор и созданы выходные каталоги.
$resolvedVersion = Resolve-AppVersion -Explicit $AppVersion -BuildOutputDir $BuildOutputDir

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
# Package exactly what we just built. installer.nsi defaults to
# bin\Release\net8.0-windows when this is not passed, which is why
# `-Configuration Debug` used to compile Debug and then quietly package a stale
# Release directory.
$payloadDirFull = (Resolve-Path -LiteralPath $BuildOutputDir).Path.TrimEnd('\')
Write-Host "NSIS payload dir: $payloadDirFull" -ForegroundColor Cyan
$nsisArgs += @("/DPAYLOAD_DIR=$payloadDirFull")
# Версия установки (Б8). installer.nsi без /DAPP_VERSION не компилируется —
# см. !ifndef APP_VERSION там и Resolve-AppVersion выше.
$nsisArgs += @("/DAPP_VERSION=$resolvedVersion")
$nsisArgs += @("$installerPath")

& "$makensis" @nsisArgs
Assert-NativeSuccess "makensis" $LASTEXITCODE

# makensis can also exit 0 without writing the file (e.g. OutFile pointing
# somewhere unexpected). Verify the artefact before declaring success.
$setupExe = Join-Path $outDir "ChillHub-Setup.exe"
if (-not (Test-Path -LiteralPath $setupExe)) {
    throw "makensis reported success but '$setupExe' does not exist."
}
$setupSize = (Get-Item -LiteralPath $setupExe).Length
Write-Host "Done. Installer: $setupExe ($([math]::Round($setupSize / 1MB, 1)) MB)" -ForegroundColor Green

