<#
.SYNOPSIS
    Запускает WPF-клиент ChillHub против локального API.

.DESCRIPTION
    Клиент берёт адрес сервера из %APPDATA%\ChillHub\config.json и ниоткуда
    больше: переменной окружения, которая бы его переопределяла, нет — и это
    осознанно, конфиг заодно хранит путь к играм и последнюю открытую игру.
    Поэтому «запустить против локального API» означает ровно одно: подменить
    ApiBaseUrl в этом файле, запустить клиент, вернуть файл как было.

    Возврат — главная часть. Оставленный localhost в конфиге означает, что
    после отладки клиент молча перестанет видеть прод, а выглядеть это будет
    как «сервер лежит»: тот же экран, та же ошибка сети.

    Открытый http:// клиент принимает только для петлевых адресов
    (Config.IsAcceptableApiBaseUrl) — по этому же адресу он берёт манифест
    самообновления, и подмена сервера означала бы подмену обновления.

.PARAMETER ApiBase
    Адрес локального API. По умолчанию http://localhost:55700 — порт цели
    chillhub-api из .deploy-kit/api.env.

.EXAMPLE
    dk run chillhub-client
#>
param(
    [string]$ApiBase = 'http://localhost:55700'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$project = Join-Path $repoRoot 'launcher\ChillHub\ChillHub.csproj'
if (-not (Test-Path $project)) { throw "не найден $project" }

# Конфиг живёт в %APPDATA%, а НЕ в %LOCALAPPDATA%: последний — каталог
# установки лаунчера, и конфиг оттуда попадал в пакет обновления.
$configDir = Join-Path $env:APPDATA 'ChillHub'
$configPath = Join-Path $configDir 'config.json'
$backup = $null
$hadConfig = Test-Path $configPath

if ($hadConfig) {
    $backup = Get-Content -Path $configPath -Raw
    $cfg = $backup | ConvertFrom-Json
} else {
    # Первого запуска ещё не было — клиент создаст конфиг сам, но нам нужен
    # адрес уже сейчас. Пишем минимальный: остальные поля клиент дозаполнит
    # своими дефолтами.
    if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir | Out-Null }
    $cfg = [pscustomobject]@{ GamesPath = ''; DownloadThreads = 8; ApiBaseUrl = ''; LastGameId = '' }
}

$cfg.ApiBaseUrl = $ApiBase
$cfg | ConvertTo-Json -Depth 8 | Set-Content -Path $configPath -Encoding UTF8
Write-Host "[dk run] ApiBaseUrl -> $ApiBase" -ForegroundColor Cyan

try {
    # YL_DEV_SKIP_SELF_UPDATE — иначе клиент при старте пойдёт за обновлением
    # к локальному API, не найдёт манифеста и уйдёт в окно апдейтера вместо
    # того, ради чего его запускали.
    $env:YL_DEV_SKIP_SELF_UPDATE = '1'
    dotnet run --project $project `
        -p:RunAnalyzersDuringBuild=false `
        -p:RunAnalyzersDuringLiveAnalysis=false `
        -p:TreatWarningsAsErrors=false
} finally {
    if ($hadConfig) {
        Set-Content -Path $configPath -Value $backup -Encoding UTF8
        Write-Host "[dk run] ApiBaseUrl возвращён как был" -ForegroundColor Cyan
    } else {
        Remove-Item -Path $configPath -ErrorAction SilentlyContinue
        Write-Host "[dk run] временный config.json удалён" -ForegroundColor Cyan
    }
}
