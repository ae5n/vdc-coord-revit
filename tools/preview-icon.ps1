Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$width  = 64
$height = 64
$outPath = "$PSScriptRoot\icon-preview.png"

function Pt($cx, $cy, $r, $angle) {
    [System.Windows.Point]::new(
        $cx + $r * [Math]::Cos($angle),
        $cy + $r * [Math]::Sin($angle))
}

function New-Icon([int]$w, [int]$h, [scriptblock]$draw) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $dc = $visual.RenderOpen()

    $rect   = [System.Windows.Rect]::new(0, 0, $w, $h)
    $radius = [Math]::Min($w, $h) * 0.25

    $bg = [System.Windows.Media.LinearGradientBrush]::new(
        [System.Windows.Media.Color]::FromRgb(0x3B, 0x1F, 0x7A),
        [System.Windows.Media.Color]::FromRgb(0x7C, 0x4D, 0xE8),
        [System.Windows.Point]::new(0,0), [System.Windows.Point]::new(1,1))
    $bg.Freeze()
    $dc.DrawRoundedRectangle($bg, $null, $rect, $radius, $radius)

    $sheen = [System.Windows.Media.LinearGradientBrush]::new(
        [System.Windows.Media.Color]::FromArgb(90,255,255,255),
        [System.Windows.Media.Color]::FromArgb(30,255,255,255),
        [System.Windows.Point]::new(0,0), [System.Windows.Point]::new(0,1))
    $sheen.Freeze()
    $dc.DrawRoundedRectangle($sheen, $null, [System.Windows.Rect]::new(1,1,$w-2,$h-2), $radius*0.85, $radius*0.85)

    & $draw $dc $w $h
    $dc.Close()

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($w, $h, 96, 96,
        [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $bitmap.Freeze()
    $bitmap
}

$drawGear = {
    param($dc, $w, $h)

    $min        = [Math]::Min($w, $h)
    $cx         = $w * 0.50
    $cy         = $h * 0.50
    $outerR     = $min * 0.37   # tooth tips
    $rootR      = $min * 0.26   # tooth valleys
    $holeR      = $min * 0.13   # center hole
    $N          = 8
    $step       = 2.0 * [Math]::PI / $N   # full period per tooth
    $toothHalf  = $step * 0.25            # tooth half-width = 50% of period
    $chamfer    = $toothHalf * 0.28       # small angle bevel on tooth edges

    $gear = [System.Windows.Media.StreamGeometry]::new()
    $gear.FillRule = [System.Windows.Media.FillRule]::EvenOdd
    $ctx = $gear.Open()

    # Start at the rising edge of tooth 0
    $startAngle = -[Math]::PI / 2.0 - $toothHalf
    $ctx.BeginFigure((Pt $cx $cy $rootR $startAngle), $true, $true)

    for ($i = 0; $i -lt $N; $i++) {
        $b = $i * $step - [Math]::PI / 2.0   # tooth centre angle

        # rising chamfer  (rootR → outerR)
        $ctx.LineTo((Pt $cx $cy $outerR ($b - $toothHalf + $chamfer)), $true, $false)
        # flat tooth top  (outerR)
        $ctx.LineTo((Pt $cx $cy $outerR ($b + $toothHalf - $chamfer)), $true, $false)
        # falling chamfer (outerR → rootR)
        $ctx.LineTo((Pt $cx $cy $rootR  ($b + $toothHalf)),            $true, $false)
        # valley arc to next tooth (rootR, clockwise)
        $ctx.ArcTo(
            (Pt $cx $cy $rootR ($b + $step - $toothHalf)),
            [System.Windows.Size]::new($rootR, $rootR),
            0, $false,
            [System.Windows.Media.SweepDirection]::Clockwise,
            $true, $false)
    }

    # Centre hole — EvenOdd punches it out
    $ctx.BeginFigure([System.Windows.Point]::new($cx + $holeR, $cy), $true, $true)
    $ctx.ArcTo([System.Windows.Point]::new($cx - $holeR, $cy),
        [System.Windows.Size]::new($holeR, $holeR), 0, $true,
        [System.Windows.Media.SweepDirection]::Clockwise, $true, $false)
    $ctx.ArcTo([System.Windows.Point]::new($cx + $holeR, $cy),
        [System.Windows.Size]::new($holeR, $holeR), 0, $true,
        [System.Windows.Media.SweepDirection]::Clockwise, $true, $false)

    $ctx.Close()
    $gear.Freeze()

    $gearBrush = [System.Windows.Media.SolidColorBrush]::new(
        [System.Windows.Media.Color]::FromArgb(230, 255, 255, 255))
    $gearBrush.Freeze()
    $dc.DrawGeometry($gearBrush, $null, $gear)
}

$bitmap = New-Icon $width $height $drawGear

$encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$stream = [System.IO.FileStream]::new($outPath, [System.IO.FileMode]::Create)
$encoder.Save($stream)
$stream.Close()

Write-Host "Saved: $outPath"
Start-Process $outPath
