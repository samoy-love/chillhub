# Гарантирует makensis на машине: и текущему процессу, и следующим шагам.
#
# Зовётся из ДВУХ мест, и это причина существования файла:
#
#   * ci.yml, job installer-windows — отдельным шагом перед сборкой;
#   * deploy.yml, job installer — первой командой входа `build` пайплайна
#     desktop-artifact (сборка и подготовка там живут в одном шаге).
#
# Пока логика лежала инлайном в ci.yml, второй путь молча полагался на NSIS
# из образа windows-latest — а образ его больше не несёт. CI при этом был
# зелёным: свой шаг установки у него был. Первая же настоящая публикация
# установщика упала на «NSIS not found» — дефект жил ровно в зазоре между
# двумя копиями одного и того же знания. Поэтому копий больше нет.
#
# ============================================================================
# ИСТОЧНИК NSIS: ПОРТАТИВНЫЙ АРХИВ С ЗАКРЕПЛЁННОЙ СУММОЙ, А НЕ CHOCOLATEY
# ============================================================================
# Было `choco install nsis --version=...`, и с ним репозиторий получил два
# разных класса отказов:
#
#   * пакет 3.11 ПРОПАЛ из community-фида, и сборка установщика встала без
#     единой правки в репозитории — у Chocolatey старые версии со временем
#     перестают отдаваться;
#   * 15.08.2026 — четыре падения за один день (504, 499, «package not
#     found»), окно нестабильности фида в несколько минут.
#
# То есть выкатка установщика зависела от доступности стороннего сервиса,
# который к NSIS никакого отношения не имеет. Повторные попытки лечили только
# второй класс и ничего не могли сделать с первым.
#
# Теперь берётся официальный zip-дистрибутив NSIS: makensis из него работает
# распакованным, установка не нужна вовсе, права администратора — тоже.
# Подлинность проверяется SHA-256, закреплённой ниже.
#
# ОТКУДА ВЗЯЛАСЬ СУММА. Она не переписана с чьей-то страницы: архив скачан и
# просуммирован при подготовке этой правки, причём двумя разными путями
# (прямая ссылка на зеркало и страница загрузки SourceForge) — обе дали одно и
# то же значение. Прошлая попытка перейти на постоянный источник заглохла
# именно на этом: прописывать в CI сумму, которую не проверил сам, — риск хуже
# исходной проблемы, потому что один неверный хеш ломает сборку установщика
# навсегда и без внятного объяснения.
#
# КАК ПОДНИМАТЬ ВЕРСИЮ. Скачать nsis-<версия>.zip со страницы проекта,
# посчитать `Get-FileHash -Algorithm SHA256`, вписать сюда оба значения. Кэш в
# ci.yml сбросится сам: его ключ — хэш этого файла.
#
# Chocolatey остался РЕЗЕРВОМ на случай, когда до SourceForge не достучаться.
# Он больше не единственный путь, поэтому его перебои перестали быть отказом
# выкатки.
$ErrorActionPreference = 'Stop'

$NsisVersion = '3.12'
$NsisZipSha256 = '56581F90DB321581C5381193D796FFFCF2D24B2F8FED2160A6C6A3BAA67F2C4F'
$NsisZipUrl = "https://downloads.sourceforge.net/project/nsis/NSIS%203/$NsisVersion/nsis-$NsisVersion.zip"

# Версия для резервного пути через Chocolatey: там пакеты именуются иначе
# (3.12.0 против 3.12 у самого проекта).
$NsisChocoVersion = '3.12.0'
$ChocoMaxAttempts = 3
$ChocoRetryDelaySeconds = 30

$SystemNsisDir = 'C:\Program Files (x86)\NSIS'
# Портативная копия кладётся В РЕПОЗИТОРИЙ, а не в Program Files: туда можно
# писать без администратора (локальный прогон у обычного пользователя), и этот
# же каталог кэширует CI. Он в .gitignore.
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$PortableDir = Join-Path $RepoRoot ".tools\nsis-$NsisVersion"

# PATH правится ДВАЖДЫ, и обе правки нужны:
#   * $env:PATH — для текущего процесса: вызывающий может собрать установщик
#     этой же командной строкой, отдельного «следующего шага» у него нет;
#   * $GITHUB_PATH — для следующих шагов workflow (ci.yml так и устроен).
# Вне GitHub Actions переменной GITHUB_PATH нет — тогда правим только PATH.
function Add-NsisToPath {
    param([Parameter(Mandatory = $true)][string]$Dir)
    if ($env:PATH -notlike "*$Dir*") { $env:PATH = "$Dir;$env:PATH" }
    if ($env:GITHUB_PATH) { Add-Content -Path $env:GITHUB_PATH -Value $Dir }
}

# Несовпадение контрольной суммы отличается от «не скачалось», и отличать их
# обязан catch у вызова: резервный путь через Chocolatey сумму не сверяет
# вовсе, поэтому уход туда превратил бы подмену архива в предупреждение в логе
# при зелёном шаге. Флаг ставится ровно в одном месте — там, где сумма не
# сошлась.
$script:NsisChecksumMismatch = $false

