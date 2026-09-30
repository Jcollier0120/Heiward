# Renders Heiward's mark (wwwroot/favicon.svg: the shield and leaf) into the app icon: heiward.ico
# for the exe (and so its shortcuts and Apps & Features), wwwroot/heiward.png for notifications, and
# the Store package's logos in Store/Assets (store.ps1 packs them).
# Run it after changing favicon.svg; the outputs are checked in. Windows PowerShell or pwsh on Windows
# (it draws with WPF, which reads SVG's path syntax).
#   powershell -ExecutionPolicy Bypass -File HEI.Agent\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

$here = $PSScriptRoot
[xml]$svg = Get-Content -Raw (Join-Path $here 'wwwroot\favicon.svg')
$paths = @{}
foreach ($p in $svg.svg.path) { $paths[$p.class] = $p.d }
$box = $svg.svg.viewBox -split '\s+' | ForEach-Object { [double]$_ } # x y width height

# The Light theme's greens, as the favicon's <style> has them outside dark mode.
function Brush([string]$hex) { $b = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($hex)); $b.Freeze(); $b }
$mint = Brush '#dff6e8'
$green = Brush '#0f7b3f'

# $fill: the share of the square the mark takes, centred (a Start menu tile leaves a margin around it).
function Render([int]$size, [double]$fill = 1) {
  $visual = New-Object System.Windows.Media.DrawingVisual
  $dc = $visual.RenderOpen()
  $scale = $size * $fill / $box[2]
  $margin = $size * (1 - $fill) / 2
  $dc.PushTransform((New-Object System.Windows.Media.TranslateTransform $margin, $margin))
  $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform $scale, $scale))
  $dc.PushTransform((New-Object System.Windows.Media.TranslateTransform (-$box[0]), (-$box[1])))
  $edge = New-Object System.Windows.Media.Pen $green, 1.6
  $edge.LineJoin = 'Round'
  $dc.DrawGeometry($mint, $edge, [System.Windows.Media.Geometry]::Parse($paths['back']))
  $dc.DrawGeometry($green, $null, [System.Windows.Media.Geometry]::Parse($paths['front']))
  $vein = New-Object System.Windows.Media.Pen $mint, 1.2
  $vein.StartLineCap = 'Round'; $vein.EndLineCap = 'Round'
  $dc.DrawGeometry($null, $vein, [System.Windows.Media.Geometry]::Parse($paths['vein']))
  $dc.Close()
  $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
  $bitmap.Render($visual)
  $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
  $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
  $stream = New-Object System.IO.MemoryStream
  $encoder.Save($stream)
  , $stream.ToArray()
}

# An .ico of PNG images (Windows Vista and later read them at every size).
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = $sizes | ForEach-Object { , (Render $_) }
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i] % 256 # 0 means 256
  $w.Write([byte]$s); $w.Write([byte]$s); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
  $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $here 'heiward.ico'), $ico.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $here 'wwwroot\heiward.png'), (Render 256))
Write-Host "Wrote heiward.ico ($($sizes -join ', ') px) and wwwroot\heiward.png (256 px)."

# The Store package's logos, at 100% and 200% scale. Square44x44Logo is the one the Start menu's list,
# the taskbar and Settings show: its targetsize-N files are those exact sizes, and the _altform-unplated
# twins are what Windows 11 draws without a coloured plate behind the icon.
$assets = Join-Path $here 'Store\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null
$logos = [ordered]@{
  'StoreLogo.scale-100.png' = 50, 1; 'StoreLogo.scale-200.png' = 100, 1
  'Square150x150Logo.scale-100.png' = 150, 0.6; 'Square150x150Logo.scale-200.png' = 300, 0.6
  'Square44x44Logo.scale-100.png' = 44, 1; 'Square44x44Logo.scale-200.png' = 88, 1
}
foreach ($t in 16, 24, 32, 48, 256) {
  $logos["Square44x44Logo.targetsize-$t.png"] = $t, 1
  $logos["Square44x44Logo.targetsize-${t}_altform-unplated.png"] = $t, 1
}
foreach ($name in $logos.Keys) {
  [System.IO.File]::WriteAllBytes((Join-Path $assets $name), (Render $logos[$name][0] $logos[$name][1]))
}
Write-Host "Wrote $($logos.Count) Store logos in Store\Assets."
