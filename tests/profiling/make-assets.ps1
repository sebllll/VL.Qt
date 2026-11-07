# Generates the photo-like JPEGs for Profile04_Images (issue #557) into tests/profiling/assets/gen/ (not in git).
# 6 files, 1920x1080: gradients + soft blobs + noise, so JPEG compression is not trivial (~0.5-1 MB each).
# Deterministic (fixed seed per file); existing files are skipped.
# Usage: tests/profiling/make-assets.ps1 [-Force]
[CmdletBinding()]
param([switch]$Force)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
public static class VlNoise
{
    // Adds uniform noise (+-amp) to the B, G, R bytes of a 32bpp buffer; alpha untouched.
    public static void Add(byte[] px, int seed, int amp)
    {
        var rnd = new System.Random(seed);
        var n = new byte[px.Length / 4];
        rnd.NextBytes(n);
        for (int i = 0, j = 0; i < px.Length; i += 4, j++)
        {
            int d = (n[j] * (2 * amp + 1) >> 8) - amp;
            for (int c = 0; c < 3; c++)
            {
                int v = px[i + c] + d + ((rnd.Next(5)) - 2);
                px[i + c] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
            }
        }
    }
}
'@

$out = Join-Path $PSScriptRoot 'assets\gen'
New-Item -ItemType Directory -Force $out | Out-Null
$w = 1920; $h = 1080
$jpeg = [Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg'
$enc = New-Object Drawing.Imaging.EncoderParameters 1
$enc.Param[0] = New-Object Drawing.Imaging.EncoderParameter ([Drawing.Imaging.Encoder]::Quality), 92L

for ($i = 1; $i -le 6; $i++) {
    $file = Join-Path $out ('photo{0:D2}.jpg' -f $i)
    if ((Test-Path $file) -and -not $Force) { continue }
    $rnd = New-Object Random (1000 + $i)
    $bmp = New-Object Drawing.Bitmap $w, $h, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $c1 = [Drawing.Color]::FromArgb($rnd.Next(40, 255), $rnd.Next(40, 255), $rnd.Next(40, 255))
    $c2 = [Drawing.Color]::FromArgb($rnd.Next(0, 215), $rnd.Next(0, 215), $rnd.Next(0, 215))
    $lg = New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.Rectangle]::new(0, 0, $w, $h)), $c1, $c2, ([float]($rnd.Next(20, 70)))
    $g.FillRectangle($lg, 0, 0, $w, $h)
    for ($k = 0; $k -lt 14; $k++) {
        $r = $rnd.Next(120, 520); $x = $rnd.Next(-100, $w); $y = $rnd.Next(-100, $h)
        $path = New-Object Drawing.Drawing2D.GraphicsPath
        $path.AddEllipse($x, $y, $r * 1.4, $r)
        $pg = New-Object Drawing.Drawing2D.PathGradientBrush $path
        $pg.CenterColor = [Drawing.Color]::FromArgb(150, $rnd.Next(0, 256), $rnd.Next(0, 256), $rnd.Next(0, 256))
        $pg.SurroundColors = @([Drawing.Color]::FromArgb(0, 0, 0, 0))
        $g.FillPath($pg, $path)
        $pg.Dispose(); $path.Dispose()
    }
    $g.Dispose(); $lg.Dispose()
    $rect = [Drawing.Rectangle]::new(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, 'ReadWrite', [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $px = New-Object byte[] ($data.Stride * $h)
    [Runtime.InteropServices.Marshal]::Copy($data.Scan0, $px, 0, $px.Length)
    [VlNoise]::Add($px, 5000 + $i, 20)
    [Runtime.InteropServices.Marshal]::Copy($px, 0, $data.Scan0, $px.Length)
    $bmp.UnlockBits($data)
    $bmp.Save($file, $jpeg, $enc)
    $bmp.Dispose()
    Write-Host ('{0} ({1:N0} KB)' -f $file, ((Get-Item $file).Length / 1KB))
}
