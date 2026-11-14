# Generate reproducible PNG/ICO assets from the checked-in vector, not user images.
param([string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'res'))
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Use Windows PowerShell 5.1 to generate WPF icons.' }
Add-Type -AssemblyName PresentationCore, WindowsBase
$iconSource = Join-Path (Split-Path -Parent $PSScriptRoot) 'res\brand.svg'
[xml]$iconSvg = Get-Content -LiteralPath $iconSource -Raw -Encoding UTF8
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
function Render-Brand([int]$size, [string]$color) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $context.PushTransform((New-Object System.Windows.Media.ScaleTransform ($size / 64.0), ($size / 64.0)))
    foreach ($shape in $iconSvg.DocumentElement.ChildNodes) {
        $brush = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($color))
        if ($shape.LocalName -eq 'rect') {
            $rect = New-Object System.Windows.Rect ([double]$shape.x), ([double]$shape.y), ([double]$shape.width), ([double]$shape.height)
            $context.DrawRoundedRectangle($brush, $null, $rect, [double]$shape.rx, [double]$shape.rx)
        } elseif ($shape.LocalName -eq 'path') {
            $pen = New-Object System.Windows.Media.Pen $brush, ([double]$shape.'stroke-width')
            $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'; $pen.LineJoin = 'Round'
            $context.DrawGeometry($null, $pen, [System.Windows.Media.Geometry]::Parse($shape.d))
        }
    }
    $context.Pop(); $context.Close()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream); $bytes = $stream.ToArray(); $stream.Dispose()
    return ,$bytes
}
$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) { $frames += ,(Render-Brand $size '#2E8158') }
[System.IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'brand.png'), $frames[-1])
foreach ($theme in @(@('light', '#243B30'), @('dark', '#F1F6F3'))) {
    [System.IO.File]::WriteAllBytes((Join-Path $OutputDirectory ('brand-' + $theme[0] + '.png')), (Render-Brand 32 $theme[1]))
}
$icoStream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $icoStream
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'brand.ico'), $icoStream.ToArray())
$writer.Dispose(); $icoStream.Dispose()
Write-Output ('OK: brand icons (' + ($sizes -join ', ') + ') in ' + $OutputDirectory)
