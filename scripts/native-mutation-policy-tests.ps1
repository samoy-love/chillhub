$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'native-mutation-results.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('chillhub-mutation-policy-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory (Join-Path $root 'log') -Force | Out-Null
$scope = @(Get-NativeMutationScope)
foreach ($file in ($scope.file | Sort-Object -Unique)) {
    $path = Join-Path $root $file
    New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
    [IO.File]::WriteAllText($path, "original`n")
}
$sourceHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("original`n"))).ToLowerInvariant()
$outcomes = @(@{ scenario='Baseline'; summary='Success' })
for ($i=0; $i -lt 175; $i++) {
    $entry = $scope[$i % $scope.Count]
    $function = $entry.function ?? 'fixture'
    $name = "$($entry.file):${i}:1: replace && with || in $function"
    $outcomes += @{ scenario=@{Mutant=@{name=$name; file=$entry.file}}; summary='CaughtMutant' }
}
$baseline = @{cargo_mutants_version='27.1.0';end_time='2026-11-11T00:00:01Z';total_mutants=175;caught=175;unviable=0;outcomes=$outcomes} | ConvertTo-Json -Depth 10
$passed = 0
function Assert-Policy {
    param([string]$Name,[scriptblock]$Change,[bool]$Accept)
    $data = $baseline | ConvertFrom-Json
    $review = @{exceptions=@()}
    $log = Join-Path $root 'log/baseline.log'
    'test result: ok. 175 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.25s' |
        Set-Content -LiteralPath $log -Encoding utf8NoBOM
    & $Change $data $review
    $data | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $root 'outcomes.json') -Encoding utf8NoBOM
    $review | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $root 'review.json') -Encoding utf8NoBOM
    $accepted = $false
    try { $null = Test-NativeMutationResults -Results (Join-Path $root 'outcomes.json') -Project $root -Review (Join-Path $root 'review.json'); $accepted = $true }
    catch { if ($Accept) { throw "${Name}: $_" } }
    if ($accepted -ne $Accept) { throw "$Name — мутационный гейт принял ошибочный результат." }
    $script:passed++
}
try {
    Assert-Policy 'caught mutations' {} $true
    Assert-Policy 'unknown survivor' {param($d,$r) $d.outcomes[1].summary='MissedMutant'} $false
    Assert-Policy 'reviewed exact source' {param($d,$r)
        $d.outcomes[1].summary='MissedMutant'
        $r.exceptions=@(@{name=$d.outcomes[1].scenario.Mutant.name;file='src/changelog.rs';sourceSha256=$sourceHash;reason='Synthetic equivalent for policy verification'})
    } $true
    Assert-Policy 'stale source review' {param($d,$r)
        $d.outcomes[1].summary='MissedMutant'
        $r.exceptions=@(@{name=$d.outcomes[1].scenario.Mutant.name;file='src/changelog.rs';sourceSha256=('0'*64);reason='Stale review'})
    } $false
    Assert-Policy 'timeout' {param($d,$r) $d.outcomes[1].summary='Timeout'} $false
    Assert-Policy 'broken baseline' {param($d,$r) $d.outcomes[0].summary='Failure'} $false
    Assert-Policy 'empty baseline' {
        'test result: ok. 0 passed; 0 failed; 0 ignored; 0 measured; 175 filtered out; finished in 0s' |
            Set-Content -LiteralPath (Join-Path $root 'log/baseline.log') -Encoding utf8NoBOM
    } $false
    Assert-Policy 'compile-time rejection' {param($d,$r)
        $d.outcomes[1] | Add-Member -NotePropertyName log_path -NotePropertyValue 'log/mutant.log'
        $d.outcomes[1].summary='Unviable'
        @('error[E0277]: broken mutation',' --> src/changelog.rs:10:2') |
            Set-Content -LiteralPath (Join-Path $root 'log/mutant.log') -Encoding utf8NoBOM
    } $true
    Assert-Policy 'broken build environment' {param($d,$r)
        $d.outcomes[1] | Add-Member -NotePropertyName log_path -NotePropertyValue 'log/mutant.log'
        $d.outcomes[1].summary='Unviable'
        'error: failed to run custom build command (missing resource)' |
            Set-Content -LiteralPath (Join-Path $root 'log/mutant.log') -Encoding utf8NoBOM
    } $false
    Assert-Policy 'unfinished run' {param($d,$r) $d.end_time=$null} $false
    Assert-Policy 'truncated results' {param($d,$r) $d.total_mutants=176} $false
    Assert-Policy 'changed tool' {param($d,$r) $d.cargo_mutants_version='0.0.0'} $false
    Assert-Policy 'missing module coverage' {param($d,$r)
        foreach($o in $d.outcomes){if($o.scenario -isnot [string] -and $o.scenario.Mutant.file -eq 'src/paths.rs'){$o.summary='Unviable'}}
    } $false
    Assert-Policy 'missing policy in a covered module' {param($d,$r)
        foreach ($o in $d.outcomes) { if ($o.scenario.Mutant.name -like '* in trusted_url') { $o.summary='Unviable' } }
    } $false
    [ordered]@{ passed=$passed; failed=0 } | ConvertTo-Json
} finally {
    $resolved=[IO.Path]::GetFullPath($root)
    $allowed=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    if (!$resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -notlike 'chillhub-mutation-policy-*') { throw 'Unsafe policy scratch path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
