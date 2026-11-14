[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repo 'launcher/ChillHub.Native'
$id = [Guid]::NewGuid().ToString('N')
$scratch = Join-Path ([IO.Path]::GetTempPath()) "chillhub-mutations-$id"
$snapshot = Join-Path $scratch 'launcher/ChillHub.Native'
New-Item -ItemType Directory -Force -Path $snapshot,(Join-Path $scratch 'scripts') | Out-Null
# Mutations never touch the working tree. Copy only inputs of the core test build.
foreach ($name in @('Cargo.toml','Cargo.lock','build.rs','src','tests','Data','ui','vendor')) {
    Copy-Item -LiteralPath (Join-Path $project $name) -Destination $snapshot -Recurse
}
foreach ($name in @('app.ico','native-app.manifest')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $scratch 'scripts')
}
$result = Join-Path $project "dist/mutations/$id"
New-Item -ItemType Directory -Force -Path $result | Out-Null
& cargo mutants --dir $snapshot --in-place --no-default-features --file src/changelog.rs --file src/manifest.rs --file src/paths.rs --build-timeout 900 --timeout 60 -- --lib
$code = $LASTEXITCODE
$outcomes = Join-Path $snapshot 'mutants.out/outcomes.json'
if (!(Test-Path -LiteralPath $outcomes)) { throw "Нет результатов мутаций (код $code)." }
Copy-Item -LiteralPath (Join-Path $snapshot 'mutants.out') -Destination $result -Recurse
if ($code -notin @(0,2)) { throw "Мутационный прогон аварийно завершился: $code." }
. (Join-Path $PSScriptRoot 'native-mutation-results.ps1')
$verdict = Test-NativeMutationResults -Results $outcomes -Project $snapshot -Review (Join-Path $repo 'tests/native-mutation-review.json')
$verdict | ConvertTo-Json -Depth 8 | Tee-Object -FilePath (Join-Path $result 'verdict.json')

# cargo-mutants uses 2 for reviewed survivors; the policy above is the gate.
# Clear it only after the complete report passes, for the Actions pwsh wrapper.
$global:LASTEXITCODE = 0
