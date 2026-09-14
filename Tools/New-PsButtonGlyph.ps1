param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\BetterJoyForCemu\Assets\InputPrompts\BetterJoy\PlayStation\playstation_button_ps.png')
)

# Generates BetterJoy's own PS button glyph to match the Kenney Input Prompts PlayStation set: a
# 48 px white disc on a 64 px transparent canvas with plain bold "PS" letters cut out. Deliberately
# uses a system font and no Sony logo styling.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$size = 64
$disc = 48
$capHeight = 18
$OutputPath = [IO.Path]::GetFullPath($OutputPath)

$bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$glyph = [Drawing.Drawing2D.GraphicsPath]::new([Drawing.Drawing2D.FillMode]::Alternate)
$letters = [Drawing.Drawing2D.GraphicsPath]::new()
$family = [Drawing.FontFamily]::new('Segoe UI')
$matrix = [Drawing.Drawing2D.Matrix]::new()
try {
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.Color]::Transparent)

    $offset = [single](($size - $disc) / 2)
    $glyph.AddEllipse($offset, $offset, [single]$disc, [single]$disc)

    # Letters are scaled to the Kenney L3/R3 cap height and centered; the alternate fill mode turns
    # them into holes in the disc.
    $letters.AddString('PS', $family, [int][Drawing.FontStyle]::Bold, [single]100,
        [Drawing.PointF]::new(0, 0), [Drawing.StringFormat]::GenericTypographic)
    $bounds = $letters.GetBounds()
    $scale = [single]($capHeight / $bounds.Height)
    $matrix.Translate([single]($size / 2), [single]($size / 2))
    $matrix.Scale($scale, $scale)
    $matrix.Translate([single](-($bounds.X + $bounds.Width / 2)),
        [single](-($bounds.Y + $bounds.Height / 2)))
    $letters.Transform($matrix)
    $glyph.AddPath($letters, $false)

    $graphics.FillPath([Drawing.Brushes]::White, $glyph)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
    $bitmap.Save($OutputPath, [Drawing.Imaging.ImageFormat]::Png)
    Write-Host "Generated $OutputPath"
} finally {
    $matrix.Dispose()
    $family.Dispose()
    $letters.Dispose()
    $glyph.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}
