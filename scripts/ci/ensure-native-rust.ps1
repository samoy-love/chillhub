[CmdletBinding()]
param([string]$ToolRoot)
$ErrorActionPreference = 'Stop'
# Dedicated CI tool directories; no changes to the user's default Rust or PATH.
if (!$ToolRoot) { $ToolRoot = Join-Path $env:LOCALAPPDATA 'chillhub-tools/rust-native-msvc' }
$ToolRoot = [IO.Path]::GetFullPath($ToolRoot)
$env:RUSTUP_HOME = Join-Path $ToolRoot 'rustup'
$env:CARGO_HOME = Join-Path $ToolRoot 'cargo'
$env:RUSTUP_TOOLCHAIN = '1.99.1-x86_64-pc-windows-msvc'
$toolBin = Join-Path $env:CARGO_HOME 'bin'
$rustup = Join-Path $toolBin 'rustup.exe'
if (!(Test-Path -LiteralPath $rustup -PathType Leaf)) {
    New-Item -ItemType Directory -Force -Path $ToolRoot | Out-Null
    $installer = Join-Path $ToolRoot 'rustup-init-1.29.3.exe'
    $source = 'https://static.rust-lang.org/rustup/archive/1.29.3/x86_64-pc-windows-msvc/rustup-init.exe'
    $expected = '38027d51d69a5d05493627a0634dbea52d6cd793c8f072d2373d3f4163eec7db'
    Invoke-WebRequest -Uri $source -OutFile $installer -TimeoutSec 180
    if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ine $expected) {
        throw 'Контрольная сумма официального rustup-init не совпала.'
    }
    & $installer -y --no-modify-path --default-host x86_64-pc-windows-msvc --default-toolchain none --profile minimal
    if ($LASTEXITCODE) { throw 'Не удалось подготовить отдельный rustup для Native CI.' }
}
$env:PATH = "$toolBin;$env:PATH"
& $rustup toolchain install $env:RUSTUP_TOOLCHAIN --profile minimal --component rustfmt --component clippy
if ($LASTEXITCODE) { throw 'Не удалось установить закреплённый Rust toolchain.' }
& (Join-Path $toolBin 'rustc.exe') -vV
if ($LASTEXITCODE) { throw 'Rust toolchain недоступен.' }
if ($env:GITHUB_ENV) {
    foreach ($name in @('RUSTUP_HOME','CARGO_HOME','RUSTUP_TOOLCHAIN')) {
        "$name=$([Environment]::GetEnvironmentVariable($name, 'Process'))" | Add-Content -LiteralPath $env:GITHUB_ENV
    }
}
if ($env:GITHUB_PATH) { $toolBin | Add-Content -LiteralPath $env:GITHUB_PATH }
