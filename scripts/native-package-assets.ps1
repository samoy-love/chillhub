[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Directory).Path
$catalog = Get-Content -LiteralPath (Join-Path $root 'Data/games.json') -Raw | ConvertFrom-Json -AsHashtable
$paths = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
function Read-References($Value) {
    if ($Value -is [string]) {
        if ($Value.StartsWith('Assets/', [StringComparison]::Ordinal)) {
            if ($Value -match '[\\:]' -or @($Value.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count) { throw "Небезопасный путь медиа: $Value" }
            [void]$paths.Add($Value)
        }
    } elseif ($Value -is [Collections.IDictionary]) {
        foreach ($item in $Value.Values) { Read-References $item }
    } elseif ($Value -is [Collections.IEnumerable]) {
        foreach ($item in $Value) { Read-References $item }
    }
}
Read-References $catalog
foreach ($relative in $paths) {
    $path = Join-Path $root $relative
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "В каталоге игры указано отсутствующее медиа: $relative" }
    $part = Get-Item -LiteralPath $path -Force
    while ($part.FullName -ne $root) {
        if ($part.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Ссылка вместо медиа: $relative" }
        $part = $part.Parent ?? $part.Directory
        if (!$part) { throw "Путь вне каталога: $relative" }
    }
    $relative
}
