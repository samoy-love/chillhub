[CmdletBinding(DefaultParameterSetName = 'Build')]
param(
    [Parameter(ParameterSetName = 'Check')][switch]$Check,
    [Parameter(ParameterSetName = 'Test')][switch]$Test,
    [Parameter(ParameterSetName = 'Test')][string]$TestTarget,
    [Parameter(ParameterSetName = 'Format')][switch]$FormatCheck,
    [Parameter(ParameterSetName = 'Clippy')][switch]$Clippy,
    [ValidateSet('All','Launcher','Helpers')][string]$Component = 'All',
    [switch]$Benchmark,
    [switch]$UpdateLock,
    [switch]$Library,
    [string]$Target,
    [string]$CargoPath,
    [string]$LlvmMingw,
    [ValidateRange(1, 64)][int]$Jobs = 6
)
$ErrorActionPreference = 'Stop'
# All toolchain changes are process-local and restored for callers.
$keys = @('PATH','RUSTUP_HOME','CARGO_HOME','CARGO_BUILD_JOBS','RUSTFLAGS','CHILLHUB_LLVM_MINGW','CARGO_TARGET_X86_64_PC_WINDOWS_GNU_LINKER')
$saved = @{}; foreach ($key in $keys) { $saved[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
Push-Location $PSScriptRoot
try {
    if (!$CargoPath) {
        $installed = Get-Command cargo.exe -ErrorAction SilentlyContinue
        if ($installed) { $CargoPath = $installed.Source }
        else {
            $portable = Join-Path $env:USERPROFILE '.codex/tools/rust-chillhub'
            $CargoPath = Join-Path $portable 'cargo/bin/cargo.exe'
            if (!(Test-Path -LiteralPath $CargoPath)) { throw 'Установите Rust MSVC с Windows SDK или задайте -CargoPath.' }
            $env:RUSTUP_HOME = Join-Path $portable 'rustup'
            $env:CARGO_HOME = Join-Path $portable 'cargo'
        }
    }
    $CargoPath = (Resolve-Path -LiteralPath $CargoPath).Path
    $env:PATH = (Split-Path $CargoPath) + ';' + $env:PATH
    $rustVersion = (& (Join-Path (Split-Path $CargoPath) 'rustc.exe') -vV | Out-String)
    if ($LASTEXITCODE) { throw 'Не удалось определить Rust toolchain.' }
    $hostTriple = [regex]::Match($rustVersion, '(?m)^host: (.+)$').Groups[1].Value.Trim()
    $effectiveTarget = if ($Target) { $Target } else { $hostTriple }
    if ($effectiveTarget -notin @('x86_64-pc-windows-gnu','x86_64-pc-windows-msvc')) { throw "Неподдерживаемая цель: $effectiveTarget" }
    $env:CARGO_BUILD_JOBS = "$Jobs"
    if ($effectiveTarget.EndsWith('-gnu')) {
        if (!$LlvmMingw) { $LlvmMingw = Join-Path $env:USERPROFILE '.codex/tools/llvm-mingw-20261104-ucrt-x86_64/bin' }
        $LlvmMingw = (Resolve-Path -LiteralPath $LlvmMingw).Path.Replace('\','/')
        foreach ($tool in @('ld.lld.exe','llvm-dlltool.exe','llvm-ar.exe','x86_64-w64-mingw32-windres.exe')) {
            if (!(Test-Path -LiteralPath "$LlvmMingw/$tool")) { throw "Не найден $LlvmMingw/$tool" }
        }
        $env:PATH = "$LlvmMingw;" + $env:PATH
        $env:CHILLHUB_LLVM_MINGW = $LlvmMingw
        $env:CARGO_TARGET_X86_64_PC_WINDOWS_GNU_LINKER = "$LlvmMingw/ld.lld.exe"
        # LLD resolves raw-dylib imports; Rust supplies the matching GNU CRT.
        $env:RUSTFLAGS = "-C linker-flavor=ld -C link-self-contained=yes -C dlltool=$LlvmMingw/llvm-dlltool.exe"
    }
    if ($UpdateLock) {
        if (Test-Path -LiteralPath Cargo.lock) { & $CargoPath update --workspace }
        else { & $CargoPath generate-lockfile }
        if ($LASTEXITCODE) { throw 'Не удалось обновить зависимости.' }
    }
    if ($FormatCheck) {
        & $CargoPath fmt --package chillhub-native -- --check
        if ($LASTEXITCODE) { throw "Cargo fmt завершился с ошибкой $LASTEXITCODE" }
        # Preserve the upstream formatting of the pinned renderer snapshot.
        & (Join-Path (Split-Path $CargoPath) 'rustfmt.exe') --edition 2024 --check vendor/i-slint-renderer-software/smooth_images.rs vendor/i-slint-renderer-software/frame_timings.rs
        if ($LASTEXITCODE) { throw 'Не отформатированы локальные расширения renderer.' }
    } else {
        if ($TestTarget -and $Component -eq 'Helpers') { throw 'Integration TestTarget требует Launcher.' }
        $components = if ($TestTarget) { @('Launcher') } elseif ($Component -eq 'All') { @('Launcher','Helpers') } else { @($Component) }
        foreach ($part in $components) {
            # Separate invocations are intentional: Cargo unifies dependency features
            # within one build, which would pull WGPU/OpenGL into both small helpers.
            $features = if ($part -eq 'Launcher') { if ($Benchmark) { 'benchmark' } else { 'launcher-ui' } } else { 'helper-ui' }
            $argsList = if ($Clippy) { @('clippy','--release') } elseif ($Check) { @('check','--release') } elseif ($Test) { @('test','--release') } else { @('build','--release') }
            $argsList = @($argsList)
            $argsList += @('--locked','--no-default-features','--features',$features)
            if ($part -eq 'Helpers') {
                if ($Clippy) { $argsList += '--all-targets' }
                elseif ($Test -or $Check -or $Library) { $argsList += @('--lib','--bins') }
                else { $argsList += @('--bin','ChillHub-Updater','--bin','ChillHub-SecurityHelper') }
            } elseif ($Library) { $argsList += '--lib' }
            elseif ($TestTarget) { $argsList += @('--test',$TestTarget) }
            elseif ($Clippy -or $Check) { $argsList += '--all-targets' }
            elseif (!$Test) { $argsList += @('--bin','ChillHub') }
            if ($Target) { $argsList += @('--target',$Target) }
            if ($Clippy) { $argsList += @('--','-D','warnings') }
            & $CargoPath @argsList
            if ($LASTEXITCODE) { throw "Cargo $part завершился с ошибкой $LASTEXITCODE" }
            if ($Test -and $part -eq 'Launcher' -and !$TestTarget) {
                $samplerArgs = @('test','--release','--locked','-p','i-slint-renderer-software','--features','smooth-images','--lib','smooth_images::tests')
                if ($Target) { $samplerArgs += @('--target',$Target) }
                & $CargoPath @samplerArgs
                if ($LASTEXITCODE) { throw 'Не прошли проверки масштабирования изображений.' }
            }
            if ($part -eq 'Helpers' -and !$Test -and !$Check -and !$Clippy -and !$Library) {
                $treeArgs=@('tree','--locked','--no-default-features','--features','helper-ui','--edges','normal','--prefix','none','--target',$effectiveTarget)
                $runtimeGraph=& $CargoPath @treeArgs
                if($LASTEXITCODE){throw 'Не удалось проверить зависимости помощников.'}
                if($runtimeGraph -match '^(wgpu(?:-[^ ]+)?|naga|glutin(?:-[^ ]+)?|glow|femtovg|i-slint-renderer-femtovg|webview2-com(?:-sys)?) v'){throw 'В помощники вернулся тяжёлый GPU renderer или WebView2.'}
                New-Item -ItemType Directory -Force -Path (Join-Path $PSScriptRoot 'dist') | Out-Null
                $runtimeGraph | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'dist/helper-runtime-deps.txt') -Encoding utf8NoBOM
            }
        }
    }
    if (!$Check -and !$Test -and !$Library -and !$FormatCheck -and !$Clippy) {
        $metadata = & $CargoPath metadata --no-deps --format-version 1 --locked | ConvertFrom-Json
        if ($LASTEXITCODE) { throw 'Не удалось прочитать сведения сборки.' }
        $output = $metadata.target_directory
        if ($Target) { $output = Join-Path $output $Target }
        $output = Join-Path $output 'release'
        New-Item -ItemType Directory -Force -Path (Join-Path $PSScriptRoot 'dist') | Out-Null
        $binaryNames = if($Component -eq 'Launcher'){@('ChillHub.exe')}elseif($Component -eq 'Helpers'){@('ChillHub-Updater.exe','ChillHub-SecurityHelper.exe')}else{@('ChillHub.exe','ChillHub-Updater.exe','ChillHub-SecurityHelper.exe')}
        $binaries=@($binaryNames | ForEach-Object {$path=Join-Path $output $_;[ordered]@{name=$_;bytes=(Get-Item -LiteralPath $path).Length;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}})
        [ordered]@{ schemaVersion=2; version=($metadata.packages | Where-Object name -eq 'chillhub-native').version; target=$effectiveTarget; binaryDirectory=$output; binaries=$binaries; component=$Component; benchmark=[bool]$Benchmark; helperRenderer='software'; rust=$rustVersion.Trim(); builtUtc=[DateTime]::UtcNow.ToString('o') } |
            ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'dist/build-info.json') -Encoding utf8NoBOM
    }
} finally {
    Pop-Location
    foreach ($key in $keys) {
        # PowerShell coerces $null to an empty string for this .NET overload.
        # Newer runtimes preserve that empty variable instead of removing it.
        if ($null -eq $saved[$key]) {
            Remove-Item -LiteralPath "Env:$key" -ErrorAction SilentlyContinue
        } else {
            [Environment]::SetEnvironmentVariable($key, $saved[$key], 'Process')
        }
    }
}
