[CmdletBinding()]
param([string]$Bootstrapper = (Join-Path $PSScriptRoot 'Redist/MicrosoftEdgeWebview2Setup.exe'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ensure-webview2-bootstrapper.ps1')
if (!(Test-Path -LiteralPath $Bootstrapper -PathType Leaf)) { throw 'Для всех четырёх проверок нужен официальный WebView2 bootstrapper.' }
$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$fixture = Join-Path $temporaryBase ('chillhub-bootstrapper-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$checks = [Collections.Generic.List[string]]::new()
function Expect-Rejection([scriptblock]$Action, [string]$Expected) {
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -like "*$Expected*") { $checks.Add($Expected); return }
        throw
    }
    throw "Принят недоверенный bootstrapper: $Expected"
}
try {
    $target = Join-Path $fixture 'MicrosoftEdgeWebview2Setup.exe'
    $bytes = [byte[]]::new(2048); $bytes[0] = 0x4D; $bytes[1] = 0x5A
    [IO.File]::WriteAllBytes($target, $bytes)
    Expect-Rejection { Ensure-WebView2Bootstrapper -RedistDir $fixture -Required } 'действительной подписи Microsoft'
    & {
        $null = Get-WebView2BootstrapperMetadata -Path $Bootstrapper
        $checks.Add('official Microsoft signature and product metadata')
        Copy-Item -LiteralPath $Bootstrapper -Destination $target -Force
        $cached = Ensure-WebView2Bootstrapper -RedistDir $fixture -Required
        if ($cached.sha256 -ne (Get-FileHash -LiteralPath $Bootstrapper -Algorithm SHA256).Hash) { throw 'Изменились проверенные cached bytes.' }
        $checks.Add('cached bootstrapper revalidated without download or execution')
        $bytes = [IO.File]::ReadAllBytes($target)
        $bytes[4096] = $bytes[4096] -bxor 1
        [IO.File]::WriteAllBytes($target, $bytes)
        Expect-Rejection { Ensure-WebView2Bootstrapper -RedistDir $fixture -Required } 'действительной подписи Microsoft'
    }
    if ($checks.Count -ne 4) { throw "Не все проверки bootstrapper выполнены: $($checks.Count)/4." }
    [ordered]@{passed=$checks.Count;checks=$checks;executablesStarted=0;runtimeInstalled=$false;realUserStateModified=$false} | ConvertTo-Json -Depth 4
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (!$resolved.StartsWith($temporaryBase + '\', [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -notlike 'chillhub-bootstrapper-test-*') { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
