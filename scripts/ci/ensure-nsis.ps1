# Гарантирует makensis на раннере: и текущему процессу, и следующим шагам.
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
# И31: версия NSIS закреплена явно.
#
# Было `choco install nsis` без версии — то есть «что окажется свежим в
# момент запуска». Установщик собирается компилятором, который может
# смениться между двумя прогонами одного и того же коммита; это ровно та
# невоспроизводимость, ради борьбы с которой в ci.yml закреплены все
# остальные линтеры.
#
# Если Chocolatey отвергнет эту версию, скрипт упадёт с внятной ошибкой —
# это нормальное, ревьюируемое однострочное обновление.
# Посмотреть доступные версии: choco list nsis --all-versions
#
# 3.11 -> 3.12.0: ровно этот случай и произошёл. Пакет nsis 3.11 пропал из
# community-фида, и сборка установщика встала с «The package was not found
# with the source(s) listed» — без единой правки в репозитории. Так что
# закрепление версии защищает от подмены компилятора, но не от исчезновения
# пакета: у Chocolatey старые версии со временем перестают отдаваться.
# Если это начнёт повторяться, следующий шаг — тянуть NSIS с постоянного
# адреса (SourceForge) и проверять контрольную сумму, а не через choco.
$ErrorActionPreference = 'Stop'
$NsisVersion = '3.12.0'
$NsisDir = 'C:\Program Files (x86)\NSIS'

# PATH правится ДВАЖДЫ, и обе правки нужны:
#   * $env:PATH — для текущего процесса: вызывающий может собрать установщик
#     этой же командной строкой, отдельного «следующего шага» у него нет;
#   * $GITHUB_PATH — для следующих шагов workflow (ci.yml так и устроен).
# Вне GitHub Actions переменной GITHUB_PATH нет — тогда правим только PATH.
function Add-NsisToPath {
    if ($env:PATH -notlike "*$NsisDir*") { $env:PATH = "$NsisDir;$env:PATH" }
    if ($env:GITHUB_PATH) { Add-Content -Path $env:GITHUB_PATH -Value $NsisDir }
}

if (Get-Command makensis -ErrorAction SilentlyContinue) {
    Write-Host "makensis found: $((Get-Command makensis).Source)"
} elseif (Test-Path (Join-Path $NsisDir 'makensis.exe')) {
    Write-Host "makensis found in the default install directory"
    Add-NsisToPath
} else {
    Write-Host "makensis not found, installing NSIS $NsisVersion via Chocolatey"
    choco install nsis --version=$NsisVersion --no-progress -y
    if ($LASTEXITCODE -ne 0) {
        throw "choco install nsis --version=$NsisVersion failed. Если версия больше не публикуется, обновите `$NsisVersion в этом скрипте (choco list nsis --all-versions)."
    }
    Add-NsisToPath
}

# Какой именно компилятор собрал артефакт — это должно быть видно в логе,
# а не выясняться постфактум.
makensis /VERSION; Write-Host ""
if ($LASTEXITCODE -ne 0) { throw "makensis недоступен и после установки (exit $LASTEXITCODE)" }
