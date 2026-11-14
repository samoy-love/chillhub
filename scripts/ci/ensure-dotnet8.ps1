[CmdletBinding()]
param([string]$ToolRoot)
$ErrorActionPreference = 'Stop'

# The installer and its isolated AOT updater must use the same patched runtime.
# SDK 8.0.425 selected ILCompiler 8.0.31 (GHSA-p3jp-9v83-76w9); 8.0.33 fixes it.
# Source: https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json
$sdkVersion = '8.0.427'
$runtimeVersion = '8.0.33'
$source = "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdkVersion/dotnet-sdk-$sdkVersion-win-x64.zip"
$downloadSources = @($source, "https://dotnetcli.blob.core.windows.net/dotnet/Sdk/$sdkVersion/dotnet-sdk-$sdkVersion-win-x64.zip")
$expected = 'a4dd6eb2bf2a2f6fb10997d3e044a229b23ad96a5c86d20494687e34ee73b537d7bd14488ce14cded6917f6778da4d66360e2734020b7ef487d0f29ab144489d'
$archiveBytes = 285280040L

function Save-SdkArchive {
    param([string]$Destination)
    $curl = (Get-Command curl.exe -ErrorAction Stop).Source
    $chunkBytes = 4MB
    $chunkCount = [int][Math]::Ceiling($archiveBytes / [double]$chunkBytes)
    $chunkRoot = "$Destination.parts"
    New-Item -ItemType Directory -Path $chunkRoot -ErrorAction Stop | Out-Null
    $deadline = [DateTime]::UtcNow.AddMinutes(8)
    Write-Host ".NET SDK $sdkVersion`: скачивание $chunkCount фрагментов с проверкой диапазонов."
    try {
        $completed = @(0..($chunkCount - 1) | ForEach-Object -Parallel {
            $ErrorActionPreference = 'Stop'
            $index = $_
            $size = $using:chunkBytes
            $total = $using:archiveBytes
            $start = [long]$index * $size
            $end = [Math]::Min($start + $size - 1, $total - 1)
            $part = Join-Path $using:chunkRoot "$index.part"
            $headers = "$part.headers"
            $lastFailure = $null
            foreach ($url in $using:downloadSources) {
                if ([DateTime]::UtcNow -ge $using:deadline) { throw 'Истекло время скачивания .NET SDK.' }
                try {
                    $status = & $using:curl --fail --location --http1.1 --silent --show-error --connect-timeout 15 --max-time 60 --range "$start-$end" --dump-header $headers --output $part --write-out '%{http_code}' $url
                    if ($LASTEXITCODE -ne 0) { throw "curl exit $LASTEXITCODE" }
                    if ("$status".Trim() -ne '206') { throw "Expected HTTP206, got $status" }
                    $ranges = [regex]::Matches((Get-Content -LiteralPath $headers -Raw), '(?im)^Content-Range:\s*(bytes \d+-\d+/\d+)\s*$')
                    if ($ranges.Count -ne 1 -or $ranges[0].Groups[1].Value -ne "bytes $start-$end/$total") { throw 'Incorrect Content-Range.' }
                    if ((Get-Item -LiteralPath $part).Length -ne ($end - $start + 1)) { throw 'Incomplete SDK fragment.' }
                    $index
                    return
                } catch { $lastFailure = $_ }
            }
            throw "SDK fragment $index failed: $lastFailure"
        } -ThrottleLimit 4)
        if ($completed.Count -ne $chunkCount) { throw 'Not all SDK fragments were downloaded.' }
        $output = [IO.File]::Create($Destination)
        try {
            for ($index = 0; $index -lt $chunkCount; $index++) {
                $partStream = [IO.File]::OpenRead((Join-Path $chunkRoot "$index.part"))
                try { $partStream.CopyTo($output) } finally { $partStream.Dispose() }
            }
        } finally { $output.Dispose() }
        if ((Get-Item -LiteralPath $Destination).Length -ne $archiveBytes) { throw 'Incomplete SDK archive.' }
        if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA512).Hash -ine $expected) { throw '.NET SDK archive SHA512 mismatch.' }
    } finally {
        # This unique task directory contains only our flat download fragments.
        Get-ChildItem -LiteralPath $chunkRoot -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
        Remove-Item -LiteralPath $chunkRoot -Force
    }
}

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
            Save-SdkArchive -Destination $temporary
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
