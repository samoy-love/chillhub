[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [ValidateRange(0,100)][double]$MinimumLines = 76,
    [ValidateRange(0,100)][double]$MinimumBranches = 64
)
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
        $contents = [IO.File]::ReadAllText($source.FullName)
        if ($contents -match '(?m)^\s*#\[\s*test\s*\]') {
            throw "Перенесите тесты из $($source.Name) в отдельный файл *_tests.rs."
        }
        $testAttributes = [regex]::Matches($contents, '(?m)^[ \t]*#\[cfg\((?:test|all\(test\b[^\r\n]*\))\)\][ \t]*\r?\n')
        foreach ($attribute in $testAttributes) {
            $tail = $contents.Substring($attribute.Index + $attribute.Length)
            $declaration = [regex]::Match($tail, '\A(?:[ \t]*#\[[^\r\n]+\][ \t]*\r?\n)*[ \t]*([^\r\n]+)').Groups[1].Value
            # A test-only import, module declaration, or extern declaration contains no executable code.
            if ($declaration -notmatch '^(?:pub(?:\([^)]*\))?\s+)?(?:mod\s+\w+|use\s+.+|fn\s+[^{};]+);\s*$') {
                throw "Перенесите тестовые вспомогательные функции из $($source.Name) в отдельный файл *_tests.rs."
            }
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
    # The screenshot matrix has its own gate in native-windows. Its profile adds
    # no covered application lines, branches, regions, or functions.
    & cargo llvm-cov --branch --no-rustc-wrapper --target $target --coverage-target-only --locked --package chillhub-native --features helper-ui --all-targets --no-report -- --test-threads=1 --skip real_ui_headless_layout_and_actions
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
        'main.rs' = @{ Lines = 64; Branches = 51 }
        'controller.rs' = @{ Lines = 64; Branches = 50 }
        'controller/settings.rs' = @{ Lines = 92; Branches = 79 }
        'desktop.rs' = @{ Lines = 49; Branches = 41 }
        'controller/launch_presentation.rs' = @{ Lines = 95; Branches = 90 }
        # LLVM gives the parser's string match arms line records.
        'controller/ui_action.rs' = @{ Lines = 95 }
        'bin/security_helper.rs' = @{ Lines = 46; Branches = 49 }
        'bin/updater.rs' = @{ Lines = 60; Branches = 42 }
        'helper_runtime.rs' = @{ Lines = 76; Branches = 68 }
        'setup.rs' = @{ Lines = 66; Branches = 68 }
        'launch.rs' = @{ Lines = 89; Branches = 80 }
        'media.rs' = @{ Lines = 82; Branches = 67 }
        'playtime.rs' = @{ Lines = 92; Branches = 80 }
        'queue.rs' = @{ Lines = 85; Branches = 71 }
        'security.rs' = @{ Lines = 76; Branches = 75 }
        'transfer.rs' = @{ Lines = 84; Branches = 64 }
        'transfer/cleanup.rs' = @{ Lines = 75; Branches = 44 }
        'transfer/deferred.rs' = @{ Lines = 85; Branches = 77 }
        'update.rs' = @{ Lines = 78; Branches = 69 }
    }
    $failures = foreach ($name in $critical.Keys) {
        $metrics = $files[$name]
        $floor = $critical[$name]
        $requiresBranches = $floor.ContainsKey('Branches')
        if (!$metrics -or $metrics.lines.count -le 0 -or ($requiresBranches -and $metrics.branches.count -le 0)) {
            throw "Отчёт не содержит измерений для $name."
        }
        $branches = if ($metrics.branches.count -gt 0) {
            '{0,6:F2}% ветвей' -f $metrics.branches.percent
        } else {
            'ветви LLVM не измерены'
        }
        Write-Host ('{0,-35} {1,6:F2}% строк, {2}' -f $name, $metrics.lines.percent, $branches)
        if ($metrics.lines.percent -lt $floor.Lines -or ($requiresBranches -and $metrics.branches.percent -lt $floor.Branches)) {
            $required = "$($floor.Lines)% строк"
            if ($requiresBranches) { $required += " и $($floor.Branches)% ветвей" }
            "$name (нужно $required)"
        }
    }
    $report.totals | ConvertTo-Json -Depth 4
    if ($failures) { throw "Покрытие отдельных компонентов снизилось: $($failures -join ', ')." }
    if ($report.totals.lines.percent -lt $MinimumLines) { throw "Покрытие строк Native ниже $MinimumLines%." }
    if ($report.totals.branches.percent -lt $MinimumBranches) { throw "Покрытие ветвей Native ниже $MinimumBranches%." }
} finally {
    Pop-Location
    if ($null -eq $previousQa) { Remove-Item Env:CHILLHUB_UI_QA -ErrorAction SilentlyContinue }
    else { $env:CHILLHUB_UI_QA = $previousQa }
}
