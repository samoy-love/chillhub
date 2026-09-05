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
    # SELF-CONTAINED ПО УМОЛЧАНИЮ.
    #
    # Раньше сборка была framework-dependent, и установщик тащил с собой
    # windowsdesktop-runtime (55.8 МБ), который пользователь ставил галочкой на
    # финальной странице. Снял галочку — получил лаунчер, который не стартует:
    # ни .NET 8, ни любой другой .NET в Windows 10/11 не предустановлен (в системе
    # есть только .NET Framework, а это другой продукт).
    #
    # Self-contained убирает этот шаг совсем. Цена — сборка вырастает с 6 МБ до
    # 166 МБ на диске (68 МБ в сжатом виде против 2.3 МБ), но инсталлятор .NET
    # больше не нужен, так что итоговый дистрибутив прибавляет всего ~10 МБ.
    # На самообновление это почти не влияет: обновления диффовые, а файлы рантайма
    # между версиями лаунчера не меняются и повторно не скачиваются.
    #
    # Отключить: -Publish:$false -SelfContained:$false
    [switch]$Publish = $true,
    [string]$Configuration = "Release",
    [string]$Csproj = "launcher/ChillHub/ChillHub.csproj",
    # Апдейтер публикуется отдельно от ChillHub (см. Publish-UpdaterAot) —
    # ему нужен собственный self-contained набор, а не то, что достаётся
    # транзитивно через ProjectReference.
    [string]$UpdaterCsproj = "updater/ChillHub.Updater.csproj",
    [string]$Installer = "scripts/installer.nsi",
    [string]$MakensisPath,
    [string]$Runtime = "win-x64",
    [switch]$SelfContained = $true,
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

