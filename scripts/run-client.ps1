param(
  [string]$GamesPath
)

# Ensure Unicode I/O
try {
  [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($true)
  [Console]::InputEncoding  = [System.Text.UTF8Encoding]::new($true)
} catch {}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Split-Path -Parent $scriptDir

if (-not $GamesPath -or $GamesPath -eq '') {
  $defaultD = "D:\\Games\\ChillHub"
  $defaultC = "C:\\Games\\ChillHub"
  if (Test-Path 'D:\\') { $GamesPath = $defaultD } else { $GamesPath = $defaultC }
}

Write-Host "GamesPath = $GamesPath" -ForegroundColor Cyan

Push-Location (Join-Path $repoRoot 'launcher\ChillHub')
try {
  # Pass GamesPath via environment for app to consume
  $env:ChillHub_GAMES_PATH = $GamesPath
  Write-Host "Starting WPF client..." -ForegroundColor Green
  dotnet run --project .\ChillHub.csproj
} finally {
  Pop-Location
}
