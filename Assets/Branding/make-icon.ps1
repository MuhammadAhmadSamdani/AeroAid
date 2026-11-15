# Generates app.ico (16..256 px, PNG-compressed entries) from logo.png.
# Run automatically by MissionPlanner.csproj before compiling, so replacing logo.png also updates the
# Windows icon embedded in the executable (Explorer, shortcuts, pinned taskbar items).
param(
    [string]$Source = (Join-Path $PSScriptRoot "logo.png"),
    [string]$Target = (Join-Path $PSScriptRoot "app.ico")
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$logo = [System.Drawing.Image]::FromFile((Resolve-Path $Source))
try {
    $sizes = 16, 24, 32, 48, 64, 128, 256
    $images = @()
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $scale = [Math]::Min($s / $logo.Width, $s / $logo.Height)
        $w = $logo.Width * $scale; $h = $logo.Height * $scale
        $g.DrawImage($logo, ($s - $w) / 2, ($s - $h) / 2, $w, $h)
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $images += , @($s, $ms.ToArray())
    }
}
finally {
    $logo.Dispose()
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
