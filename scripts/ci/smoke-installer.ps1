# =============================================================================
# Smoke-проверка собранного установщика: тихая установка -> проверки -> тихое
# удаление -> проверки.
#
# ЗАЧЕМ. Установщик собирался в CI и НЕ ЗАПУСКАЛСЯ ни разу. Проверялось ровно
# одно: что makensis отработал и файл появился. Всё остальное — что файлы
# доезжают до каталога установки, что launcher.version совпадает с версией
# сборки, что запись в «Установка и удаление программ» появляется и исчезает,
# что в установку не попали preserve-файлы, — выяснялось у пользователя.
#
# Установщик — единственный артефакт проекта, у которого не было ни одного
# автоматического прогона, хотя ошибка в нём стоит дороже всего: человек не
# может ни поставить лаунчер, ни удалить его.
#
# ЧТО ЭТО НЕ ПРОВЕРЯЕТ. Лаунчер здесь не запускается: раннер CI — машина без
# графической сессии, а Chill Hub — WPF-приложение. Проверяется установка, а не
# работоспособность самой программы.
# =============================================================================
[CmdletBinding()]
param(
    [string]$Setup = "scripts/generated_downloads/ChillHub-Setup.exe",
    # Версия, которую эта сборка обязана записать в launcher.version и в реестр.
    # Совпадение проверяется ПОСИМВОЛЬНО: именно так его сравнивает лаунчер с
    # manifests/launcher/latest.json, и именно расхождение здесь давало вечный
    # цикл самообновления в 1.0.2, 1.0.3, 1.1.7 и 1.1.8.
    [Parameter(Mandatory = $true)][string]$ExpectedVersion,
    [string]$InstallDir,
    [switch]$NativeCleanup
)

$ErrorActionPreference = 'Stop'

if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Installer smoke requires a disposable GitHub-hosted runner.'
}

$UninstKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ChillHub'
$AppKey = 'HKCU:\Software\ChillHub\Install'

$script:Failures = @()

function Test-Assert {
    param([Parameter(Mandatory = $true)][string]$What, [Parameter(Mandatory = $true)][bool]$Condition, [string]$Detail)
    if ($Condition) {
        Write-Host "[smoke] PASS $What" -ForegroundColor Green
    }
    else {
        Write-Host "[smoke] FAIL $What$(if ($Detail) { " -> $Detail" })" -ForegroundColor Red
        $script:Failures += $What
    }
}

# Тихая установка принимает каталог только через /D=<путь>, и у этого ключа два
# жёстких правила NSIS: он должен быть ПОСЛЕДНИМ аргументом и НЕ ДОЛЖЕН быть
# закавычен (всё до конца строки считается путём). PowerShell же закавычивает
# аргумент с пробелом сам, молча ломая ключ, поэтому путь с пробелом сюда
# передавать нельзя — лучше отказаться сразу и внятно.
function Invoke-SilentInstall {
    param([string]$SetupExe, [string]$Dir, [string]$GamesDir)
    if ($Dir -match '\s') {
        throw "Каталог установки '$Dir' содержит пробел: ключ NSIS /D= таким путём пользоваться не умеет. Передайте -InstallDir без пробелов."
    }
    # /D= обязан быть последним — всё, что после него, NSIS считает путём.
    $args = @('/S')
    if ($GamesDir) { $args += "/GAMESDIR=$GamesDir" }
    $args += "/D=$Dir"
    $p = Start-Process -FilePath $SetupExe -ArgumentList $args -PassThru -Wait
    return $p.ExitCode
}

$setupPath = (Resolve-Path -LiteralPath $Setup).Path
$setupMb = [math]::Round((Get-Item -LiteralPath $setupPath).Length / 1MB, 1)
Write-Host "[smoke] установщик: $setupPath ($setupMb МБ), ожидаемая версия: $ExpectedVersion" -ForegroundColor Cyan

