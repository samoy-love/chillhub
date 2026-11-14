# Shared by the legacy and Native installers. Downloading never runs the setup.
function Get-WebView2BootstrapperMetadata {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)
    $file = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $file.Length -lt 1024 -or $file.Length -gt 20MB) {
        throw 'Некорректный файл WebView2 bootstrapper.'
    }
    $stream = $file.OpenRead()
    try { if ($stream.ReadByte() -ne 0x4D -or $stream.ReadByte() -ne 0x5A) { throw 'WebView2 bootstrapper не является PE-файлом.' } }
    finally { $stream.Dispose() }
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.Status -ne 'Valid' -or !$signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Microsoft Corporation(?:,|$)') {
        throw "WebView2 bootstrapper не имеет действительной подписи Microsoft: $($signature.Status)."
    }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($file.FullName)
    if ($version.OriginalFilename -ne 'MicrosoftEdgeUpdateSetup.exe' -or $version.ProductName -ne 'Microsoft Edge Update') {
        throw 'Подписанный файл не является установщиком Microsoft Edge WebView2.'
    }
    [ordered]@{
        path = $file.FullName
        source = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703'
        bytes = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        fileVersion = $version.FileVersion
        signature = $signature.Status.ToString()
        signer = $signature.SignerCertificate.Subject
        signerThumbprint = $signature.SignerCertificate.Thumbprint
        executed = $false
    }
}

function Ensure-WebView2Bootstrapper {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RedistDir, [switch]$Required)
    $target = Join-Path $RedistDir 'MicrosoftEdgeWebview2Setup.exe'
    # Revalidate cached bytes too; a file's presence is not proof of provenance.
    if (Test-Path -LiteralPath $target) { return Get-WebView2BootstrapperMetadata -Path $target }
    New-Item -ItemType Directory -Force -Path $RedistDir | Out-Null
    $temporary = Join-Path $RedistDir ('webview2-' + [Guid]::NewGuid().ToString('N') + '.exe')
    try {
        Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $temporary -TimeoutSec 120
        $null = Get-WebView2BootstrapperMetadata -Path $temporary
        Move-Item -LiteralPath $temporary -Destination $target -Force
        Get-WebView2BootstrapperMetadata -Path $target
    } catch {
        if ($Required) { throw "Не удалось подготовить обязательный компонент WebView2: $($_.Exception.Message)" }
        Write-Warning "Нерелизная сборка без WebView2 bootstrapper: $($_.Exception.Message)"
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}
