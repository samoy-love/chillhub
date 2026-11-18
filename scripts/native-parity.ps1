[CmdletBinding()]
param([Parameter(Mandatory)][string]$TestLog, [string]$PackageManifest, [string]$Output, [switch]$RequireReleaseReady)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$contract = Get-Content -LiteralPath (Join-Path $repo 'tests/parity-contract.json') -Raw | ConvertFrom-Json
$log = Get-Content -LiteralPath $TestLog -Raw
$passed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($match in [regex]::Matches($log, '(?m)^test ([\w:]+) \.\.\. ok\s*$')) { [void]$passed.Add($match.Groups[1].Value) }
if (!$passed.Count -or $log -match '(?m)^test result: FAILED') { throw 'Нет успешного полного журнала Rust tests.' }
$packageValid = $false
if ($PackageManifest) {
    $package = Get-Content -LiteralPath $PackageManifest -Raw | ConvertFrom-Json
    $null = & (Join-Path $PSScriptRoot 'native-verify-package.ps1') -Directory $package.payload -Version $package.version
    $packageValid=$true
}
$seen = @{}
$results = @($contract.checks | ForEach-Object {
    if ($seen.ContainsKey($_.id)) { throw "Повторяющийся check id: $($_.id)" }; $seen[$_.id]=$true
    foreach ($path in $_.original) { if (!(Test-Path -LiteralPath (Join-Path $repo $path))) { throw "Нет baseline source: $path" } }
    $missing = @($_.tests | Where-Object { !$passed.Contains($_) })
    [ordered]@{ id=$_.id; automated=if($missing.Count){'missing-or-failed'}elseif($_.tests.Count){'passed'}else{'not-applicable'}; missingTests=$missing; manual=$_.manual; disposition=$_.disposition; package=if($_.packageCheck){$packageValid}else{$null} }
})
$failures = @($results | Where-Object { $_.automated -eq 'missing-or-failed' -or $_.package -eq $false })
$pending = @($results | Where-Object { $_.manual -or $_.disposition -eq 'intentional-legacy-omission' })
$report = [ordered]@{ schemaVersion=1; baseline=$contract.baseline; checkedUtc=[DateTime]::UtcNow.ToString('o'); uniquePassedTests=$passed.Count; automatedValid=($failures.Count -eq 0); releaseReady=($failures.Count -eq 0 -and $pending.Count -eq 0); pendingReviewCount=$pending.Count; checks=$results }
$json=$report | ConvertTo-Json -Depth 8
if ($Output) { $json | Set-Content -LiteralPath $Output -Encoding utf8NoBOM }
$json
if ($failures.Count -or ($RequireReleaseReady -and $pending.Count)) { throw 'Контракт паритета не выполнен; смотрите missingTests и pendingReviewCount.' }
