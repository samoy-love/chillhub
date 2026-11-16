# Shared by the mutation runner and its fail-closed policy tests.
function Get-NativeMutationScope {
    param([switch]$PoliciesOnly)
    if (!$PoliciesOnly) {
        foreach ($file in @('src/changelog.rs', 'src/manifest.rs', 'src/paths.rs')) {
            [pscustomobject]@{ file=$file; name=$file; function=$null; pattern=('^' + [regex]::Escape($file) + ':') }
        }
    }
    $policies = [ordered]@{
        'src/launch/doorstop.rs' = @('enabled_state', 'doorstop_config_valid')
        'src/launch/steam_handoff.rs' = @('same_executable_name')
        'src/security_session.rs' = @('validate_token')
        'src/transfer/cleanup.rs' = @('is_approved')
        'src/transfer/deferred.rs' = @('is_locked')
        'src/update.rs' = @('job_arguments', 'trusted_url')
    }
    foreach ($file in $policies.Keys) {
        foreach ($function in $policies[$file]) {
            $pattern = '^' + [regex]::Escape($file) + ':.*(?:replace ' + $function + ' ->| in ' + $function + '$)'
            [pscustomobject]@{ file=$file; name="$file / $function"; function=$function; pattern=$pattern }
        }
    }
}

function Test-NativeMutationResults {
    param(
        [Parameter(Mandatory)][string]$Results,
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$Review,
        [switch]$PoliciesOnly
    )
    $ErrorActionPreference = 'Stop'
    $data = Get-Content -LiteralPath $Results -Raw | ConvertFrom-Json
    $reviewed = Get-Content -LiteralPath $Review -Raw | ConvertFrom-Json
    if ($data.cargo_mutants_version -ne '27.1.0' -or !$data.end_time) { throw 'Мутационный прогон не завершён или версия инструмента изменилась.' }
    $baselines = @($data.outcomes | Where-Object scenario -eq Baseline)
    if ($baselines.Count -ne 1 -or $baselines[0].summary -ne 'Success') { throw 'Исходные тесты не прошли.' }
    $baselineLog = Join-Path (Split-Path $Results -Parent) 'log/baseline.log'
    if (!(Test-Path -LiteralPath $baselineLog -PathType Leaf)) { throw 'Журнал исходных тестов отсутствует.' }
    $passed = 0
    foreach ($line in Select-String -LiteralPath $baselineLog -Pattern '^test result: ok\. ([0-9]+) passed; 0 failed;') {
        $passed += [int]$line.Matches[0].Groups[1].Value
    }
    if ($passed -eq 0) { throw 'В исходной сборке не выполнено ни одного теста.' }
    $mutants = @($data.outcomes | Where-Object { $_.scenario -isnot [string] })
    $minimum = if ($PoliciesOnly) { 35 } else { 175 }
    if ($data.total_mutants -ne $mutants.Count -or $mutants.Count -lt $minimum) { throw 'Мутационный прогон неполный.' }
    $coverage = [ordered]@{}
    foreach ($entry in Get-NativeMutationScope -PoliciesOnly:$PoliciesOnly) {
        $matching = @($mutants | Where-Object { $_.scenario.Mutant.name -cmatch $entry.pattern })
        $caught = @($matching | Where-Object summary -eq 'CaughtMutant').Count
        if (!$caught) {
            throw "Нет пойманных ошибок в $($entry.name)."
        }
        $coverage[$entry.name] = [ordered]@{ tested=$matching.Count; caught=$caught }
    }
    $accepted = @()
    foreach ($outcome in $mutants) {
        if ($outcome.summary -eq 'CaughtMutant') { continue }
        if ($outcome.summary -eq 'Unviable') {
            $buildLog = Join-Path (Split-Path $Results -Parent) $outcome.log_path
            $diagnostics = Get-Content -LiteralPath $buildLog -Raw
            # Cargo-mutants counts missing SDKs, resources, and other infrastructure
            # failures as unviable too. Require a Rust source diagnostic.
            $buildFailed = $diagnostics -match '(?m)^error: (failed to run custom build command|linking with|could not execute)'
            $rustDiagnostic = $diagnostics -match '(?m)^\s*-->\s+src[/\\].+\.rs:\d+:\d+'
            if ($buildFailed -or !$rustDiagnostic) {
                throw "Не удалось проверить мутацию из-за ошибки сборки: $($outcome.scenario.Mutant.name)."
            }
            continue
        }
        if ($outcome.summary -ne 'MissedMutant') { throw "Неприемлемый результат: $($outcome.summary)." }
        $mutant = $outcome.scenario.Mutant
        $entry = @($reviewed.exceptions | Where-Object name -ceq $mutant.name)
        if ($entry.Count -ne 1 -or !$entry[0].reason -or $entry[0].file -cne $mutant.file) {
            throw "Тесты не поймали новую ошибку: $($mutant.name)"
        }
        # The exact source, including guards and cfg boundaries, was reviewed.
        # Normalizing newlines keeps the check stable on Git's LF/CRLF checkouts.
        $source = [IO.File]::ReadAllText((Join-Path $Project $entry[0].file)).Replace("`r`n", "`n")
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($source))).ToLowerInvariant()
        if ($hash -cne $entry[0].sourceSha256) { throw "Обоснование мутации устарело: $($mutant.name)" }
        $accepted += [ordered]@{ name=$mutant.name; reason=$entry[0].reason }
    }
    [ordered]@{ baselinePassedTests=$passed; tested=$mutants.Count; caught=$data.caught; unviable=$data.unviable; scopes=$coverage; reviewedEquivalentOrInactive=$accepted; unexpectedMissed=0 }
}
