param(
  [string]$ContentRoot
)

# Ensure Unicode I/O to avoid mojibake
try {
  [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($true)
  [Console]::InputEncoding  = [System.Text.UTF8Encoding]::new($true)
} catch {}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Split-Path -Parent $scriptDir

# Apply common env (CONTENT_ROOT)
& "$scriptDir\env.ps1" -ContentRoot $ContentRoot | Out-Host

Write-Host "Starting Admin server on :55777 ..." -ForegroundColor Green
Write-Host "Admin UI:  http://localhost:55777/admin" -ForegroundColor Yellow
Write-Host "Health:    http://localhost:55777/admin/health" -ForegroundColor DarkYellow

# Show go version and full path if available
try {
  $gc = Get-Command go -ErrorAction SilentlyContinue
  $gv = (& go version)
  if ($gv) {
    if ($gc -and $gc.Path) { Write-Host ("go: $gv (path: $($gc.Path))") -ForegroundColor Cyan }
    else { Write-Host ("go: $gv") -ForegroundColor Cyan }
  }
} catch {}

Push-Location (Join-Path $repoRoot 'server')
try {
  go run ./cmd/admin
} finally {
  Pop-Location
}
