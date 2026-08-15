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
# графической сессии, а ChillHub — WPF-приложение. Проверяется установка, а не
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
    [string]$InstallDir
)

$ErrorActionPreference = 'Stop'

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
    $InstallDir = Join-Path ([IO.Path]::GetTempPath().TrimEnd('\')) ("chillhub-smoke-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
}

# Путь без пробелов — требование ключа /D= (см. Invoke-SilentInstall), а
# временный каталог лежит в профиле пользователя, в имени которого пробел —
# обычное дело. Короткая форма пути (8.3) решает это, не заставляя никого
# передавать каталог руками; она же — то, что и так стоит в %TEMP% на раннере
# CI. Каталог для этого должен существовать, поэтому создаём его заранее:
# установщик всё равно создал бы его сам.
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
if ($InstallDir -match '\s') {
    try {
        $fso = New-Object -ComObject Scripting.FileSystemObject
        $short = $fso.GetFolder($InstallDir).ShortPath
        if ($short -and $short -notmatch '\s') { $InstallDir = $short }
    }
    catch { }
}

# Конфиг лаунчера живёт в профиле, а не в каталоге установки, и установщик
# пишет туда осознанно (И19). На машине разработчика это ЕГО настоящий конфиг,
# поэтому сохраняем и возвращаем как было: проверка не имеет права трогать
# рабочее окружение того, кто её запустил.
$appDataDir = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'ChillHub'
$configPath = Join-Path $appDataDir 'config.json'
$configBackup = $null
$configExistedBefore = Test-Path -LiteralPath $configPath
if ($configExistedBefore) {
    $configBackup = "$configPath.smoke-backup"
    Copy-Item -LiteralPath $configPath -Destination $configBackup -Force
}

# ОКРУЖЕНИЕ ВОЗВРАЩАЕТСЯ РОВНО В ИСХОДНОЕ СОСТОЯНИЕ.
#
# Проверка ставит и удаляет НАСТОЯЩИЙ ChillHub, а установщик — пользовательский:
# он пишет в HKCU, кладёт ярлык на рабочий стол и в меню «Пуск» по фиксированным
# путям, не зависящим от каталога установки. На чистом раннере это неважно, а на
# машине разработчика, где ChillHub стоит по-настоящему, тихое удаление в конце
# снесло бы его ярлыки и запись в списке программ — то есть проверка сломала бы
# ровно то, что проверяет.
#
# Поэтому всё, что установщик трогает вне $InstallDir, снимается до и
# восстанавливается после, независимо от исхода проверок.
$snapshotDir = Join-Path ([IO.Path]::GetTempPath().TrimEnd('\')) ("chillhub-smoke-backup-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $snapshotDir -Force | Out-Null

$desktopLnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'ChillHub.lnk'
$startMenuDir = Join-Path ([Environment]::GetFolderPath('Programs')) 'ChillHub'

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
        if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue }
        Copy-Item -LiteralPath $saved -Destination $Path -Recurse -Force
    }
    elseif (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$hadDesktopLnk = Save-Snapshot -Path $desktopLnk -Name 'ChillHub.lnk'
$hadStartMenu = Save-Snapshot -Path $startMenuDir -Name 'StartMenu'

# Ключи реестра сохраняются целиком (reg export), а не по одному значению:
# восстановить надо всё, что было, а не то, что вспомнили перечислить.
$hadUninstKey = Test-Path -LiteralPath $UninstKey
$hadAppKey = Test-Path -LiteralPath $AppKey
if ($hadUninstKey) { reg export "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\ChillHub" (Join-Path $snapshotDir 'uninst.reg') /y | Out-Null }
if ($hadAppKey) { reg export "HKCU\Software\ChillHub\Install" (Join-Path $snapshotDir 'app.reg') /y | Out-Null }

$gamesDir = Join-Path ([IO.Path]::GetTempPath().TrimEnd('\')) ("chillhub-smoke-games-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))

try {
    # ---------------------------------------------------------------- установка
    Write-Host "[smoke] тихая установка в $InstallDir" -ForegroundColor Cyan
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

    $startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'ChillHub\ChillHub.lnk'
    Test-Assert -What "ярлык в меню «Пуск» создан" -Condition (Test-Path -LiteralPath $startMenu)

    if ($reg) {
        Test-Assert -What "ссылка на сайт есть в записи списка программ" -Condition ($reg.URLInfoAbout -like 'http*') -Detail "'$($reg.URLInfoAbout)'"
    }

    $desktopShortcutMade = Join-Path ([Environment]::GetFolderPath('Desktop')) 'ChillHub.lnk'
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
    $up = Start-Process -FilePath $uninstaller -ArgumentList '/S', "_?=$InstallDir" -PassThru -Wait
    Test-Assert -What "деинсталлятор завершился успешно" -Condition ($up.ExitCode -eq 0) -Detail "код возврата $($up.ExitCode)"

    Test-Assert -What "ChillHub.exe удалён" -Condition (-not (Test-Path -LiteralPath (Join-Path $InstallDir 'ChillHub.exe')))
    Test-Assert -What "launcher.version удалён" -Condition (-not (Test-Path -LiteralPath $verPath))
    Test-Assert -What "запись в списке программ удалена" -Condition (-not (Test-Path -LiteralPath $UninstKey))
    Test-Assert -What "ключ настроек установки удалён" -Condition (-not (Test-Path -LiteralPath $AppKey))
    Test-Assert -What "ярлык в меню «Пуск» удалён" -Condition (-not (Test-Path -LiteralPath $startMenu))

    # Настройки пользователя переживают удаление намеренно (см. комментарий в
    # секции Uninstall у installer.nsi) — проверяем именно это, а не обратное.
    Test-Assert -What "настройки пользователя не тронуты удалением" -Condition (Test-Path -LiteralPath $configPath)
}
finally {
    # Возвращаем окружение как было — проверка не должна оставлять следов ни на
    # раннере, ни на машине разработчика.
    if ($configBackup) {
        Copy-Item -LiteralPath $configBackup -Destination $configPath -Force
        Remove-Item -LiteralPath $configBackup -Force -ErrorAction SilentlyContinue
    }
    elseif (-not $configExistedBefore -and (Test-Path -LiteralPath $appDataDir)) {
        Remove-Item -LiteralPath $appDataDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $gamesDir) { Remove-Item -LiteralPath $gamesDir -Recurse -Force -ErrorAction SilentlyContinue }
    Restore-Snapshot -Path $desktopLnk -Name 'ChillHub.lnk' -Existed $hadDesktopLnk
    Restore-Snapshot -Path $startMenuDir -Name 'StartMenu' -Existed $hadStartMenu

    if (Test-Path -LiteralPath $UninstKey) { Remove-Item -LiteralPath $UninstKey -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $AppKey) { Remove-Item -LiteralPath $AppKey -Recurse -Force -ErrorAction SilentlyContinue }
    if ($hadUninstKey) { reg import (Join-Path $snapshotDir 'uninst.reg') 2>&1 | Out-Null }
    if ($hadAppKey) { reg import (Join-Path $snapshotDir 'app.reg') 2>&1 | Out-Null }
    Remove-Item -LiteralPath $snapshotDir -Recurse -Force -ErrorAction SilentlyContinue

    # Каталог освобождается не мгновенно: Uninstall.exe завершился, но ссылка
    # на файл может ещё жить у антивируса или индексатора.
    for ($i = 0; $i -lt 5 -and (Test-Path -LiteralPath $InstallDir); $i++) {
        try { Remove-Item -LiteralPath $InstallDir -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Milliseconds 400 }
    }
    if (Test-Path -LiteralPath $InstallDir) {
        Write-Warning "не удалось убрать временный каталог $InstallDir"
    }
}

if ($script:Failures.Count -gt 0) {
    throw ("Smoke-проверка установщика провалена ({0} из проверок): {1}" -f $script:Failures.Count, ($script:Failures -join '; '))
}
Write-Host "[smoke] все проверки установщика пройдены" -ForegroundColor Green