if (-not $InstallDir) {
    $InstallDir = Join-Path ([IO.Path]::GetTempPath()) ("chillhub-smoke-" + [Guid]::NewGuid().ToString('N'))
}
$InstallDir = [IO.Path]::GetFullPath($InstallDir, (Get-Location).ProviderPath)
if (Get-Item -LiteralPath $InstallDir -Force -ErrorAction SilentlyContinue) {
    throw "Каталог для проверки должен быть новым: $InstallDir"
}
$createdInstallDir = $null

$appDataDir = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'ChillHub'
$localAppDataDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ChillHub'
$configPath = Join-Path $appDataDir 'config.json'

# Установщик пишет в профиль раннера. Снимки создаются до первой записи.
$snapshotDir = Join-Path ([IO.Path]::GetTempPath()) ("chillhub-smoke-backup-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $snapshotDir | Out-Null

# Ярлыки называются по видимому имени продукта — «Chill Hub», а не по имени exe.
$desktopLnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Chill Hub.lnk'
$startMenuDir = Join-Path ([Environment]::GetFolderPath('Programs')) 'Chill Hub'
$legacyDesktopLnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'ChillHub.lnk'
$legacyStartMenuDir = Join-Path ([Environment]::GetFolderPath('Programs')) 'ChillHub'

function Save-Snapshot {
    param([string]$Path, [string]$Name)
    if (Test-Path -LiteralPath $Path) {
        Copy-Item -LiteralPath $Path -Destination (Join-Path $snapshotDir $Name) -Recurse -Force
        return $true
    }
    return $false
}

function Restore-Snapshot {
    param([string]$Path, [string]$Name, [bool]$Existed)
    $saved = Join-Path $snapshotDir $Name
    if ($Existed) {
        $null = Get-Item -LiteralPath $saved
    }
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    if ($Existed) {
        Copy-Item -LiteralPath $saved -Destination $Path -Recurse -Force
    }
}

$shortcutSnapshots = @(
    @{ Path = $desktopLnk; Name = 'Chill Hub.lnk' }
    @{ Path = $startMenuDir; Name = 'StartMenu' }
    @{ Path = $legacyDesktopLnk; Name = 'ChillHub.lnk' }
    @{ Path = $legacyStartMenuDir; Name = 'LegacyStartMenu' }
)
foreach ($shortcut in $shortcutSnapshots) {
    $shortcut.Existed = Save-Snapshot -Path $shortcut.Path -Name $shortcut.Name
}

$stashed = @{}

function Move-Aside {
    param([string]$Path, [string]$Name)
    if (-not (Test-Path -LiteralPath $Path)) { return $true }
    $to = Join-Path $snapshotDir $Name
    try {
        Move-Item -LiteralPath $Path -Destination $to -Force -ErrorAction Stop
        $stashed[$Path] = $to
        return $true
    }
    catch {
        Write-Warning "не удалось увести '$Path' в сторону: $($_.Exception.Message)"
        return $false
    }
}

function Invoke-RegistrySnapshot {
    param([string[]]$Arguments)
    $regExe = Join-Path ([Environment]::SystemDirectory) 'reg.exe'
    & $regExe @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Ошибка reg.exe $($Arguments[0]), код $LASTEXITCODE."
    }
}

function Restore-RegistrySnapshot {
    param([string]$Key, [string]$File)
    if ($File -and -not (Test-Path -LiteralPath $File -PathType Leaf)) {
        throw "Снимок раздела $Key не найден: $File"
    }
    if (Test-Path -LiteralPath $Key) {
        Remove-Item -LiteralPath $Key -Recurse -Force
    }
    if ($File) {
        Invoke-RegistrySnapshot -Arguments @('import', $File)
    }
}

$registrySnapshots = @{}
foreach ($key in $UninstKey, $AppKey) {
    $registrySnapshots[$key] = $null
    if (Test-Path -LiteralPath $key) {
        $file = Join-Path $snapshotDir ("$(Split-Path -Leaf $key).reg")
        Invoke-RegistrySnapshot -Arguments @('export', ($key -replace '^HKCU:', 'HKCU'), $file)
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw "reg.exe не создал снимок раздела $key."
        }
        $registrySnapshots[$key] = $file
    }
}

