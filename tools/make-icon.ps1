# Рисует значок программы (src/Mixer.App/Mixer.ico) из логотипа в шапке окна:
# две встречные кривые на квадрате акцентного цвета. Запускать в Windows PowerShell 5.1:
#   powershell -NoProfile -File tools\make-icon.ps1
param([string]$Out = (Join-Path $PSScriptRoot '..\src\Mixer.App\Mixer.ico'))

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$accent = [Windows.Media.Color]::FromRgb(0x4F, 0xA3, 0xFF)
$ink = [Windows.Media.Color]::FromRgb(0x0E, 0x11, 0x16)
# тот же путь, что IconLogo в Themes/Theme.xaml (сетка 24×24)
$logo = [Windows.Media.Geometry]::Parse('M4 7c6 0 10 10 16 10M4 17c6 0 10-10 16-10')

function Render([int]$size) {
    $dv = New-Object Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $inset = [Math]::Max(0.5, $size / 32.0)
    $box = New-Object Windows.Rect($inset, $inset, ($size - 2 * $inset), ($size - 2 * $inset))
    $r = $size * 0.22
    $dc.DrawRoundedRectangle((New-Object Windows.Media.SolidColorBrush($accent)), $null, $box, $r, $r)

    # на мелких размерах линии толще, чтобы значок не рассыпался
    $scale = $size * 0.64 / 16.0          # путь занимает 16 единиц сетки (от 4 до 20)
    $stroke = if ($size -le 16) { 2.0 } elseif ($size -le 32) { $size * 0.11 } else { $size * 0.095 }
    $pen = New-Object Windows.Media.Pen((New-Object Windows.Media.SolidColorBrush($ink)), $stroke)
    $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'; $pen.LineJoin = 'Round'
    $g = $logo.Clone()
    $tg = New-Object Windows.Media.TransformGroup
    $tg.Children.Add((New-Object Windows.Media.TranslateTransform(-12, -12)))
    $tg.Children.Add((New-Object Windows.Media.ScaleTransform($scale, $scale)))
    $tg.Children.Add((New-Object Windows.Media.TranslateTransform(($size / 2.0), ($size / 2.0))))
    $g.Transform = $tg
    $dc.DrawGeometry($null, $pen, $g)
    $dc.Close()

    $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object IO.MemoryStream
    $enc.Save($ms)
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($s in $sizes) { , (Render $s) }

# ICO с PNG внутри (Windows Vista и новее)
$fs = [IO.File]::Create([IO.Path]::GetFullPath($Out))
$w = New-Object IO.BinaryWriter($fs)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)) # 256 записывается как 0
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
Write-Host "значок: $([IO.Path]::GetFullPath($Out)) ($($sizes -join ', ') px)"
