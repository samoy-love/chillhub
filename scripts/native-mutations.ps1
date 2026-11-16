[CmdletBinding()]
param([switch]$PoliciesOnly)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'native-mutation-results.ps1')
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repo 'launcher/ChillHub.Native'
$id = [Guid]::NewGuid().ToString('N')
$temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$scratch = Join-Path $temp "chillhub-mutations-$id"
$snapshot = Join-Path $scratch 'launcher/ChillHub.Native'
$previousTarget = $env:CARGO_TARGET_DIR
$targetLease = $null
try {
    if ($previousTarget) {
        $target = [IO.Path]::GetFullPath($previousTarget)
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        try {
            $targetLease = [IO.File]::Open((Join-Path $target '.native-mutations.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
        } catch [IO.IOException] {
            throw "Кэш сборки мутаций уже занят. Для параллельного прогона задайте другой CARGO_TARGET_DIR."
        }
        $env:CARGO_TARGET_DIR = $target
    }
    New-Item -ItemType Directory -Force -Path $snapshot,(Join-Path $scratch 'scripts') | Out-Null
    # Mutations never touch the working tree. Copy only inputs of the core test build.
    foreach ($name in @('Cargo.toml','Cargo.lock','build.rs','src','tests','Data','ui','vendor')) {
        Copy-Item -LiteralPath (Join-Path $project $name) -Destination $snapshot -Recurse
    }
    foreach ($name in @('app.ico','native-app.manifest')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $scratch 'scripts')
    }
    # A previous snapshot can have the same Cargo target identity and newer
    # timestamps. Keep dependency artifacts, but always rebuild this package.
    & cargo clean --locked --manifest-path (Join-Path $snapshot 'Cargo.toml') --package chillhub-native
    if ($LASTEXITCODE) { throw 'Не удалось очистить сборку перед мутациями.' }
    $result = Join-Path $project "dist/mutations/$id"
    New-Item -ItemType Directory -Force -Path $result | Out-Null
    $scope = @(Get-NativeMutationScope -PoliciesOnly:$PoliciesOnly)
    $files = foreach ($file in ($scope.file | Sort-Object -Unique)) { '--file'; $file }
    $include = $scope.pattern -join '|'
    # Playtime's real 15-second heartbeat runs in normal tests and coverage.
    # It would add nearly 50 minutes to this run on its own, outside the mutation scope.
    $heartbeat = 'playtime::tests::reconciled_live_process_has_one_watcher_and_updates_its_heartbeat'
    & cargo mutants --dir $snapshot --in-place --no-default-features @files --re $include --cargo-arg=--locked --build-timeout 900 --timeout 60 -- --lib -- --skip $heartbeat
    $code = $LASTEXITCODE
    $outcomes = Join-Path $snapshot 'mutants.out/outcomes.json'
    if (!(Test-Path -LiteralPath $outcomes)) { throw "Нет результатов мутаций (код $code)." }
    Copy-Item -LiteralPath (Join-Path $snapshot 'mutants.out') -Destination $result -Recurse
    if ($code -notin @(0,2)) { throw "Мутационный прогон аварийно завершился: $code." }
    $verdict = Test-NativeMutationResults -Results $outcomes -Project $snapshot -Review (Join-Path $repo 'tests/native-mutation-review.json') -PoliciesOnly:$PoliciesOnly
    $verdict | ConvertTo-Json -Depth 8 | Tee-Object -FilePath (Join-Path $result 'verdict.json')
} finally {
    if ($targetLease) { $targetLease.Dispose() }
    if ($previousTarget) { $env:CARGO_TARGET_DIR = $previousTarget }
    $resolved = (Resolve-Path -LiteralPath $scratch -ErrorAction SilentlyContinue).Path
    if ($resolved) {
        if ((Split-Path $resolved -Parent) -ine $temp -or (Split-Path $resolved -Leaf) -cne "chillhub-mutations-$id") {
            throw 'Временный каталог мутаций находится вне ожидаемого пути.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

# cargo-mutants uses 2 for reviewed survivors; the policy above is the gate.
# Clear it only after the complete report passes, for the Actions pwsh wrapper.
$global:LASTEXITCODE = 0
