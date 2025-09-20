param(
  [string]$GamesPath
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

if (-not $GamesPath -or $GamesPath -eq '') {
  $defaultD = "D:\\Games\\ChillHub"
  $defaultC = "C:\\Games\\ChillHub"
  if (Test-Path 'D:\\') { $GamesPath = $defaultD } else { $GamesPath = $defaultC }
}

Write-Host "GamesPath = $GamesPath" -ForegroundColor Cyan

Push-Location (Join-Path $repoRoot 'launcher\ChillHub')
# Persist games path into settings if app supports it; otherwise pass via env
$env:ChillHub_GAMES_PATH = $GamesPath

Write-Host "Starting WPF client..." -ForegroundColor Green
# Build & run
# dotnet build
 dotnet run --project .\ChillHub.csproj
Pop-Location
