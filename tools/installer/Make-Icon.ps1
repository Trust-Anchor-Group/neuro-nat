<#
.SYNOPSIS
  Draws Neuro.AA's icon file (src/Neuro.AA.Desktop/Assets/Neuro.AA.ico) from the same design as
  AppIcon.cs: an accent tile with a white "N". Run it again only when the design changes.
#>
param(
	[string]$Out = (Join-Path $PSScriptRoot '..\..\src\Neuro.AA.Desktop\Assets\Neuro.AA.ico')
)

Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$tile = [System.Drawing.Color]::FromArgb(255, 0x33, 0x58, 0xD4)

function New-IconPng([int]$size) {
	$bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
	$g = [System.Drawing.Graphics]::FromImage($bmp)
	$g.SmoothingMode = 'AntiAlias'
	$g.TextRenderingHint = 'AntiAliasGridFit'
	$g.PixelOffsetMode = 'HighQuality'
	$g.Clear([System.Drawing.Color]::Transparent)

	# Same proportions as AppIcon.cs (64 px: 2 px inset, 14 px corner radius, 40 px bold "N").
	$k = $size / 64.0
	$inset = [Math]::Max(0.0, 2 * $k)
	$w = $size - 2 * $inset
	$d = [Math]::Max(2.0, 28 * $k)
	$path = New-Object System.Drawing.Drawing2D.GraphicsPath
	$path.AddArc($inset, $inset, $d, $d, 180, 90)
	$path.AddArc($inset + $w - $d, $inset, $d, $d, 270, 90)
	$path.AddArc($inset + $w - $d, $inset + $w - $d, $d, $d, 0, 90)
	$path.AddArc($inset, $inset + $w - $d, $d, $d, 90, 90)
	$path.CloseFigure()
	$brush = New-Object System.Drawing.SolidBrush $tile
	$g.FillPath($brush, $path)

	$font = New-Object System.Drawing.Font 'Segoe UI', ([single](40 * $k)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
	$fmt = New-Object System.Drawing.StringFormat
	$fmt.Alignment = 'Center'
	$fmt.LineAlignment = 'Center'
	$rect = New-Object System.Drawing.RectangleF 0, ([single](1 * $k)), $size, $size
	$g.DrawString('N', $font, [System.Drawing.Brushes]::White, $rect, $fmt)

	$g.Dispose()
	$ms = New-Object System.IO.MemoryStream
	$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
	$bmp.Dispose()
	return , $ms.ToArray()
}

$images = foreach ($s in $sizes) { , (New-IconPng $s) }

# ICO container with PNG-compressed entries (Windows Vista and later read these at every size).
$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ico
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
	$s = $sizes[$i]
	$dim = if ($s -ge 256) { 0 } else { $s }
	$bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
	$bw.Write([uint16]1); $bw.Write([uint16]32)
	$bw.Write([uint32]$images[$i].Length); $bw.Write([uint32]$offset)
	$offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Flush()

$full = [System.IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force (Split-Path $full) | Out-Null
[System.IO.File]::WriteAllBytes($full, $ico.ToArray())
Write-Host "Wrote $full ($($sizes -join ', ') px)"
