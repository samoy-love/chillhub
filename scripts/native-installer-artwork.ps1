[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$bitmap = [Drawing.Bitmap]::new(164, 314, [Drawing.Imaging.PixelFormat]::Format24bppRgb)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$ink = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#f6f4f7'))
$dim = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#a6a3af'))
$accent = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#c9bbfa'))
$title = [Drawing.Font]::new('Segoe UI', 19, [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
$caption = [Drawing.Font]::new('Segoe UI', 13, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
$icon = [Drawing.Icon]::new((Join-Path $PSScriptRoot 'app.ico'), 48, 48)
try {
    $graphics.Clear([Drawing.ColorTranslator]::FromHtml('#1c1c21'))
    $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.DrawIcon($icon, [Drawing.Rectangle]::new(24, 36, 48, 48))
    $graphics.DrawString('Chill Hub', $title, $ink, [Drawing.PointF]::new(22, 108))
    $graphics.DrawString("Игры.`nВместе.", $caption, $dim, [Drawing.PointF]::new(24, 146))
    $graphics.FillRectangle($accent, 24, 266, 36, 3)
    $bitmap.Save((Join-Path $OutputDirectory 'installer-sidebar.bmp'), [Drawing.Imaging.ImageFormat]::Bmp)
    $bitmap.Save((Join-Path $OutputDirectory 'installer-sidebar.png'), [Drawing.Imaging.ImageFormat]::Png)
} finally {
    $icon.Dispose(); $caption.Dispose(); $title.Dispose(); $accent.Dispose(); $dim.Dispose(); $ink.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
