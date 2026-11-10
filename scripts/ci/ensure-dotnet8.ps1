[CmdletBinding()]
param([string]$ToolRoot)
$ErrorActionPreference = 'Stop'

# The installer and its isolated AOT updater must use the same patched runtime.
# SDK 8.0.425 selected ILCompiler 8.0.31 (GHSA-p3jp-9v83-76w9); 8.0.33 fixes it.
# Source: https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json
$sdkVersion = '8.0.427'
$runtimeVersion = '8.0.33'
$source = "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdkVersion/dotnet-sdk-$sdkVersion-win-x64.zip"
$downloadSources = @("https://dotnetcli.blob.core.windows.net/dotnet/Sdk/$sdkVersion/dotnet-sdk-$sdkVersion-win-x64.zip", $source)
$expected = 'a4dd6eb2bf2a2f6fb10997d3e044a229b23ad96a5c86d20494687e34ee73b537d7bd14488ce14cded6917f6778da4d66360e2734020b7ef487d0f29ab144489d'
if (!$ToolRoot) { $ToolRoot = Join-Path $env:LOCALAPPDATA "chillhub-tools/dotnet-$sdkVersion" }
$ToolRoot = [IO.Path]::GetFullPath($ToolRoot)
$dotnet = Join-Path $ToolRoot 'dotnet.exe'
$bundled = Join-Path $ToolRoot "sdk/$sdkVersion/Microsoft.NETCoreSdk.BundledVersions.props"
if (!(Test-Path -LiteralPath $bundled) -or !(Test-Path -LiteralPath $dotnet)) {
    New-Item -ItemType Directory -Path $ToolRoot -Force | Out-Null
    $archive = Join-Path $ToolRoot "dotnet-sdk-$sdkVersion-win-x64.zip"
    if (!(Test-Path -LiteralPath $archive)) {
        $temporary = "$archive.$([Guid]::NewGuid().ToString('N')).tmp"
        try {
            $downloaded = $false
            foreach ($url in $downloadSources) {
                try {
                    # curl streams large SDK archives without PowerShell's progress
                    # buffering and has separate connection/whole-download limits.
                    if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
                        & curl.exe --fail --location --http1.1 --silent --show-error --connect-timeout 20 --max-time 600 --output $temporary $url
                        if ($LASTEXITCODE -ne 0) { throw "SDK download failed (curl exit $LASTEXITCODE)." }
                    } else {
                        Invoke-WebRequest -Uri $url -OutFile $temporary -TimeoutSec 600
                    }
                    $downloaded = $true
                    break
                } catch {
                    if ($url -eq $downloadSources[-1]) { throw }
                    Write-Warning 'Первый официальный источник SDK недоступен; пробуем резервный Microsoft.'
                }
            }
            if (!$downloaded) { throw 'Не удалось скачать .NET SDK.' }
            if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA512).Hash -ine $expected) { throw '.NET SDK archive SHA512 mismatch.' }
            Move-Item -LiteralPath $temporary -Destination $archive -Force
        } finally {
            if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
        }
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ine $expected) { throw '.NET SDK archive SHA512 mismatch.' }
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $ToolRoot, $true)
}

# Do not alter global PATH, registry or the installed .NET SDK. Every caller gets
# the exact executable path; metadata below also rejects an incomplete cache.
$actual = (& $dotnet --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $actual -ne $sdkVersion) { throw "Expected .NET SDK $sdkVersion, got '$actual'." }
[xml]$metadata = Get-Content -LiteralPath $bundled -Raw
$compiler = @($metadata.Project.ItemGroup.KnownILCompilerPack | Where-Object { $_.TargetFramework -eq 'net8.0' })
if ($compiler.Count -ne 1 -or $compiler[0].ILCompilerPackVersion -ne $runtimeVersion) { throw 'The SDK does not contain the expected patched .NET 8 AOT compiler.' }
if (!(Test-Path -LiteralPath (Join-Path $ToolRoot "shared/Microsoft.NETCore.App/$runtimeVersion/System.Private.CoreLib.dll"))) { throw 'The patched .NET 8 runtime is missing from the SDK.' }
[ordered]@{sdk=$sdkVersion;runtime=$runtimeVersion;source=$source;downloadSources=$downloadSources;archiveSha512=$expected;verifiedUtc=[DateTime]::UtcNow.ToString('o')} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ToolRoot 'source.json') -Encoding utf8NoBOM
$dotnet
