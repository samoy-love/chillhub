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
    foreach ($source in Get-ChildItem -LiteralPath (Join-Path $project 'src') -Filter '*.rs' -File -Recurse) {
        if ($source.DirectoryName -match '[/\\]tests(?:[/\\]|$)' -or $source.Name -match '(?:^|[_-])tests\.rs$') { continue }
        if (Select-String -LiteralPath $source.FullName -Pattern '^\s*#\[\s*test\s*\]' -Quiet) {
            throw "Перенесите тесты из $($source.Name) в отдельный файл *_tests.rs."
        }
    }
    & rustup component add llvm-tools-preview
    if ($LASTEXITCODE) { throw 'Не удалось подготовить LLVM для закреплённого Rust.' }
    $target = ((& rustc -vV) -match '^host: ' -replace '^host: ', '')[0]
    if ($LASTEXITCODE -or !$target) { throw 'Не удалось определить цель покрытия Rust.' }
    $env:CHILLHUB_UI_QA = '1'
    # A combined graph is used only for coverage. Release helpers remain separate
    # software-only builds, verified by build.ps1's dependency check.
    # --no-report disables cargo-llvm-cov's automatic clean. Cached instrumented
    # workspace artifacts can retain stale source mappings, so remove those while
    # keeping dependency artifacts from the shared target cache.
    & cargo llvm-cov clean --workspace
    if ($LASTEXITCODE) { throw 'Не удалось очистить инструментированные артефакты Native.' }
    # Instrumentation slows down loopback and UI fixtures. Each fixture can
    # still start its own competing workers; avoid contention between fixtures.
    # Cargo puts large Rust command lines on Windows in @argfiles. The llvm-cov
    # wrapper cannot see the crate name inside them, so it misses application
    # instrumentation. Use compiler flags directly, limiting them to the target.
    & cargo llvm-cov --branch --no-rustc-wrapper --target $target --coverage-target-only --locked --package chillhub-native --features helper-ui --all-targets --no-report -- --test-threads=1
    if ($LASTEXITCODE) { throw 'Не прошли инструментированные тесты Native.' }
    # Keep the same exclusions when cargo paths and LLVM paths use different
    # separators. Covered tests of private functions live in *_tests.rs files.
    $cargoHome = $env:CARGO_HOME ?? (Join-Path $env:USERPROFILE '.cargo')
    $cargoHome = [IO.Path]::GetFullPath($cargoHome).TrimEnd('\', '/') -replace '\\', '/'
    $cargoHomePattern = [regex]::Escape($cargoHome).Replace('/', '[/\\]')
    $vendorPattern = [regex]::Escape(($project -replace '\\', '/') + '/vendor').Replace('/', '[/\\]')
    $ignore = "^$cargoHomePattern[/\\](registry|git)[/\\]|^$vendorPattern[/\\]|[/\\]rustc[/\\][0-9a-f]+[/\\]library[/\\]"
    & cargo llvm-cov report --target $target --json --summary-only --ignore-filename-regex $ignore --output-path (Join-Path $OutputDirectory 'summary.json')
    if ($LASTEXITCODE) { throw 'Не удалось собрать JSON покрытия.' }
    & cargo llvm-cov report --target $target --lcov --ignore-filename-regex $ignore --output-path (Join-Path $OutputDirectory 'launcher.lcov')
    if ($LASTEXITCODE) { throw 'Не удалось собрать LCOV покрытия.' }
    $summary = Get-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Raw | ConvertFrom-Json
    $report = $summary.data[0]
    if (!$report -or $report.totals.lines.count -le 0) { throw 'Отчёт покрытия пуст.' }
    if ($report.totals.branches.count -le 0) { throw 'Отчёт покрытия не содержит измерений ветвей.' }
    $files = @{}
    $sourceRoot = ((Join-Path $project 'src') -replace '\\', '/') + '/'
    foreach ($file in $report.files) {
        $filename = $file.filename -replace '\\', '/'
        if (!$filename.StartsWith($sourceRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Покрытие включает исходник вне приложения: $filename."
        }
        $relative = $filename.Substring($sourceRoot.Length)
        if ($relative -match '(?:^|/)tests/|(?:^|/|[_-])tests\.rs$') {
            throw "Отчёт содержит строки теста: $relative."
        }
        $files[$relative] = $file.summary
    }
    # Keep critical components visible when the overall total improves.
    $critical = [ordered]@{
        'main.rs' = @{ Lines = 34; Branches = 20 }
        'controller.rs' = @{ Lines = 49; Branches = 37 }
        'bin/security_helper.rs' = @{ Lines = 20; Branches = 25 }
        'bin/updater.rs' = @{ Lines = 35; Branches = 5 }
        'launch.rs' = @{ Lines = 73; Branches = 55 }
        'playtime.rs' = @{ Lines = 80; Branches = 55 }
        'queue.rs' = @{ Lines = 82; Branches = 65 }
        'security.rs' = @{ Lines = 42; Branches = 43 }
        'transfer.rs' = @{ Lines = 63; Branches = 40 }
        'update.rs' = @{ Lines = 66; Branches = 58 }
    }
    $failures = foreach ($name in $critical.Keys) {
        $metrics = $files[$name]
        if (!$metrics -or $metrics.lines.count -le 0 -or $metrics.branches.count -le 0) {
            throw "Отчёт не содержит измерений для $name."
        }
        Write-Host ('{0,-24} {1,6:F2}% строк, {2,6:F2}% ветвей' -f $name, $metrics.lines.percent, $metrics.branches.percent)
        $floor = $critical[$name]
        if ($metrics.lines.percent -lt $floor.Lines -or $metrics.branches.percent -lt $floor.Branches) {
            "$name (нужно $($floor.Lines)% строк и $($floor.Branches)% ветвей)"
        }
    }
    $report.totals | ConvertTo-Json -Depth 4
    if ($failures) { throw "Покрытие отдельных компонентов снизилось: $($failures -join ', ')." }
    if ($report.totals.lines.percent -lt $MinimumLines) { throw "Покрытие строк Native ниже $MinimumLines%." }
} finally {
    Pop-Location
    if ($null -eq $previousQa) { Remove-Item Env:CHILLHUB_UI_QA -ErrorAction SilentlyContinue }
    else { $env:CHILLHUB_UI_QA = $previousQa }
}
