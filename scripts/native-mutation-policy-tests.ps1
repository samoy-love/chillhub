$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'native-mutation-results.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('chillhub-mutation-policy-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $root 'src') -Force | Out-Null
$files = @('src/changelog.rs','src/manifest.rs','src/paths.rs')
foreach ($file in $files) { [IO.File]::WriteAllText((Join-Path $root $file), "original`n") }
$sourceHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("original`n"))).ToLowerInvariant()
$outcomes = @(@{ scenario='Baseline'; summary='Success' })
for ($i=0; $i -lt 140; $i++) {
    $outcomes += @{ scenario=@{Mutant=@{name="mutation-$i"; file=$files[$i % 3]}}; summary='CaughtMutant' }
}
$baseline = @{cargo_mutants_version='27.1.0';end_time='2026-11-11T00:00:01Z';total_mutants=140;caught=140;unviable=0;outcomes=$outcomes} | ConvertTo-Json -Depth 10
$passed = 0
function Assert-Policy {
    param([string]$Name,[scriptblock]$Change,[bool]$Accept)
    $data = $baseline | ConvertFrom-Json
    $review = @{exceptions=@()}
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
        $r.exceptions=@(@{name='mutation-0';file='src/changelog.rs';sourceSha256=$sourceHash;reason='Synthetic equivalent for policy verification'})
    } $true
    Assert-Policy 'stale source review' {param($d,$r)
        $d.outcomes[1].summary='MissedMutant'
        $r.exceptions=@(@{name='mutation-0';file='src/changelog.rs';sourceSha256=('0'*64);reason='Stale review'})
    } $false
    Assert-Policy 'timeout' {param($d,$r) $d.outcomes[1].summary='Timeout'} $false
    Assert-Policy 'broken baseline' {param($d,$r) $d.outcomes[0].summary='Failure'} $false
    Assert-Policy 'unfinished run' {param($d,$r) $d.end_time=$null} $false
    Assert-Policy 'truncated results' {param($d,$r) $d.total_mutants=141} $false
    Assert-Policy 'changed tool' {param($d,$r) $d.cargo_mutants_version='0.0.0'} $false
    Assert-Policy 'missing module coverage' {param($d,$r)
        foreach($o in $d.outcomes){if($o.scenario -isnot [string] -and $o.scenario.Mutant.file -eq 'src/paths.rs'){$o.summary='Unviable'}}
    } $false
    [ordered]@{ passed=$passed; failed=0 } | ConvertTo-Json
} finally {
    $resolved=[IO.Path]::GetFullPath($root)
    $allowed=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    if (!$resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -notlike 'chillhub-mutation-policy-*') { throw 'Unsafe policy scratch path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