function Install-NsisPortable {
    param([Parameter(Mandatory = $true)][string]$TargetDir)

    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("nsis-" + [Guid]::NewGuid().ToString('N') )
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null
    try {
        $zip = Join-Path $tmp "nsis-$NsisVersion.zip"
        Write-Host "NSIS ${NsisVersion}: качаю $NsisZipUrl"
        # UserAgent — ЛЮБОЙ, КРОМЕ БРАУЗЕРНОГО, и это не придирка.
        #
        # SourceForge смотрит на заголовок и браузеру отдаёт HTML-страницу
        # «ваша загрузка вот-вот начнётся» вместо архива. Собственный UA
        # PowerShell (в нём есть «Mozilla/5.0 ... WindowsPowerShell») тоже
        # считается браузерным: без явного заголовка сюда приезжало 140 КБ
        # разметки. С UA вида curl или своим — настоящий архив, проверено
        # обоими вариантами.
        #
        # Подмену поймала бы и сверка суммы ниже, но падать «сумма не сошлась»
        # там, где на деле пришла страница-заглушка, — значит каждый раз
        # заново расследовать одно и то же.
        Invoke-WebRequest -Uri $NsisZipUrl -OutFile $zip -UseBasicParsing -TimeoutSec 180 `
            -UserAgent 'chillhub-ci/1.0' -MaximumRedirection 10

        $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash
        if ($actual -ne $NsisZipSha256) {
            $script:NsisChecksumMismatch = $true
            throw @"
Контрольная сумма архива NSIS не совпала.
  ожидалась: $NsisZipSha256
  получена:  $actual
  источник:  $NsisZipUrl

Это либо подмена содержимого, либо страница-заглушка вместо архива, либо
версия на сервере молча переиздана. Ни один из вариантов не годится: этим
компилятором собирается файл, который скачивают пользователи.
"@
        }
        Write-Host "NSIS ${NsisVersion}: контрольная сумма совпала"

        $unpack = Join-Path $tmp 'unpack'
        Expand-Archive -LiteralPath $zip -DestinationPath $unpack -Force
        # В архиве один корневой каталог nsis-<версия>; раскладку внутри
        # (makensis.exe, Include\, Plugins\, Stubs\) makensis ищет
        # ОТНОСИТЕЛЬНО СЕБЯ, поэтому переносим её целиком, как есть.
        $root = Get-ChildItem -LiteralPath $unpack -Directory | Select-Object -First 1
        if (-not $root) { throw "В архиве NSIS не оказалось корневого каталога — раскладка изменилась?" }

        if (Test-Path -LiteralPath $TargetDir) { Remove-Item -LiteralPath $TargetDir -Recurse -Force }
        New-Item -ItemType Directory -Path (Split-Path -Parent $TargetDir) -Force | Out-Null
        Move-Item -LiteralPath $root.FullName -Destination $TargetDir

        if (-not (Test-Path -LiteralPath (Join-Path $TargetDir 'makensis.exe'))) {
            throw "После распаковки в '$TargetDir' нет makensis.exe."
        }
    }
    finally {
        try { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction Stop } catch {}
    }
}

function Install-NsisViaChocolatey {
    # Сам choco при недоступности фида отдаёт финальную ошибку сразу, без
    # собственных повторов — а виденные сбои исчезали за минуты.
    for ($attempt = 1; $attempt -le $ChocoMaxAttempts; $attempt++) {
        choco install nsis --version=$NsisChocoVersion --no-progress -y
        if ($LASTEXITCODE -eq 0) { return $true }
        if ($attempt -lt $ChocoMaxAttempts) {
            Write-Host "choco install nsis не удался (попытка $attempt из $ChocoMaxAttempts, exit $LASTEXITCODE), повтор через $ChocoRetryDelaySeconds с..."
            Start-Sleep -Seconds $ChocoRetryDelaySeconds
        }
    }
    return $false
}

if (Get-Command makensis -ErrorAction SilentlyContinue) {
    Write-Host "makensis found: $((Get-Command makensis).Source)"
}
elseif (Test-Path (Join-Path $PortableDir 'makensis.exe')) {
    # Сюда же попадает восстановленный кэш CI: отдельной ветки «кэш» не нужно,
    # достаточно того, что каталог на месте.
    Write-Host "makensis found in the portable directory: $PortableDir"
    Add-NsisToPath -Dir $PortableDir
}
elseif (Test-Path (Join-Path $SystemNsisDir 'makensis.exe')) {
    Write-Host "makensis found in the default install directory"
    Add-NsisToPath -Dir $SystemNsisDir
}
else {
    Write-Host "makensis not found, fetching NSIS $NsisVersion (portable, checksum-pinned)"
    try {
        Install-NsisPortable -TargetDir $PortableDir
        Add-NsisToPath -Dir $PortableDir
    }
    catch {
        # Сумма не сошлась — резервного пути нет. Chocolatey поставил бы тот же
        # NSIS без единой сверки, а этим компилятором собирается файл, который
        # скачивают пользователи: такой отказ обязан быть красным.
        if ($script:NsisChecksumMismatch) { throw }
        Write-Warning "Портативный NSIS получить не удалось: $($_.Exception.Message)"
        Write-Warning "Пробую резервный путь — Chocolatey."
        if (-not (Get-Command choco -ErrorAction SilentlyContinue)) {
            throw "NSIS не получен: портативный архив недоступен (см. предупреждение выше), а Chocolatey на этой машине нет. Установите NSIS $NsisVersion вручную или положите распакованный дистрибутив в $PortableDir."
        }
        if (-not (Install-NsisViaChocolatey)) {
            throw "NSIS не получен ни портативным архивом, ни через Chocolatey ($ChocoMaxAttempts попыток). Проверьте сеть; при необходимости положите распакованный дистрибутив NSIS $NsisVersion в $PortableDir."
        }
        Add-NsisToPath -Dir $SystemNsisDir
    }
}

# Какой именно компилятор собрал артефакт — это должно быть видно в логе,
# а не выясняться постфактум.
makensis /VERSION; Write-Host ""
if ($LASTEXITCODE -ne 0) { throw "makensis недоступен и после установки (exit $LASTEXITCODE)" }
