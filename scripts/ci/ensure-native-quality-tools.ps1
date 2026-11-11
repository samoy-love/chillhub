[CmdletBinding()]
param([string]$ToolRoot, [ValidateSet('All','Coverage','Mutations','Audit')][string]$Component = 'All')
$ErrorActionPreference = 'Stop'
if (!$ToolRoot) { $ToolRoot = Join-Path $env:LOCALAPPDATA 'chillhub-tools/native-quality' }
$tools = @(
    @{ Component='Coverage'; Name='cargo-llvm-cov'; Version='0.9.1'; Url='https://github.com/taiki-e/cargo-llvm-cov/releases/download/v0.9.1/cargo-llvm-cov-x86_64-pc-windows-msvc.zip'; Sha='656590cfa13bb7478595bbfdd3bc6bc075a4f7f3f0df5a439c1856cb03c5a4d2' },
    @{ Component='Mutations'; Name='cargo-mutants'; Version='27.1.0'; Url='https://github.com/sourcefrog/cargo-mutants/releases/download/v27.1.0/cargo-mutants-x86_64-pc-windows-msvc.zip'; Sha='2a2f00e47d4b458262a41501b0820aa26015fd35779903d2c8b30b2993f36791' },
    @{ Component='Audit'; Name='cargo-audit'; Version='0.23.0'; Url='https://github.com/rustsec/rustsec/releases/download/cargo-audit%2Fv0.23.0/cargo-audit-x86_64-pc-windows-msvc-v0.23.0.zip'; Sha='aafeb61206730029601a72e669a12231f8674427e6248d02f6cb382cb36d56b4' }
)
foreach ($tool in $tools) {
    if ($Component -ne 'All' -and $Component -ne $tool.Component) { continue }
    $folder = Join-Path $ToolRoot "$($tool.Name)-$($tool.Version)"
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $archive = Join-Path $folder 'release.zip'
    if (!(Test-Path -LiteralPath $archive)) {
        Invoke-WebRequest -Uri $tool.Url -OutFile $archive -TimeoutSec 180
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $tool.Sha) {
        throw "Не совпала контрольная сумма $($tool.Name) $($tool.Version)."
    }
    Expand-Archive -LiteralPath $archive -DestinationPath $folder -Force
    $matches = @(Get-ChildItem -LiteralPath $folder -Recurse -File -Filter "$($tool.Name).exe")
    if ($matches.Count -ne 1) { throw "В архиве нет единственного $($tool.Name).exe." }
    $binaryFolder = $matches[0].DirectoryName
    $env:PATH = "$binaryFolder;$env:PATH"
    if ($env:GITHUB_PATH) { $binaryFolder | Add-Content -LiteralPath $env:GITHUB_PATH }
    & $matches[0].FullName ($tool.Name -replace '^cargo-', '') --version
    if ($LASTEXITCODE) { throw "Не запускается $($tool.Name)." }
}
