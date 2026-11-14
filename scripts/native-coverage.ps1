[CmdletBinding()]
param([string]$OutputDirectory, [ValidateRange(0,100)][double]$MinimumLines = 70)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../launcher/ChillHub.Native'))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $project 'dist/coverage' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$previousQa = $env:CHILLHUB_UI_QA
Push-Location $project
try {
    & rustup component add llvm-tools-preview
    if ($LASTEXITCODE) { throw 'Не удалось подготовить LLVM для закреплённого Rust.' }
    $env:CHILLHUB_UI_QA = '1'
    # A combined graph is used only for coverage. Release helpers remain separate
    # software-only builds, verified by build.ps1's dependency check.
    & cargo llvm-cov --locked --package chillhub-native --features helper-ui --all-targets --no-report
    if ($LASTEXITCODE) { throw 'Не прошли инструментированные тесты Native.' }
    # Ignore test sources and generated/dependency code, never application modules.
    $ignore = '[/\\](target|vendor|tests)[/\\]|[/\\]controller_tests\.rs$'
    & cargo llvm-cov report --json --summary-only --ignore-filename-regex $ignore --output-path (Join-Path $OutputDirectory 'summary.json')
    if ($LASTEXITCODE) { throw 'Не удалось собрать JSON покрытия.' }
    & cargo llvm-cov report --lcov --ignore-filename-regex $ignore --output-path (Join-Path $OutputDirectory 'launcher.lcov') --fail-under-lines $MinimumLines
    if ($LASTEXITCODE) { throw "Покрытие строк Native ниже $MinimumLines% или отчёт не создан." }
    $summary = Get-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Raw | ConvertFrom-Json
    if (!$summary.data -or $summary.data[0].totals.lines.count -le 0) { throw 'Отчёт покрытия пуст.' }
    $summary.data[0].totals | ConvertTo-Json -Depth 4
} finally {
    Pop-Location
    if ($null -eq $previousQa) { Remove-Item Env:CHILLHUB_UI_QA -ErrorAction SilentlyContinue }
    else { $env:CHILLHUB_UI_QA = $previousQa }
}