$gamesDir = Join-Path ([IO.Path]::GetTempPath().TrimEnd('\')) ("chillhub-smoke-games-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$installerStarted = $false

try {
    # Без -Force создание остановится, если каталог успел появиться после проверки.
    $createdInstallDir = (New-Item -ItemType Directory -Path $InstallDir).FullName
    if ($InstallDir -match '\s') {
        $fso = New-Object -ComObject Scripting.FileSystemObject
        $InstallDir = $fso.GetFolder($InstallDir).ShortPath
        if (-not $InstallDir -or $InstallDir -match '\s') {
            throw 'Передайте новый каталог установки без пробелов.'
        }
    }

    # Переносим до установки, но уже под finally: если второй перенос не
    # удастся, первый каталог всё равно должен вернуться на место.
    if (-not ((Move-Aside -Path $appDataDir -Name 'AppDataRoaming') -and
              (Move-Aside -Path $localAppDataDir -Name 'AppDataLocal'))) {
        throw "Каталоги данных заняты — увести их в сторону не удалось. Закройте Chill Hub (проверьте значок в области уведомлений) и повторите."
    }

    # ---------------------------------------------------------------- установка
    Write-Host "[smoke] тихая установка в $InstallDir" -ForegroundColor Cyan
    $installerStarted = $true
    $code = Invoke-SilentInstall -SetupExe $setupPath -Dir $InstallDir -GamesDir $gamesDir
    Test-Assert -What "установщик завершился успешно" -Condition ($code -eq 0) -Detail "код возврата $code"

    Test-Assert -What "ChillHub.exe на месте" -Condition (Test-Path -LiteralPath (Join-Path $InstallDir 'ChillHub.exe'))
    Test-Assert -What "Uninstall.exe на месте" -Condition (Test-Path -LiteralPath (Join-Path $InstallDir 'Uninstall.exe'))

    # launcher.version — точное содержимое, без перевода строки и без BOM.
    # Лаунчер сравнивает эту строку с манифестом посимвольно.
    $verPath = Join-Path $InstallDir 'launcher.version'
    if (Test-Path -LiteralPath $verPath) {
        $verBytes = [IO.File]::ReadAllBytes($verPath)
        $verText = [Text.Encoding]::UTF8.GetString($verBytes)
        Test-Assert -What "launcher.version = '$ExpectedVersion'" -Condition ($verText -ceq $ExpectedVersion) -Detail "прочитано '$verText' ($($verBytes.Length) байт)"
    }
    else {
        Test-Assert -What "launcher.version создан" -Condition $false -Detail "файла нет"
    }

    # Preserve-файлы в каталоге установки быть не должно: config.json приезжает
    # из PAYLOAD_DIR только по ошибке, и тогда он попадает и в манифест сборки.
    Test-Assert -What "config.json НЕ попал в каталог установки" -Condition (-not (Test-Path -LiteralPath (Join-Path $InstallDir 'config.json')))
    $pdbs = @(Get-ChildItem -LiteralPath $InstallDir -Recurse -File -Filter '*.pdb' -ErrorAction SilentlyContinue)
    Test-Assert -What "отладочные символы не поехали в установку" -Condition ($pdbs.Count -eq 0) -Detail "найдено $($pdbs.Count)"
    $foreign = @(Get-ChildItem -LiteralPath $InstallDir -Recurse -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'linux-*' -or $_.Name -like 'osx-*' })
    Test-Assert -What "нативные библиотеки не под Windows не поехали в установку" -Condition ($foreign.Count -eq 0) -Detail ($foreign.Name -join ', ')

    # Запись в «Установка и удаление программ».
    $reg = $null
    try { $reg = Get-ItemProperty -LiteralPath $UninstKey -ErrorAction Stop } catch { }
    Test-Assert -What "запись в списке программ создана" -Condition ($null -ne $reg)
    if ($reg) {
        Test-Assert -What "DisplayVersion = '$ExpectedVersion'" -Condition ($reg.DisplayVersion -ceq $ExpectedVersion) -Detail "в реестре '$($reg.DisplayVersion)'"
        Test-Assert -What "InstallLocation указывает на каталог установки" -Condition ($reg.InstallLocation -ieq $InstallDir) -Detail "в реестре '$($reg.InstallLocation)'"
        # UninstallString с задвоенным разделителем уже был в этом файле — то,
        # что путь существует, проверяется явно.
        $uninstFromReg = ($reg.UninstallString -replace '"', '')
        Test-Assert -What "UninstallString ведёт на существующий файл" -Condition (Test-Path -LiteralPath $uninstFromReg) -Detail $reg.UninstallString
        Test-Assert -What "EstimatedSize посчитан" -Condition ($reg.EstimatedSize -gt 0) -Detail "$($reg.EstimatedSize)"
    }

    $startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Chill Hub\Chill Hub.lnk'
    Test-Assert -What "ярлык в меню «Пуск» создан" -Condition (Test-Path -LiteralPath $startMenu)

    if ($reg) {
        Test-Assert -What "ссылка на сайт есть в записи списка программ" -Condition ($reg.URLInfoAbout -like 'http*') -Detail "'$($reg.URLInfoAbout)'"
    }

    $desktopShortcutMade = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Chill Hub.lnk'
    Test-Assert -What "ярлык на рабочем столе создан (в тихом режиме — по умолчанию)" -Condition (Test-Path -LiteralPath $desktopShortcutMade)

    # Папка для игр доезжает до конфига через внешний PowerShell — шаг, у
    # которого раньше не проверялся даже код возврата. Заодно проверяется ключ
    # /GAMESDIR: до него тихая установка не умела задать папку вообще и молча
    # писала в конфиг то, что осталось в реестре от прошлой установки.
    if (Test-Path -LiteralPath $configPath) {
        $cfg = Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json
        Test-Assert -What "GamesPath из /GAMESDIR записан в config.json" -Condition ($cfg.GamesPath -ieq $gamesDir) -Detail "в конфиге '$($cfg.GamesPath)', ожидалось '$gamesDir'"
    }
    else {
        Test-Assert -What "config.json создан установщиком" -Condition $false -Detail "нет файла $configPath"
    }
    $appReg = $null
    try { $appReg = Get-ItemProperty -LiteralPath $AppKey -ErrorAction Stop } catch { }
    Test-Assert -What "папка для игр сохранена в реестре" -Condition ($appReg -and $appReg.GamesDir -ieq $gamesDir) -Detail "'$($appReg.GamesDir)'"
    Test-Assert -What "папка для игр создана" -Condition (Test-Path -LiteralPath $gamesDir)

    # ------------------------------------------------- повторная установка
    # Установка поверх уже установленного — самый частый способ обновиться, и
    # раньше он не проверялся ничем. Здесь же проверяется, что перезапись
    # файлов не спотыкается о них самих.
    #
    # Заодно проверяется чистка устаревших файлов: `File /r` никогда ничего не
    # удаляет, поэтому файл, исчезнувший в новой версии, оставался в каталоге
    # навсегда. Подкладываем такой файл (и такой каталог) руками и требуем,
    # чтобы установка поверх их убрала — а preserve-файлы сохранила.
    Write-Host "[smoke] повторная установка в тот же каталог" -ForegroundColor Cyan
    $staleFile = Join-Path $InstallDir 'stale-from-previous-version.dll'
    $staleDir = Join-Path $InstallDir 'runtimes-old'
    Set-Content -LiteralPath $staleFile -Value 'мусор от прошлой версии'
    New-Item -ItemType Directory -Path $staleDir -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $staleDir 'x.dll') -Value 'мусор'
    $statusFile = Join-Path $InstallDir 'launcher.update-status'
    Set-Content -LiteralPath $statusFile -Value 'ok' -NoNewline
    if ($NativeCleanup -and $ExpectedVersion -match '^\d+(\.\d+){1,3}$') {
        Set-ItemProperty -Path $UninstKey -Name 'DisplayVersion' -Value "$ExpectedVersion-rc.1"
    }

    $code2 = Invoke-SilentInstall -SetupExe $setupPath -Dir $InstallDir -GamesDir $gamesDir
    Test-Assert -What "установка поверх существующей завершилась успешно" -Condition ($code2 -eq 0) -Detail "код возврата $code2"
    Test-Assert -What "устаревший файл прошлой версии удалён" -Condition (-not (Test-Path -LiteralPath $staleFile))
    Test-Assert -What "устаревший каталог прошлой версии удалён" -Condition (-not (Test-Path -LiteralPath $staleDir))
    Test-Assert -What "preserve-файл пережил установку поверх" -Condition (Test-Path -LiteralPath $statusFile)
    Test-Assert -What "ChillHub.exe на месте после установки поверх" -Condition (Test-Path -LiteralPath (Join-Path $InstallDir 'ChillHub.exe'))

    # ------------------------------------------------- откат версии
    #
    # Установщик обязан спросить, прежде чем ставить версию СТАРШЕ установленной,
    # а в тихом режиме — отказаться: скриптовый откат почти наверняка ошибка.
    # Второй артефакт для этого не нужен: достаточно объявить в реестре версию
    # заведомо новее — установщик читает именно её.
    Write-Host "[smoke] установка поверх более новой версии" -ForegroundColor Cyan
    $realVersion = (Get-ItemProperty -LiteralPath $UninstKey).DisplayVersion
    Set-ItemProperty -Path $UninstKey -Name 'DisplayVersion' -Value '99.0.0'
    try {
        $downgradeCode = Invoke-SilentInstall -SetupExe $setupPath -Dir $InstallDir -GamesDir $gamesDir
        Test-Assert -What "тихий откат версии отклонён" -Condition ($downgradeCode -ne 0) -Detail "код возврата $downgradeCode (ожидался ненулевой)"
    }
    finally { Set-ItemProperty -Path $UninstKey -Name 'DisplayVersion' -Value $realVersion }

    # ------------------------------------------------- установка на занятых файлах
    #
    # Установщик обязан ОТКАЗАТЬСЯ, а не писать поверх занятых файлов половину
    # сборки. Держатель файла здесь — этот скрипт (открытый на запись в
    # монопольном режиме ChillHub.exe): для проверки не нужен ни запущенный
    # лаунчер, ни графическая сессия, а установщику эта ситуация неотличима от
    # настоящей.
    #
    # Заодно это проверка на зависание: без /SD у окна «файлы заняты» тихая
    # установка ждала бы ответа человека, которого нет.
    Write-Host "[smoke] установка при занятом ChillHub.exe" -ForegroundColor Cyan
    $lockedExe = Join-Path $InstallDir 'ChillHub.exe'
    $lock = [IO.File]::Open($lockedExe, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $lockedCode = Invoke-SilentInstall -SetupExe $setupPath -Dir $InstallDir
        Test-Assert -What "установка на занятых файлах отклонена" -Condition ($lockedCode -ne 0) -Detail "код возврата $lockedCode (ожидался ненулевой)"
    }
    finally { $lock.Dispose() }

    # ----------------------------------------------------------------- удаление
    #
    # `_?=<каталог>` обязателен для скриптового удаления: без него деинсталлятор
    # копирует себя во временный каталог, перезапускается оттуда и СРАЗУ отдаёт
    # управление — Wait дожидается копии, а не удаления, и проверки ниже
    # состязались бы с ещё не закончившей работу программой.
    #
    # Плата за режим «на месте» — сам Uninstall.exe остаётся: удалить работающий
    # файл Windows не даст. Это ожидаемое поведение NSIS, поэтому ниже
    # проверяется отсутствие всего остального, а огрызок доубирает cleanup.
    Write-Host "[smoke] тихое удаление" -ForegroundColor Cyan
    $uninstaller = Join-Path $InstallDir 'Uninstall.exe'
    $securityJournal = Join-Path $appDataDir 'security-exclusions.json'
    if ($NativeCleanup) {
        $journal = '{"AddedPaths":["C:\\Games\\ChillHub"],"IntroductionShown":true}'
        Set-Content -LiteralPath $securityJournal -Value $journal -NoNewline -Encoding utf8
        # The unsafe configured path must fail before files, registry entries or
        # settings disappear. With no game deletion selected, it is irrelevant.
        $driveRoot = [IO.Path]::GetPathRoot($gamesDir)
        Set-ItemProperty -LiteralPath $AppKey -Name 'GamesDir' -Value $driveRoot
        $refused = Start-Process -FilePath $uninstaller -ArgumentList '/S', '/DELETEGAMES', "_?=$InstallDir" -PassThru -Wait
        Test-Assert -What "деинсталлятор отклонил удаление корня диска" -Condition ($refused.ExitCode -ne 0)
        Test-Assert -What "отказ оставил установленный лаунчер" -Condition (Test-Path -LiteralPath (Join-Path $InstallDir 'ChillHub.exe'))
        Test-Assert -What "отказ оставил настройки" -Condition (Test-Path -LiteralPath $configPath)
        Test-Assert -What "отказ оставил запись в списке программ" -Condition (Test-Path -LiteralPath $UninstKey)
    }
    $up = Start-Process -FilePath $uninstaller -ArgumentList '/S', "_?=$InstallDir" -PassThru -Wait
    Test-Assert -What "деинсталлятор завершился успешно" -Condition ($up.ExitCode -eq 0) -Detail "код возврата $($up.ExitCode)"

    Test-Assert -What "ChillHub.exe удалён" -Condition (-not (Test-Path -LiteralPath (Join-Path $InstallDir 'ChillHub.exe')))
    Test-Assert -What "launcher.version удалён" -Condition (-not (Test-Path -LiteralPath $verPath))
    Test-Assert -What "запись в списке программ удалена" -Condition (-not (Test-Path -LiteralPath $UninstKey))
    Test-Assert -What "ключ настроек установки удалён" -Condition (-not (Test-Path -LiteralPath $AppKey))
    Test-Assert -What "ярлык в меню «Пуск» удалён" -Condition (-not (Test-Path -LiteralPath $startMenu))

    # Служебные каталоги лаунчера уходят ВСЕГДА, без всяких галочек: удаление
    # программы значит удаление программы (см. секцию Uninstall у installer.nsi).
    if ($NativeCleanup) {
        Test-Assert -What "настройки удалены, когда папка игр недопустима для удаления" -Condition (-not (Test-Path -LiteralPath $configPath))
        Test-Assert -What "журнал исключений сохранён без изменений" -Condition ((Get-Content -LiteralPath $securityJournal -Raw -ErrorAction SilentlyContinue) -ceq $journal)
        if (Test-Path -LiteralPath $securityJournal) {
            Remove-Item -LiteralPath $securityJournal
        }
    }
    else {
        Test-Assert -What "%APPDATA%\ChillHub удалён вместе с лаунчером" -Condition (-not (Test-Path -LiteralPath $appDataDir)) -Detail $appDataDir
    }
    Test-Assert -What "%LOCALAPPDATA%\ChillHub удалён вместе с лаунчером" -Condition (-not (Test-Path -LiteralPath $localAppDataDir)) -Detail $localAppDataDir

    # А вот папка с играми — пользовательские данные, и без явной галочки она
    # обязана пережить удаление.
    Test-Assert -What "папка с играми пережила удаление без галочки" -Condition (Test-Path -LiteralPath $gamesDir)

    # ------------------------------------------- удаление С ГАЛОЧКОЙ «И ИГРЫ»
    #
    # Вторая ветка удаления — та, где человек попросил унести и игры. Она делает
    # RMDir /r по пути из СВОБОДНОГО ТЕКСТОВОГО ПОЛЯ и до появления ключа
    # /DELETEGAMES не прогонялась ни разу: тихое удаление всегда шло по ветке
    # «игры не трогаем».
    Write-Host "[smoke] установка заново ради проверки удаления с галочкой «и игры»" -ForegroundColor Cyan
    $code3 = Invoke-SilentInstall -SetupExe $setupPath -Dir $InstallDir -GamesDir $gamesDir
    Test-Assert -What "установка перед проверкой удаления с галочкой прошла" -Condition ($code3 -eq 0) -Detail "код возврата $code3"

    # Файлы-маркеры: пустой каталог мог бы исчезнуть и сам по себе, а эти файлы
    # исчезают только вместе с ним. legacy-config.json — след прежней установки
    # в %LOCALAPPDATA% у тех, кто ставил лаунчер своим путём.
    Set-Content -LiteralPath (Join-Path $gamesDir 'игра.bin') -Value 'данные игры'
    New-Item -ItemType Directory -Path $localAppDataDir -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $localAppDataDir 'legacy-config.json') -Value '{}'

    $up2 = Start-Process -FilePath $uninstaller -ArgumentList '/S', '/DELETEGAMES', "_?=$InstallDir" -PassThru -Wait
    Test-Assert -What "деинсталлятор с галочкой завершился успешно" -Condition ($up2.ExitCode -eq 0) -Detail "код возврата $($up2.ExitCode)"
    Test-Assert -What "папка с играми удалена по галочке" -Condition (-not (Test-Path -LiteralPath $gamesDir)) -Detail $gamesDir
    Test-Assert -What "%APPDATA%\ChillHub удалён и во второй раз" -Condition (-not (Test-Path -LiteralPath $appDataDir)) -Detail $appDataDir
    Test-Assert -What "каталог прежней установки в %LOCALAPPDATA% удалён" -Condition (-not (Test-Path -LiteralPath $localAppDataDir)) -Detail $localAppDataDir

    # ------------------------------------- удаление ПРИ ЗАПУЩЕННОМ ЛАУНЧЕРЕ
    #
    # Деинсталлятор обязан закрыть лаунчер сам. Раньше он показывал диалог
    # «закройте программу», а в тихом режиме просто отказывался работать — и то
    # и другое доставалось человеку, который уже нажал «Удалить» и ушёл.
    #
    # ЛАУНЧЕР ЗДЕСЬ НЕ ЗАПУСКАЕТСЯ: это WPF-приложение, а раннер CI — машина без
    # графической сессии. Вместо него на месте ChillHub.exe оказывается копия
    # cmd.exe: для проверки важно ровно то, что процесс с таким ИМЕНЕМ и таким
    # ПУТЁМ держит файл, — а держит его настоящий процесс, а не заглушка в коде.
    # Заодно это проверяет ветку принудительного завершения: у копии cmd.exe нет
    # окна, WM_CLOSE ей не поможет, и уйти она может только по таймауту.
    Write-Host "[smoke] удаление при запущенном лаунчере" -ForegroundColor Cyan
    $code4 = Invoke-SilentInstall -SetupExe $setupPath -Dir $InstallDir -GamesDir $gamesDir
    Test-Assert -What "установка перед проверкой авто-закрытия прошла" -Condition ($code4 -eq 0) -Detail "код возврата $code4"

    $fakeExe = Join-Path $InstallDir 'ChillHub.exe'
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\cmd.exe') -Destination $fakeExe -Force
    $fake = Start-Process -FilePath $fakeExe -ArgumentList '/c', 'ping -n 300 127.0.0.1 > nul' -PassThru -WindowStyle Hidden
    try {
        # Файл обязан быть занят ДО удаления — иначе проверка ничего не проверяет.
        $lockedBefore = $false
        try { $probe = [IO.File]::Open($fakeExe, 'Open', 'ReadWrite', 'None'); $probe.Dispose() }
        catch { $lockedBefore = $true }
        Test-Assert -What "подставной процесс действительно держит ChillHub.exe" -Condition $lockedBefore

        $up3 = Start-Process -FilePath $uninstaller -ArgumentList '/S', "_?=$InstallDir" -PassThru -Wait
        Test-Assert -What "тихое удаление при запущенном лаунчере прошло" -Condition ($up3.ExitCode -eq 0) -Detail "код возврата $($up3.ExitCode)"
        Test-Assert -What "запущенный лаунчер закрыт деинсталлятором" -Condition $fake.HasExited
        Test-Assert -What "ChillHub.exe удалён после авто-закрытия" -Condition (-not (Test-Path -LiteralPath $fakeExe))
    }
    finally {
        if (-not $fake.HasExited) {
            Write-Warning "[smoke] подставной процесс пришлось убирать вручную — авто-закрытие не сработало"
            try { $fake.Kill() } catch { }
        }
    }
}
finally {
    $restoreFailures = [Collections.Generic.List[string]]::new()
    foreach ($original in $appDataDir, $localAppDataDir) {
        try {
            if ($stashed.ContainsKey($original)) {
                $null = Get-Item -LiteralPath $stashed[$original]
                if (Test-Path -LiteralPath $original) {
                    Remove-Item -LiteralPath $original -Recurse -Force
                }
                Move-Item -LiteralPath $stashed[$original] -Destination $original
            }
            elseif ($installerStarted -and (Test-Path -LiteralPath $original)) {
                Remove-Item -LiteralPath $original -Recurse -Force
            }
        }
        catch {
            $restoreFailures.Add("Каталог ${original}: $($_.Exception.Message)")
        }
    }

    if ($installerStarted) {
        foreach ($shortcut in $shortcutSnapshots) {
            try {
                Restore-Snapshot -Path $shortcut.Path -Name $shortcut.Name -Existed $shortcut.Existed
            }
            catch {
                $restoreFailures.Add("Ярлыки $($shortcut.Path): $($_.Exception.Message)")
            }
        }
        foreach ($key in $registrySnapshots.Keys) {
            try {
                Restore-RegistrySnapshot -Key $key -File $registrySnapshots[$key]
            }
            catch {
                $restoreFailures.Add("Раздел ${key}: $($_.Exception.Message)")
            }
        }
    }

    if ($restoreFailures.Count -eq 0) {
        Remove-Item -LiteralPath $snapshotDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($installerStarted -and (Test-Path -LiteralPath $gamesDir)) {
        Remove-Item -LiteralPath $gamesDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    # Каталог освобождается не мгновенно: Uninstall.exe завершился, но ссылка
    # на файл может ещё жить у антивируса или индексатора.
    for ($i = 0; $i -lt 5 -and $createdInstallDir -and (Test-Path -LiteralPath $createdInstallDir); $i++) {
        try { Remove-Item -LiteralPath $createdInstallDir -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Milliseconds 400 }
    }
    if ($createdInstallDir -and (Test-Path -LiteralPath $createdInstallDir)) {
        Write-Warning "не удалось убрать временный каталог $createdInstallDir"
    }
    if ($restoreFailures.Count -gt 0) {
        throw "Снимки сохранены в $snapshotDir. Ошибки восстановления: $($restoreFailures -join '; ')"
    }
}

if ($script:Failures.Count -gt 0) {
    throw ("Smoke-проверка установщика провалена ({0} из проверок): {1}" -f $script:Failures.Count, ($script:Failures -join '; '))
}
Write-Host "[smoke] все проверки установщика пройдены" -ForegroundColor Green
