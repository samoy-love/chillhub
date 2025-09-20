# Common environment setup for ChillHub dev
Param(
  [string]$ContentRoot
)

# Resolve repo root (directory containing this script is /scripts)
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ContentRoot -or $ContentRoot -eq '') {
  $ContentRoot = Join-Path $repoRoot 'content'
}

# Set env var for child processes
$env:CONTENT_ROOT = $ContentRoot
Write-Host "CONTENT_ROOT = $env:CONTENT_ROOT" -ForegroundColor Cyan
