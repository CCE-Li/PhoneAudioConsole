# Generates Assets/app.ico from a source PNG for the tray icon and exe icon.
# Usage: powershell -File tools/make-icon.ps1 [-Source ..\..\icon.png] [-Output ..\Assets\app.ico]
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\..\icon.png'),
    [string]$Output = (Join-Path $PSScriptRoot '..\Assets\app.ico')
)

Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

$src = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Source))
try {
    # Content bounding box: keep colourful or dark pixels, ignore the white/soft-grey background.
    $rect = New-Object System.Drawing.Rectangle(0, 0, $src.Width, $src.Height)
    $data = $src.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $bytes = New-Object byte[] ($stride * $src.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $src.UnlockBits($data)

    $minX = $src.Width; $minY = $src.Height; $maxX = -1; $maxY = -1
    $step = 2
    $count = 0
    for ($y = 0; $y -lt $src.Height; $y += $step) {
        $row = $y * $stride
        for ($x = 0; $x -lt $src.Width; $x += $step) {
            $i = $row + $x * 4
            if ($bytes[$i + 3] -lt 128) { continue }
            $b = $bytes[$i]; $g = $bytes[$i + 1]; $r = $bytes[$i + 2]
            $mx = [Math]::Max($r, [Math]::Max($g, $b))
            $mn = [Math]::Min($r, [Math]::Min($g, $b))
            $sat = $mx - $mn
            $lum = ($r + $g + $b) / 3
            if ($sat -gt 60 -or $lum -lt 90) {
                $count++
                if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    Write-Host "content pixels (sampled): $count"

    if ($maxX -lt 0) { throw 'No icon artwork found in the source image.' }

    Write-Host "bbox x:$minX..$maxX  y:$minY..$maxY  (image $($src.Width)x$($src.Height))"

    $pad = [int]([Math]::Max($maxX - $minX, $maxY - $minY) * 0.05)
    $side = [Math]::Max($maxX - $minX, $maxY - $minY) + 2 * $pad
    if ($side -gt $src.Width) { $side = $src.Width }
    if ($side -gt $src.Height) { $side = $src.Height }
    $cx = ($minX + $maxX) / 2; $cy = ($minY + $maxY) / 2
    $x0 = [int]([Math]::Round($cx - $side / 2)); $y0 = [int]([Math]::Round($cy - $side / 2))
    $x0 = [Math]::Max(0, [Math]::Min($x0, $src.Width - $side))
    $y0 = [Math]::Max(0, [Math]::Min($y0, $src.Height - $side))

    $square = New-Object System.Drawing.Bitmap($side, $side, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($square)
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, $side, $side)), (New-Object System.Drawing.Rectangle($x0, $y0, $side, $side)), [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    Write-Host "cropped to ${side}x${side} (from $($src.Width)x$($src.Height))"

    $entries = @()
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($square, (New-Object System.Drawing.Rectangle(0, 0, $s, $s)))
        $g.Dispose()

        $r2 = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
        $d2 = $bmp.LockBits($r2, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $px = New-Object byte[] ($d2.Stride * $s)
        [System.Runtime.InteropServices.Marshal]::Copy($d2.Scan0, $px, 0, $px.Length)
        $stride2 = $d2.Stride
        $bmp.UnlockBits($d2)

        $andRow = [int]([Math]::Ceiling($s / 32.0) * 4)
        $ms = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter($ms)
        $bw.Write([int]40)
        $bw.Write([int]$s)
        $bw.Write([int]($s * 2))
        $bw.Write([uint16]1)
        $bw.Write([uint16]32)
        $bw.Write([int]0)
        $bw.Write([int]($s * $s * 4))
        $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
        for ($row = $s - 1; $row -ge 0; $row--) { $bw.Write($px, $row * $stride2, $s * 4) }
        $zero = New-Object byte[] $andRow
        for ($row = 0; $row -lt $s; $row++) { $bw.Write($zero, 0, $andRow) }
        $bw.Flush()
        $entries += , @{ Size = $s; Data = $ms.ToArray() }
        $bw.Dispose(); $ms.Dispose(); $bmp.Dispose()
    }

    $dir = Split-Path -Parent $Output
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    $out = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($out)
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$entries.Count)
    $offset = 6 + 16 * $entries.Count
    foreach ($e in $entries) {
        $dim = $e.Size
        $dimByte = $dim
        if ($dim -ge 256) { $dimByte = 0 }
        $bw.Write([byte]$dimByte)
        $bw.Write([byte]$dimByte)
        $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([int]$e.Data.Length)
        $bw.Write([int]$offset)
        $offset += $e.Data.Length
    }
    foreach ($e in $entries) { $bw.Write($e.Data, 0, $e.Data.Length) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($Output, $out.ToArray())
    $bw.Dispose(); $out.Dispose(); $square.Dispose()
    Write-Host "wrote $Output ($($entries.Count) sizes)"
}
finally { $src.Dispose() }
