# Shared by the mutation runner and its fail-closed policy tests.
function Test-NativeMutationResults {
    param(
        [Parameter(Mandatory)][string]$Results,
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$Review
    )
    $ErrorActionPreference = 'Stop'
    $data = Get-Content -LiteralPath $Results -Raw | ConvertFrom-Json
    $reviewed = Get-Content -LiteralPath $Review -Raw | ConvertFrom-Json
    if ($data.cargo_mutants_version -ne '27.1.0' -or !$data.end_time) { throw 'Мутационный прогон не завершён или версия инструмента изменилась.' }
    $baselines = @($data.outcomes | Where-Object scenario -eq Baseline)
    if ($baselines.Count -ne 1 -or $baselines[0].summary -ne 'Success') { throw 'Исходные тесты не прошли.' }
    $mutants = @($data.outcomes | Where-Object { $_.scenario -isnot [string] })
    if ($data.total_mutants -ne $mutants.Count -or $mutants.Count -lt 140) { throw 'Мутационный прогон неполный.' }
    foreach ($file in @('src/changelog.rs','src/manifest.rs','src/paths.rs')) {
        if (!@($mutants | Where-Object { $_.scenario.Mutant.file -eq $file -and $_.summary -eq 'CaughtMutant' }).Count) {
            throw "Нет пойманных ошибок в $file."
        }
    }
    $accepted = @()
    foreach ($outcome in $mutants) {
        if ($outcome.summary -in @('CaughtMutant','Unviable')) { continue }
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
    [ordered]@{ tested=$mutants.Count; caught=$data.caught; unviable=$data.unviable; reviewedEquivalentOrInactive=$accepted; unexpectedMissed=0 }
}
