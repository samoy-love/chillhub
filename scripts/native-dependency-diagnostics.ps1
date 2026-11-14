[CmdletBinding()]
param([Parameter(Mandatory)][string]$TestLog)
$ErrorActionPreference = 'Stop'
# Read PE imports without executing the failed program or loading its DLLs.
$text = Get-Content -LiteralPath $TestLog -Raw
$matches = [regex]::Matches($text, 'process didn''t exit successfully: `([^`]+\.exe)`')
if (!$matches.Count) { return }
$binary = $matches[$matches.Count - 1].Groups[1].Value
if (!(Test-Path -LiteralPath $binary -PathType Leaf)) { return }
$dumpbin = Get-Command dumpbin.exe -ErrorAction SilentlyContinue
if (!$dumpbin) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($installation) {
            $candidate = Get-ChildItem -Path (Join-Path $installation 'VC/Tools/MSVC/*/bin/Hostx64/x64/dumpbin.exe') -File |
                Sort-Object FullName -Descending | Select-Object -First 1
            if ($candidate) { $dumpbin = $candidate }
        }
    }
}
if (!$dumpbin) { Write-Output 'PE diagnostics: dumpbin is unavailable.'; return }
$tool = if ($dumpbin.Source) { $dumpbin.Source } else { $dumpbin.FullName }
$roots = @((Split-Path $binary), [Environment]::SystemDirectory, $env:WINDIR) + @($env:PATH -split ';' | Where-Object { $_ })
$pending = [Collections.Generic.Queue[string]]::new()
$pending.Enqueue($binary)
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
while ($pending.Count -and $seen.Count -lt 128) {
    $file = $pending.Dequeue()
    if (!$seen.Add($file)) { continue }
    $output = & $tool /nologo /dependents $file 2>&1
    if ($LASTEXITCODE) { Write-Output "PE diagnostics failed: $file"; continue }
    foreach ($line in $output) {
        if ([string]$line -notmatch '^\s+([\w.-]+\.dll)\s*$') { continue }
        $name = $Matches[1]
        if ($name -match '^(api-ms-|ext-ms-)') { continue }
        $resolved = $null
        foreach ($root in $roots) {
            $candidate = Join-Path $root $name
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $resolved = $candidate; break }
        }
        if ($resolved) {
            Write-Output "PE dependency: $([IO.Path]::GetFileName($file)) -> $name [found]"
            $pending.Enqueue($resolved)
        } else {
            Write-Output "PE dependency: $([IO.Path]::GetFileName($file)) -> $name [NOT FOUND]"
        }
    }
}
