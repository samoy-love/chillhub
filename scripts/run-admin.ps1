param(
  [string]$ContentRoot
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
& "$scriptDir\env.ps1" -ContentRoot $ContentRoot | Out-Host

Push-Location "$scriptDir\..\server"
Write-Host "Starting Admin server on :55777 ..." -ForegroundColor Green
Write-Host "Tip: open http://localhost:55777/admin" -ForegroundColor Yellow
# Run admin server
go run ./cmd/admin
Pop-Location
