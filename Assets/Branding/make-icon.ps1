# Generates app.ico (16..256 px, PNG-compressed entries) from the central AEROAID logo, aeroaid.png.
# Run automatically by MissionPlanner.csproj before compiling, so replacing aeroaid.png also updates the
# Windows icon embedded in the executable (Explorer, shortcuts, pinned taskbar items).
# The wide wordmark is centred on a rounded dark tile (brand background) with its aspect ratio preserved: never
# stretched or cropped. Only the fully transparent margin around the artwork is ignored. The tile keeps the white
# "AERO" lettering visible on light taskbars.
param(
    [string]$Source = (Join-Path $PSScriptRoot "aeroaid.png"),
    [string]$Target = (Join-Path $PSScriptRoot "app.ico")
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$src = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Source))
try {
    # bounding box of the visible (alpha > 8) pixels
    $minX = $src.Width; $minY = $src.Height; $maxX = -1; $maxY = -1
    for ($y = 0; $y -lt $src.Height; $y += 2) {
        for ($x = 0; $x -lt $src.Width; $x += 2) {
            if ($src.GetPixel($x, $y).A -gt 8) {
                if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    if ($maxX -lt 0) { $minX = 0; $minY = 0; $maxX = $src.Width - 1; $maxY = $src.Height - 1 }
    $pad = [Math]::Max(2, [int]([Math]::Max($src.Width, $src.Height) / 100))
    $box = [System.Drawing.Rectangle]::FromLTRB([Math]::Max(0, $minX - $pad), [Math]::Max(0, $minY - $pad),
        [Math]::Min($src.Width, $maxX + 2 + $pad), [Math]::Min($src.Height, $maxY + 2 + $pad))

    $sizes = 16, 24, 32, 48, 64, 128, 256
    $images = @()
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        # rounded tile in the brand background colour (#111111)
        $r = [Math]::Max(2, [int]($s / 6)); $d = 2 * $r
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($s - $d - 1, 0, $d, $d, 270, 90)
        $path.AddArc($s - $d - 1, $s - $d - 1, $d, $d, 0, 90); $path.AddArc(0, $s - $d - 1, $d, $d, 90, 90)
        $path.CloseFigure()
        $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x11, 0x11, 0x11))
        $g.FillPath($brush, $path); $brush.Dispose(); $path.Dispose()
        $inner = $s * 0.9
        $scale = [Math]::Min($inner / $box.Width, $inner / $box.Height)
        $w = $box.Width * $scale; $h = $box.Height * $scale
        $dest = New-Object System.Drawing.RectangleF ((($s - $w) / 2), (($s - $h) / 2), $w, $h)
        $g.DrawImage($src, $dest, [System.Drawing.RectangleF]$box, [System.Drawing.GraphicsUnit]::Pixel)
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $images += , @($s, $ms.ToArray())
    }
}
finally {
    $src.Dispose()
}

# ICO container: ICONDIR + ICONDIRENTRY[n] + PNG payloads
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $s = $img[0]; $data = $img[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($img in $images) { $bw.Write([byte[]]$img[1]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($Target, $out.ToArray())
Write-Host "app.ico generated from $(Split-Path $Source -Leaf) ($($images.Count) sizes)"