# "1.2.3" / "0.0.0-ci" / "1.2.3+sha" -> "1.2.3.0": вид, который требует ресурс
# версии Windows (VIProductVersion в installer.nsi) — ровно четыре числа.
# Суффикс предрелиза в ресурс не попадает: там ему места нет. Полная строка
# версии уезжает в текстовые поля ресурса и в launcher.version как есть.
function ConvertTo-FileVersionQuad {
    param([Parameter(Mandatory = $true)][string]$Version)

    $numeric = ($Version -split '[-+]')[0]
    $parts = @($numeric -split '\.' | Where-Object { $_ -ne '' })
    foreach ($p in $parts) {
        if ($p -notmatch '^\d+$') {
            throw "Не удалось привести версию '$Version' к виду 1.2.3.0: компонент '$p' не число."
        }
    }
    while ($parts.Count -lt 4) { $parts += '0' }
    return ($parts[0..3] -join '.')
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
#
# Uninstall.exe (Б6, согласовано с апдейтером и сервером): это артефакт ВРЕМЕНИ
# УСТАНОВКИ — его создаёт сам NSIS (WriteUninstaller) уже в каталоге установки,
# в build-выводе его нет и быть не должно. Если он всё же попадёт в ZIP (проще
# всего — если кто-то ставил лаунчер прямо в каталог сборки), он окажется в
# манифесте, а апдейтер его не перезапишет: получится неустранимое расхождение
# хешей и вечный цикл обновления, ровно как с config.json/launcher.version.
# Дешевле исключить его безусловно, чем ловить это на проде.
#
# launcher.update-status — файл состояния апдейтера: он пишется рядом с
# launcher.version в каталоге установки и хранит исход последнего обновления,
# чтобы лаунчер мог показать причину неудачи. Он добавлен в preserve-правила
# апдейтера, а значит подчиняется тому же правилу, что config.json: попал в
# манифест — получил неустранимое расхождение хешей и вечный цикл обновления.
#
# ПОЛНЫЙ preserve-список на сегодня (держать синхронно с
# ChillHub.Update.PreserveMatcher.DefaultRules и со списком /x в installer.nsi):
#   config.json, launcher.version, launcher.update-status, Uninstall.exe
$script:PayloadExcludeFiles = @('config.json', 'launcher.version', 'launcher.update-status', 'Uninstall.exe')
$script:PayloadExcludeGlobs = @('*.pdb')
# Нативные библиотеки не под Windows: runtimes/linux-*, runtimes/osx-*
$script:PayloadExcludeDirGlobs = @('linux-*', 'osx-*')

function Test-PayloadExcluded {
    param([string]$RelativePath)
    $rel = ($RelativePath -replace '\\', '/').TrimStart('/')
    $leaf = Split-Path -Leaf $rel

    # ТОЧНЫЙ путь верхнего уровня, а НЕ имя файла в любом подкаталоге.
    #
    # Здесь стояло сравнение с $leaf, и оно вырезало из пакета data/config.json,
    # tools/Uninstall.exe и любой другой обычный файл сборки, которому не повезло
    # с именем. Сервер (LauncherStateFiles в builds.go) и клиент
    # (PreserveMatcher.DefaultRules) оба сравнивают точный путь верхнего уровня;
    # правило обязано быть одним на все три стороны — см. A11 в UpdatePreserve.cs.
    # Расхождение здесь давало тихую пропажу вложенного файла из сборки.
    foreach ($f in $script:PayloadExcludeFiles) {
        if ($rel -ieq $f) { return $true }
    }
    # Глобы, наоборот, работают по имени в любом подкаталоге: отладочные
    # символы не нужны нигде.
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
        # Get-Item, а не Resolve-Path: последний сохраняет 8.3-форму пути
        # (C:\Users\ALEXEY~1\...), тогда как FullName у Get-ChildItem всегда
        # длинный. Длины префиксов тогда расходятся, и Substring ниже режет
        # не там — в ZIP приезжает лишний каталог вроде 'e18e480/ChillHub.exe'.
        $srcFull = (Get-Item -LiteralPath $SourceDir).FullName.TrimEnd('\')
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

        # Постусловие: ни одного preserve-файла в готовом архиве.
        #
        # Фильтр выше уже должен был их отсеять, но проверяется именно то, что
        # уедет в админку и станет манифестом. Ошибка в Test-PayloadExcluded
        # иначе всплыла бы только на проде — вечным циклом самообновления у
        # всех пользователей, как это уже было на 1.0.2, 1.0.3 и 1.1.7.
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($OutZip)
        try {
            $leaked = @()
            foreach ($entry in $zip.Entries) {
                if ([string]::IsNullOrEmpty($entry.Name)) { continue }   # каталог
                foreach ($f in $script:PayloadExcludeFiles) {
                    if ($entry.Name -ieq $f) { $leaked += $entry.FullName }
                }
            }
        }
        finally { $zip.Dispose() }
        if ($leaked.Count -gt 0) {
            throw ("В payload-ZIP попали preserve-файлы: {0}. " -f ($leaked -join ', ')) +
                  "Они окажутся в манифесте, апдейтер их не перезапишет, и лаунчер уйдёт в вечный цикл обновления. " +
                  "Проверьте Test-PayloadExcluded и `$script:PayloadExcludeFiles."
        }

        Write-Host "Payload ZIP: $OutZip (files=$copied, excluded=$skipped)" -ForegroundColor Green
        Write-Host ("  preserve-check OK: ни одного из [{0}] в архиве нет" -f ($script:PayloadExcludeFiles -join ', ')) -ForegroundColor DarkGray
    }
    finally {
        try { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction Stop } catch {}
    }
}

# ПОДПИСЬ УСТАНОВЩИКА (docs/installer.md).
#
# Установщик не подписан, и это самая заметная для пользователя проблема
# дистрибутива: SmartScreen встречает каждого скачавшего экраном «Windows
# защитила ваш компьютер» с «Издатель: неизвестен», а часть антивирусов
# относится к неподписанному NSIS-файлу заведомо хуже.
#
# Починить это одним кодом нельзя — нужен сертификат, то есть деньги и
# проверка организации. Поэтому здесь ровно то, что можно сделать заранее:
# шаг подписи, который включается переменными окружения. Пока их нет, сборка
# идёт как раньше и ГОВОРИТ ВСЛУХ, что артефакт не подписан, — чтобы «а мы
# думали, оно подписывается» не выяснилось на релизе.
#
#   CHILLHUB_SIGN=1                     — включить подпись (без неё шаг пропущен)
#   CHILLHUB_SIGN_THUMBPRINT=<отпечаток> — сертификат в хранилище машины/пользователя
#   CHILLHUB_SIGN_TIMESTAMP_URL=<url>    — сервер меток времени (есть значение по умолчанию)
#   CHILLHUB_SIGNTOOL=<путь>             — signtool.exe, если он не находится сам
#
# Метка времени обязательна и не отключается: без неё подпись перестаёт быть
# действительной в день истечения сертификата, и уже скачанные установщики
# в этот день превращаются в «неизвестного издателя».
function Find-SignTool {
    param([string]$Explicit)

    if ($Explicit) {
        if (Test-Path -LiteralPath $Explicit) { return (Resolve-Path -LiteralPath $Explicit).Path }
        throw "CHILLHUB_SIGNTOOL указывает на '$Explicit', но такого файла нет."
    }

    $fromPath = (Get-Command signtool -ErrorAction SilentlyContinue | Select-Object -First 1).Path
    if ($fromPath) { return $fromPath }

    # Windows SDK кладёт signtool в каталог с версией; берём самый свежий x64.
    $sdkRoots = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin")
    $candidates = foreach ($root in $sdkRoots) {
        if (Test-Path -LiteralPath $root) {
            Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
                Sort-Object Name -Descending |
                ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
                Where-Object { Test-Path -LiteralPath $_ }
        }
    }
    $found = $candidates | Select-Object -First 1
    if ($found) { return $found }

    throw "signtool.exe не найден (PATH и Windows Kits\10\bin просмотрены). Укажите путь через CHILLHUB_SIGNTOOL."
}

function Invoke-SignArtifact {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ($env:CHILLHUB_SIGN -ne '1') {
        Write-Warning "Артефакт НЕ ПОДПИСАН: CHILLHUB_SIGN не выставлен. У пользователя SmartScreen покажет «Издатель: неизвестен» (см. docs/installer.md)."
        return
    }

    $thumb = $env:CHILLHUB_SIGN_THUMBPRINT
    if ([string]::IsNullOrWhiteSpace($thumb)) {
        # Молча не подписать при CHILLHUB_SIGN=1 — худший вариант: выкатка
        # решит, что подпись есть.
        throw "CHILLHUB_SIGN=1, но CHILLHUB_SIGN_THUMBPRINT пуст: подписывать нечем."
    }

    $timestampUrl = if ($env:CHILLHUB_SIGN_TIMESTAMP_URL) { $env:CHILLHUB_SIGN_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }
    $signtool = Find-SignTool -Explicit $env:CHILLHUB_SIGNTOOL

    Write-Host "Signing: $Path" -ForegroundColor Cyan
    & $signtool sign /sha1 $thumb /fd SHA256 /tr $timestampUrl /td SHA256 /v $Path
    Assert-NativeSuccess "signtool sign" $LASTEXITCODE

    # Постусловие: подпись проверяется отдельным вызовом, а не выводится из
    # кода возврата подписи. /pa — политика проверки для обычных программ.
    & $signtool verify /pa /v $Path
    Assert-NativeSuccess "signtool verify" $LASTEXITCODE
    Write-Host "Signed and verified: $Path" -ForegroundColor Green
}

function Publish-UpdaterAot {
    <#
      Публикует апдейтер как Native AOT и подменяет им framework-dependent-стиля
      .exe, который транзитивная сборка ChillHub (ProjectReference) кладёт в
      $TargetExePath.

      ПОЧЕМУ ОТДЕЛЬНЫЙ ШАГ, А НЕ СВОЙСТВО В САМОМ ПРОЕКТЕ.
      ChillHub self-contained, и апдейтер как ProjectReference наследует его
      RuntimeIdentifier/SelfContained — но НАСЛЕДУЕТ только при обычной сборке
      (`build`), а не при `publish`: транзитивная сборка копирует 4 файла
      апдейтера (exe/dll/deps.json/runtimeconfig.json) в общую папку установки,
      где рядом уже лежит self-contained-рантайм ChillHub (hostfxr.dll и
      остальное). В общей папке апдейтер выглядит рабочим. При самообновлении
      же PrepareUpdaterPayload копирует апдейтер В ИЗОЛЯЦИИ — без соседнего
      рантайма, — и апдейтер падает мгновенно, до единой строки в своём
      журнале: раздельная его же диагностика уходит в Windows Event Log
      ('hostpolicy.dll' ... not found), а не в apply-update.log, потому что
      падение происходит до того, как управление вообще доходит до Main.

      AOT публикуется отдельно и явно (`dotnet publish` с PublishAot=true
      именно для updater/ChillHub.Updater.csproj), потому что PublishAot
      действует только на этапе Publish ТОГО проекта, для которого он
      указан — транзитивная сборка через ProjectReference её не подхватывает
      ни при каких обстоятельствах, поэтому нельзя просто прописать
      <PublishAot> в csproj апдейтера и полагаться на обычную публикацию
      ChillHub.

      Результат — один нативный .exe без .dll/.deps.json/.runtimeconfig.json:
      копировать для самостоятельного запуска больше нечего, изоляция
      PrepareUpdaterPayload перестаёт быть проблемой в принципе, а не только
      в этом конкретном списке файлов.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$UpdaterCsproj,
        [Parameter(Mandatory = $true)][string]$Configuration,
        [Parameter(Mandatory = $true)][string]$Runtime,
        [Parameter(Mandatory = $true)][string]$TargetExePath
    )

    $aotOut = Join-Path ([IO.Path]::GetTempPath()) ("chillhub-updater-aot-" + [Guid]::NewGuid().ToString('N'))
    try {
        & dotnet publish $UpdaterCsproj -c $Configuration -r $Runtime -p:PublishAot=true -o $aotOut
        Assert-NativeSuccess "dotnet publish (updater, Native AOT)" $LASTEXITCODE

        $aotExe = Join-Path $aotOut "ChillHub.Updater.exe"
        if (-not (Test-Path -LiteralPath $aotExe)) {
            throw "AOT publish reported success but '$aotExe' does not exist."
        }

        # Постусловие: апдейтер обязан стартовать БЕЗ соседних .dll/.deps.json/
        # .runtimeconfig.json — ровно в тех условиях, в каких он оказывается
        # при самообновлении. Раньше self-contained-апдейтер выглядел рабочим
        # в общей папке установки (рантайм лежал рядом, общий с ChillHub) и
        # падал только в изоляции. Голая AOT-сборка такой зависимости иметь
        # не должна: лучше уронить сборку здесь, чем узнать об этом на проде
        # петлёй самообновления.
        #
        # Проверяем ИМЕННО кодом возврата, а не фактом запуска: без единого
        # аргумента апдейтер обязан дойти до Main, разобрать аргументы,
        # аккуратно отказать с ExitFatal=3 ("Missing required option --dst")
        # и завершиться. Любой другой код — процесс либо не стартовал
        # (провал резолва рантайма, тот самый сегодняшний баг), либо упал
        # иначе, чем ожидалось.
        $isolated = Join-Path ([IO.Path]::GetTempPath()) ("chillhub-updater-isolation-check-" + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $isolated -Force | Out-Null
        try {
            $isolatedExe = Join-Path $isolated "ChillHub.Updater.exe"
            Copy-Item -LiteralPath $aotExe -Destination $isolatedExe -Force
            $proc = Start-Process -FilePath $isolatedExe -NoNewWindow -PassThru -WorkingDirectory $isolated
            if (-not $proc.WaitForExit(15000)) {
                try { $proc.Kill() } catch {}
                throw "Изолированная проверка апдейтера не завершилась за 15 секунд — похоже на зависание, а не на ожидаемый быстрый отказ разбора аргументов."
            }
            if ($proc.ExitCode -ne 3) {
                throw "Изолированная проверка апдейтера вернула код $($proc.ExitCode), ожидался 3 (fatal: аргументы не переданы). " +
                      "Апдейтер либо не самодостаточен как self-contained, либо сломан иначе. Файл: $aotExe."
            }
            Write-Host "  isolation check OK: апдейтер стартует без соседних .dll/.deps.json/.runtimeconfig.json (exit=3, как и ожидалось)" -ForegroundColor DarkGray
        }
        finally {
            try { Remove-Item -LiteralPath $isolated -Recurse -Force -ErrorAction Stop } catch {}
        }

        Copy-Item -LiteralPath $aotExe -Destination $TargetExePath -Force
        $mb = [math]::Round((Get-Item -LiteralPath $TargetExePath).Length / 1MB, 1)
        Write-Host "Updater (Native AOT): $TargetExePath ($mb MB)" -ForegroundColor Green
    }
    finally {
        try { Remove-Item -LiteralPath $aotOut -Recurse -Force -ErrorAction Stop } catch {}
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

if (-not (Test-Path -LiteralPath $UpdaterCsproj)) {
    throw "CSProj not found at '$UpdaterCsproj'. Adjust -UpdaterCsproj argument."
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

# Б8, вторая половина. Версия доезжала до установщика (/DAPP_VERSION), но НЕ до
# самой сборки: в csproj версия не задана, поэтому ChillHub.exe объявлял себя
# 1.0.0 независимо от того, что мы выпускаем. Последствия:
#   * «О программе» показывает 1.0.0, если маркер launcher.version недоступен;
#   * Resolve-AppVersion ниже, вызванный БЕЗ -AppVersion, читает версию из
#     метаданных бинаря — то есть получил бы 1.0.0 и справедливо отказался
#     собирать релиз.
# Штампуем версию в сборку, когда она задана явно: тогда бинарь, установщик и
# манифест несут одно и то же число.
$versionStamp = @()
if ($AppVersion -and $AppVersion.Trim()) {
    $v = $AppVersion.Trim()

    # <Version> в csproj — то, чем сборка объявляет себя при обычном
    # `dotnet build`. Если релиз штампует другое число, значит csproj протух, и
    # любая локальная сборка врёт о своей версии. Расходиться этим значениям
    # молча нельзя: версия сравнивается с latest.json посимвольно.
    #
    # Заглушки 0.0.0* исключены: CI собирает установщик с -AppVersion 0.0.0-ci,
    # чтобы просто проверить, что он собирается. Это не релиз — 0.0.0 никогда не
    # публикуется в latest.json, — и требовать от csproj совпадения с заглушкой
    # значило бы ронять проверку сборки на каждом коммите.
    $declared = $null
    $isPlaceholder = $v -like '0.0.0*'
    $csprojXml = [xml](Get-Content -LiteralPath $Csproj -Raw)
    foreach ($pg in $csprojXml.Project.PropertyGroup) {
        if ($pg.Version) { $declared = ([string]$pg.Version).Trim() }
    }
    if ($declared -and -not $isPlaceholder -and $declared -ne $v) {
        throw @"
Версия релиза ($v) не совпадает с <Version> в csproj ($declared).

Эти числа обязаны совпадать: <Version> — источник версии для обычной сборки, и
протухшее значение означает, что локальный билд объявляет себя не тем, что он есть.

Как чинить: поднять <Version> в launcher/ChillHub/ChillHub.csproj до $v.
"@
    }

    # AssemblyVersion требует строго числовой четырёхкомпонентный вид, поэтому
    # суффиксы вида "-rc1" отдаём только в информационную версию.
    $numeric = ($v -split '[-+]')[0]
    $versionStamp = @("-p:Version=$v", "-p:InformationalVersion=$v", "-p:FileVersion=$numeric", "-p:AssemblyVersion=$numeric")
    Write-Host "Stamping build version: $v" -ForegroundColor Cyan
}

if ($Publish) {
    Write-Host "[2/3] Publishing self-contained ($Configuration, $Runtime, SelfContained=$SelfContained)..." -ForegroundColor Cyan
    $sc = if ($SelfContained) { "true" } else { "false" }
    & dotnet publish $Csproj -c $Configuration -r $Runtime --self-contained $sc @versionStamp
    Assert-NativeSuccess "dotnet publish" $LASTEXITCODE
    # Compute publish output path (informational)
    $ProjectDir = Split-Path -Parent $Csproj
    $PublishDir = Join-Path $ProjectDir "bin/$Configuration/net8.0-windows/$Runtime/publish"
    Write-Host "Publish output: $PublishDir" -ForegroundColor Cyan
    $BuildOutputDir = $PublishDir
} else {
    Write-Host "[2/3] Building ($Configuration)..." -ForegroundColor Cyan
    & dotnet build $Csproj -c $Configuration @versionStamp
    Assert-NativeSuccess "dotnet build" $LASTEXITCODE
    $ProjectDir = Split-Path -Parent $Csproj
    $BuildOutputDir = Join-Path $ProjectDir "bin/$Configuration/net8.0-windows"
}

# A build that "succeeded" but produced no output directory is a failure too.
if (-not (Test-Path -LiteralPath $BuildOutputDir)) {
    throw "Build output directory not found at '$BuildOutputDir' even though the build reported success."
}

# A-AOT. Апдейтер, скопированный сюда транзитивно вместе с ChillHub, self-contained
# только на бумаге (см. Publish-UpdaterAot) — заменяем его настоящим, самостоятельным.
# Только когда ChillHub сам self-contained: framework-dependent сборка ChillHub уже
# была рабочей без этого (апдейтер полагается на глобальный .NET, который в этом
# режиме и так требуется).
if ($Publish -and $SelfContained) {
    Write-Host "[2c/3] Publishing updater as Native AOT..." -ForegroundColor Cyan
    $updaterTargetExe = Join-Path $BuildOutputDir "ChillHub.Updater.exe"
    Publish-UpdaterAot -UpdaterCsproj $UpdaterCsproj -Configuration $Configuration -Runtime $Runtime -TargetExePath $updaterTargetExe
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

# WebView2 Evergreen Bootstrapper.
#
# scripts/Redist/ лежит в .gitignore, и CI его ничем не наполнял: из-за
# `File /nonfatal` в installer.nsi сборка молча проходила БЕЗ зависимостей, и
# установщик, который выкладывается на сайт, не содержал ни .NET, ни WebView2.
# Пользователь получал лаунчер, который не запускается, без единой подсказки.
#
# Bootstrapper весит ~2 МБ (офлайн-пакет — 183 МБ), поэтому его дешевле забирать
# на каждой сборке, чем держать в репозитории. Если сети нет — не валимся:
# установщик соберётся без него, ровно как раньше, но об этом будет сказано вслух.
function Ensure-WebView2Bootstrapper {
    param(
        [Parameter(Mandatory = $true)][string]$RedistDir,
        # ОТСУТСТВИЕ ЗАВИСИМОСТИ В РЕЛИЗЕ — ОШИБКА, А НЕ ПРЕДУПРЕЖДЕНИЕ.
        #
        # Здесь был только Write-Warning, а в installer.nsi — `File /nonfatal`.
        # Вдвоём это ровно та комбинация, из-за которой однажды уже выложили
        # установщик без зависимостей вообще: сеть моргнула на раннере, сборка
        # прошла зелёной, файл уехал на сайт, и у тех, у кого WebView2 нет,
        # молча не открываются новости. Предупреждение в логе УСПЕШНОЙ сборки
        # не читает никто — на то оно и успешная.
        #
        # Для нерелизных сборок (0.0.0-ci, локальная проверка «собирается ли»)
        # остаётся предупреждение: там артефакт никуда не публикуется, и ронять
        # проверку из-за сети незачем.
        [switch]$Required
    )

    $target = Join-Path $RedistDir 'MicrosoftEdgeWebview2Setup.exe'
    if (Test-Path -LiteralPath $target) {
        $mb = [math]::Round((Get-Item -LiteralPath $target).Length / 1MB, 1)
        Write-Host "WebView2 bootstrapper: уже на месте ($mb МБ)" -ForegroundColor Cyan
        return
    }

    New-Item -ItemType Directory -Force -Path $RedistDir | Out-Null
    # Официальный вечный редирект Microsoft на актуальный bootstrapper.
    $url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703'
    try {
        Write-Host "WebView2 bootstrapper: качаю..." -ForegroundColor Cyan
        $tmp = "$target.tmp"
        Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing -TimeoutSec 120
        # Проверяем, что приехал исполняемый файл, а не страница-заглушка:
        # HTML вместо .exe молча превратился бы в неработающую зависимость.
        $head = [System.IO.File]::ReadAllBytes($tmp)[0..1]
        if ($head[0] -ne 0x4D -or $head[1] -ne 0x5A) {
            Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
            throw "по ссылке пришёл не исполняемый файл (нет сигнатуры MZ)"
        }
        Move-Item -LiteralPath $tmp -Destination $target -Force
        $mb = [math]::Round((Get-Item -LiteralPath $target).Length / 1MB, 1)
        Write-Host "WebView2 bootstrapper: готов ($mb МБ)" -ForegroundColor Green
    }
    catch {
        if ($Required) {
            throw @"
WebView2 bootstrapper не скачан: $($_.Exception.Message)

Это РЕЛИЗНАЯ сборка, а без bootstrapper'а установщик выложится без
зависимости: у всех, у кого нет WebView2 Runtime (в основном Windows 10 без
свежего Edge), в лаунчере молча не откроются новости.

Как чинить: повторить сборку, когда сеть доступна, либо положить файл руками в
$RedistDir (официальная ссылка: https://go.microsoft.com/fwlink/p/?LinkId=2124703).
"@
        }
        Write-Warning "WebView2 bootstrapper не скачан: $($_.Exception.Message)"
        Write-Warning "Сборка нерелизная — продолжаю без него. На машине без WebView2 не откроются новости."
    }
}

# Резолвим версию ДО поиска makensis и до подготовки зависимостей: если версии
# нет, падать надо сразу, а не после того, как найден компилятор и созданы
# выходные каталоги. Заодно версия отвечает на вопрос, релиз это или проверка
# сборки, — от этого зависит, ошибка или предупреждение отсутствующий WebView2.
$resolvedVersion = Resolve-AppVersion -Explicit $AppVersion -BuildOutputDir $BuildOutputDir
$isReleaseBuild = -not ($resolvedVersion -like '0.0.0*')

Ensure-WebView2Bootstrapper -RedistDir (Join-Path (Split-Path $Installer -Parent) 'Redist') -Required:$isReleaseBuild

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
    # ГЛУШИТСЯ ИМЕННО SetCompressor, А НЕ SetCompress.
    #
    # Раньше здесь стоял только `/XSetCompress off`, и ключ не делал ничего:
    # installer.nsi объявляет `SetCompressor /SOLID lzma`, а в solid-режиме вся
    # полезная нагрузка жмётся ОДНИМ потоком, и пофайловый SetCompress на него
    # не влияет. Замер: шаг «Собрать установщик» с ключом и без него — те же
    # 74 секунды.
    #
    # Переключается это define-ом, а не ключом: /XSetCompressor до объявления
    # в .nsi не достаёт, а /XSetCompress в whole-режиме игнорируется вовсе
    # (предупреждение 8021). Сам выбор компрессора живёт в installer.nsi.
    $nsisArgs += @('/DFAST_COMPRESS')
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
# Та же версия в формате ресурса версии Windows: ровно четыре числовых
# компонента. Ресурсу нельзя отдать ни "1.2.3", ни "0.0.0-ci" — makensis такое
# не примет, поэтому приведение живёт здесь, а не в .nsi (в NSIS нет средств
# разобрать строку версии на этапе компиляции).
$nsisArgs += @("/DAPP_VERSION_NUMERIC=$(ConvertTo-FileVersionQuad $resolvedVersion)")
$nsisArgs += @("$installerPath")

# Старый файл удаляется ДО компиляции — так же, как это делает ZIP-ветка выше.
# generated_downloads/ лежит в .gitignore и не чистится, поэтому проверка
# Test-Path ниже без этого удаления отвечала на вопрос «файл есть?», а не
# «makensis его написал?»: выйди компилятор нулём, не тронув OutFile, — и
# подписался бы, а потом и опубликовался .exe прошлой сборки, со своей
# прежней версией внутри.
$setupExe = Join-Path $outDir "ChillHub-Setup.exe"
if (Test-Path -LiteralPath $setupExe) { Remove-Item -LiteralPath $setupExe -Force }

& "$makensis" @nsisArgs
Assert-NativeSuccess "makensis" $LASTEXITCODE

# makensis can also exit 0 without writing the file (e.g. OutFile pointing
# somewhere unexpected). Verify the artefact before declaring success.
if (-not (Test-Path -LiteralPath $setupExe)) {
    throw "makensis reported success but '$setupExe' does not exist."
}
Invoke-SignArtifact -Path $setupExe

$setupSize = (Get-Item -LiteralPath $setupExe).Length
Write-Host "Done. Installer: $setupExe ($([math]::Round($setupSize / 1MB, 1)) MB)" -ForegroundColor Green

