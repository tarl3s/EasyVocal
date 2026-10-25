# Draws src\EasyVocal\Assets\EasyVocal.ico: a green rounded square with a white microphone, at 16-256 px.
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File tools\Make-Icon.ps1
# -PngDir <folder>: writes the same drawing as EasyVocal-<size>.png at 16-1024 px there instead (the macOS .icns and the
# Linux icon of tools\Publish-Release.ps1); the .ico is left alone.
param([string]$PngDir)
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$dir = Join-Path $root 'src\EasyVocal\Assets'
New-Item -ItemType Directory -Force $dir | Out-Null
$out = Join-Path $dir 'EasyVocal.ico'
$sizes = 256, 64, 48, 32, 24, 16

function Rounded([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [single]([Math]::Min($r * 2, [Math]::Min($w, $h)))
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return ,$p
}

function Draw-Png([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    # green rounded tile (the app's accent, #4ADE80, a touch darker at the bottom)
    $m = [single]([Math]::Max(0, $s * 0.02)); $w = [single]($s - 2 * $m)
    $tile = Rounded $m $m $w $w ([single]($s * 0.22))
    $rect = New-Object System.Drawing.RectangleF $m, $m, $w, $w
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 74, 222, 128)), ([System.Drawing.Color]::FromArgb(255, 34, 170, 90)), 90
    $g.FillPath($grad, $tile)
    # white microphone: capsule, U-shaped holder, stem and base
    $white = [System.Drawing.Color]::White
    $brush = New-Object System.Drawing.SolidBrush $white
    $cx = $s / 2
    $cw = $s * 0.24; $ch = $s * 0.40; $ctop = $s * 0.15
    $capsule = Rounded ([single]($cx - $cw / 2)) ([single]$ctop) ([single]$cw) ([single]$ch) ([single]($cw / 2))
    $g.FillPath($brush, $capsule)
    $pen = New-Object System.Drawing.Pen $white, ([single][Math]::Max(1.2, $s * 0.065))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $hw = $s * 0.38; $htop = $ctop + $ch * 0.30; $hh = $ch * 0.95
    $g.DrawArc($pen, [single]($cx - $hw / 2), [single]$htop, [single]$hw, [single]$hh, 0, 180)
    $stemTop = $htop + $hh; $stemBottom = $s * 0.82
    $g.DrawLine($pen, [single]$cx, [single]$stemTop, [single]$cx, [single]$stemBottom)
    $bw = $s * 0.26
    $g.DrawLine($pen, [single]($cx - $bw / 2), [single]$stemBottom, [single]($cx + $bw / 2), [single]$stemBottom)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return ,$ms.ToArray()
}

if ($PngDir) {
    New-Item -ItemType Directory -Force $PngDir | Out-Null
    foreach ($s in 16, 32, 64, 128, 256, 512, 1024) {
        [System.IO.File]::WriteAllBytes((Join-Path $PngDir ("EasyVocal-{0:D4}.png" -f $s)), (Draw-Png $s))
    }
    "wrote the PNGs in $PngDir"
    return
}

# ICO = header + one directory entry per size + the PNGs
$pngs = $sizes | ForEach-Object { ,(Draw-Png $_) }
$fs = [System.IO.File]::Create($out); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset); $offset += $len
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p) }
$bw.Close()
"wrote $out"
