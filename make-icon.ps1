# Draws the sticky-note icon and writes app.ico (PNG frames, 16-256 px).
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot 'app.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = @()

function P([double]$x, [double]$y) { New-Object System.Drawing.PointF([single]$x, [single]$y) }
function Brush($a, $r, $g, $b) { New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb($a, $r, $g, $b)) }

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $k = $s / 32.0
    $m = 2 * $k
    $fold = 9 * $k
    $l = $m; $t = $m; $r = $s - $m; $b = $s - $m

    $note = New-Object System.Drawing.Drawing2D.GraphicsPath
    $note.AddPolygon([System.Drawing.PointF[]]@((P $l $t), (P $r $t), (P $r ($b - $fold)), (P ($r - $fold) $b), (P $l $b)))
    $g.FillPath((Brush 255 255 240 160), $note)

    $g.SetClip($note)
    $g.FillRectangle((Brush 255 247 201 72), [single]$l, [single]$t, [single]($r - $l), [single](7 * $k))
    $g.ResetClip()

    if ($s -ge 24) {
        $linePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(150, 150, 120, 40), [single][Math]::Max(1, 1.4 * $k))
        foreach ($row in 13, 18, 23) {
            $y = $t + $row * $k
            $x2 = if ($row -eq 23) { $r - $fold - 2 * $k } else { $r - 4 * $k }
            $g.DrawLine($linePen, [single]($l + 4 * $k), [single]$y, [single]$x2, [single]$y)
        }
    }

    $g.FillPolygon((Brush 255 222 184 74), [System.Drawing.PointF[]]@((P $r ($b - $fold)), (P ($r - $fold) ($b - $fold)), (P ($r - $fold) $b)))

    $outline = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 176, 136, 32), [single][Math]::Max(1, $k))
    $g.DrawPath($outline, $note)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $frames += , @{ Size = $s; Bytes = $ms.ToArray() }
}

$file = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($file)
$w.Write([UInt16]0)
$w.Write([UInt16]1)
$w.Write([UInt16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$dim)
    $w.Write([byte]$dim)
    $w.Write([byte]0)
    $w.Write([byte]0)
    $w.Write([UInt16]1)
    $w.Write([UInt16]32)
    $w.Write([UInt32]$f.Bytes.Length)
    $w.Write([UInt32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $w.Write($f.Bytes) }
$w.Flush()
[System.IO.File]::WriteAllBytes($out, $file.ToArray())
Write-Host "Wrote $out"
