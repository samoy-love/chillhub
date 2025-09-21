param(
    [switch]$Publish = $false,
    [string]$Configuration = "Release",
    [string]$Csproj = "launcher/ChillHub/ChillHub.csproj",
    [string]$Installer = "scripts/installer.nsi",
    [string]$MakensisPath,
    [string]$Runtime = "win-x64",
    [switch]$SelfContained,
    [switch]$NoCompress
)

$ErrorActionPreference = "Stop"

# Ensure Unicode I/O
try {
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($true)
    [Console]::InputEncoding  = [System.Text.UTF8Encoding]::new($true)
} catch {}

# Preflight: dotnet SDK and csproj path
try {
    $dc = Get-Command dotnet -ErrorAction Stop
    $dv = (& dotnet --version)
    if ($dv) { Write-Host "dotnet: $dv ($($dc.Path))" -ForegroundColor DarkCyan }
} catch { throw "dotnet SDK not found. Please install .NET 8 SDK and ensure 'dotnet' is in PATH." }

if (-not (Test-Path -LiteralPath $Csproj)) {
    throw "CSProj not found at '$Csproj'. Adjust -Csproj argument."
}

function Find-Makensis {
    param([string]$ExplicitPath)
    # Helper to expand a directory to candidate exe paths
    function Get-ExeFromDir([string]$dir) {
        if (-not $dir) { return $null }
        $exe1 = Join-Path $dir "makensis.exe"
        $exe2 = Join-Path $dir "makensisw.exe"
        return @($exe1, $exe2) | Where-Object { Test-Path $_ }
    }

    # 1) If explicit path provided, accept file; if it's a directory, search inside for exe
    if ($ExplicitPath) {
        if (Test-Path $ExplicitPath) {
            if ((Get-Item $ExplicitPath) -is [System.IO.DirectoryInfo]) {
                $fromDir = Get-ExeFromDir -dir $ExplicitPath
                if ($fromDir -and $fromDir.Count -gt 0) { return $fromDir[0] }
            } else {
                return $ExplicitPath
            }
        }
    }

    # 2) Try PATH (makensis or makensisw)
    $fromPath = @(
        (Get-Command makensis -ErrorAction SilentlyContinue | Select-Object -First 1).Path,
        (Get-Command makensisw -ErrorAction SilentlyContinue | Select-Object -First 1).Path
    ) | Where-Object { $_ -and (Test-Path $_) }
    if ($fromPath -and $fromPath.Count -gt 0) { return $fromPath[0] }

    # 3) Well-known install directories
    $knownDirs = @(
        "C:\Program Files (x86)\NSIS",
        "C:\Program Files\NSIS"
    )
    $fromKnown = $knownDirs | ForEach-Object { Get-ExeFromDir -dir $_ } | Where-Object { $_ }
    if ($fromKnown -and $fromKnown.Count -gt 0) { return $fromKnown[0] }

    # 4) Registry lookup
    $regKeys = @(
        'HKLM:\SOFTWARE\NSIS',
        'HKLM:\SOFTWARE\WOW6432Node\NSIS'
    )
    foreach ($rk in $regKeys) {
        try {
            $installDir = (Get-ItemProperty -Path $rk -ErrorAction SilentlyContinue).InstallDir
            if (-not $installDir) { $installDir = (Get-ItemProperty -Path $rk -ErrorAction SilentlyContinue).'(default)' }
            $fromReg = Get-ExeFromDir -dir $installDir
            if ($fromReg -and $fromReg.Count -gt 0) { return $fromReg[0] }
        } catch { }
    }

    throw "NSIS not found. Install NSIS 3.x (Typical) or supply -MakensisPath to makensis.exe. Looked in PATH and default locations like 'C:\\Program Files (x86)\\NSIS'."
}

Write-Host "[1/3] Restoring .NET packages..." -ForegroundColor Cyan
& dotnet restore $Csproj

if ($Publish) {
    Write-Host "[2/3] Publishing self-contained ($Configuration, $Runtime, SelfContained=$SelfContained)..." -ForegroundColor Cyan
    $sc = if ($SelfContained) { "true" } else { "false" }
    & dotnet publish $Csproj -c $Configuration -r $Runtime --self-contained $sc
    # Compute publish output path (informational)
    $ProjectDir = Split-Path -Parent $Csproj
    $PublishDir = Join-Path $ProjectDir "bin/$Configuration/net8.0-windows/$Runtime/publish"
    Write-Host "Publish output: $PublishDir" -ForegroundColor Cyan
} else {
    Write-Host "[2/3] Building ($Configuration)..." -ForegroundColor Cyan
    & dotnet build $Csproj -c $Configuration
}

$makensis = Find-Makensis -ExplicitPath $MakensisPath
# Resolve to full path and ensure string type
try { $makensis = (Resolve-Path -LiteralPath $makensis).Path } catch { }
if (-not $makensis -or -not (Test-Path $makensis)) { throw "makensis.exe not found or not accessible at '$MakensisPath'" }

# Resolve installer path as well
try { $installerPath = (Resolve-Path -LiteralPath $Installer).Path } catch { $installerPath = $Installer }

Write-Host "[3/3] Compiling NSIS installer with: `$makensis=`"$makensis`"; installer=`"$installerPath`"" -ForegroundColor Cyan

# Ensure prereqs dir exists (optional)
$prereqsDir = Join-Path (Split-Path $Installer -Parent) "prereqs"
if (!(Test-Path $prereqsDir)) { New-Item -ItemType Directory -Path $prereqsDir | Out-Null }

# Build NSIS args (default verbosity)
$nsisArgs = @('/INPUTCHARSET', 'UTF8')
if ($NoCompress) {
    Write-Host "NSIS: building without compression (fast dev build)" -ForegroundColor Yellow
    # Inject a directive override: SetCompress off (at compile-time)
    $nsisArgs += @('/XSetCompress off')
}
$nsisArgs += @("$installerPath")

& "$makensis" @nsisArgs

Write-Host "Done. Look for the generated installer (ChillHub-Setup.exe) near $Installer" -ForegroundColor Green

