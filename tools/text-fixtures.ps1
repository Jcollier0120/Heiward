# Pictures for the benchmarks' copy probe (HEI.Benchmarks --probe-copies <folder> --text): pictures with words, each with
# true copies of it and a twin whose words differ, laid out as the probe reads them: NAME__orig.* beside NAME__KIND.*.
#
#   powershell -File tools\text-fixtures.ps1 -Out <folder> -Ffmpeg <ffmpeg.exe> [-Photos <folder of camera JPEGs>]
#
# Kinds: half, quarter, w1024 (resized with FFmpeg's default bicubic), jpeg (recompressed, quality 85; a PNG original
# saved as JPEG), png (a JPEG original saved losslessly), heic (saved as HEIC through Windows' HEIF encoder, which needs
# the HEVC extension), and the twins: diff (the same picture with other words), diff-half (that twin resized). Chats one
# message apart, receipts with another total, two pages of one form, desktop windows with one line changed, camera
# photos with another caption, and camera photos with no words at all (their copies must stay copies too).
# Windows PowerShell 5.1: System.Drawing draws the text with ClearType, as Windows draws screens.
param(
	[Parameter(Mandatory = $true)][string]$Out,
	[Parameter(Mandatory = $true)][string]$Ffmpeg,
	[string]$Photos = ""
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName PresentationCore
New-Item -ItemType Directory -Force $Out | Out-Null
$rng = New-Object System.Random 20261109

function New-Canvas([int]$w, [int]$h, [System.Drawing.Color]$back) {
	$bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
	$g = [System.Drawing.Graphics]::FromImage($bmp)
	$g.Clear($back)
	$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
	$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
	return @($bmp, $g)
}

function Text($g, [string]$s, [string]$font, [float]$px, [System.Drawing.Color]$color, [float]$x, [float]$y, [string]$style = "Regular") {
	$f = New-Object System.Drawing.Font($font, $px, [System.Drawing.FontStyle]::$style, [System.Drawing.GraphicsUnit]::Pixel)
	$b = New-Object System.Drawing.SolidBrush $color
	$g.DrawString($s, $f, $b, $x, $y)
	$b.Dispose(); $f.Dispose()
}

function Save($pair, [string]$path) {
	$pair[1].Dispose()
	if ($path.EndsWith(".jpg")) {
		$codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq "image/jpeg" }
		$p = New-Object System.Drawing.Imaging.EncoderParameters 1
		$p.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 92L
		$pair[0].Save($path, $codec, $p)
	}
	else { $pair[0].Save($path, [System.Drawing.Imaging.ImageFormat]::Png) }
	$pair[0].Dispose()
}

$words = "the a meeting tomorrow lunch project update photos invoice sent please call back thanks great plan budget review train late arrive office weekend dinner booked tickets ready printer report draft final notes kids school pickup" -split " "
function Sentence([int]$n) { (1..$n | ForEach-Object { $words[$rng.Next($words.Length)] }) -join " " }

# ---------------------------------------------------------------- chats: one message apart
function Chat([string]$path, [string[]]$messages) {
	$c = New-Canvas 1080 2340 ([System.Drawing.Color]::FromArgb(236, 229, 221))
	$g = $c[1]
	$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0, 128, 105))), 0, 0, 1080, 200)
	Text $g "Sam Taylor" "Segoe UI" 48 ([System.Drawing.Color]::White) 150 70 "Bold"
	$y = 260
	for ($i = 0; $i -lt $messages.Length; $i++) {
		$mine = $i % 2 -eq 1
		$x = if ($mine) { 300 } else { 40 }
		$back = if ($mine) { [System.Drawing.Color]::FromArgb(220, 248, 198) } else { [System.Drawing.Color]::White }
		$g.FillRectangle((New-Object System.Drawing.SolidBrush $back), $x, $y, 740, 170)
		Text $g $messages[$i] "Segoe UI" 40 ([System.Drawing.Color]::Black) ($x + 30) ($y + 25)
		$time = "{0}:{1:00}" -f (9 + $i), ((7 * $i) % 60)
		Text $g $time "Segoe UI" 30 ([System.Drawing.Color]::Gray) ($x + 620) ($y + 115)
		$y += 220
	}
	Save $c $path
}
for ($k = 0; $k -lt 8; $k++) {
	$m = 1..8 | ForEach-Object { Sentence 4 }
	Chat (Join-Path $Out "chat$k`__orig.png") $m
	$m[7] = Sentence 4
	Chat (Join-Path $Out "chat$k`__diff.png") $m
}

# ---------------------------------------------------------------- receipts: another total
function Receipt([string]$path, [string[]]$items, [decimal[]]$prices, [string]$store) {
	$c = New-Canvas 800 1800 ([System.Drawing.Color]::White)
	$g = $c[1]
	Text $g $store "Consolas" 40 ([System.Drawing.Color]::Black) 220 60 "Bold"
	Text $g "12 Market Street  Tel 555-0142" "Consolas" 26 ([System.Drawing.Color]::Black) 130 130
	$y = 240
	for ($i = 0; $i -lt $items.Length; $i++) {
		Text $g $items[$i] "Consolas" 28 ([System.Drawing.Color]::Black) 60 $y
		Text $g ("{0,8:N2}" -f $prices[$i]) "Consolas" 28 ([System.Drawing.Color]::Black) 560 $y
		$y += 60
	}
	$total = ($prices | Measure-Object -Sum).Sum
	Text $g ("TOTAL    {0,10:N2}" -f $total) "Consolas" 34 ([System.Drawing.Color]::Black) 300 ($y + 40) "Bold"
	Text $g "Thank you for shopping with us" "Consolas" 26 ([System.Drawing.Color]::Black) 160 ($y + 160)
	Save $c $path
}
$goods = "MILK 2L", "BREAD WHOLEMEAL", "EGGS 12", "COFFEE BEANS", "APPLES 1KG", "PASTA", "TOMATOES", "CHEESE", "RICE 2KG", "OLIVE OIL", "BANANAS", "YOGURT"
for ($k = 0; $k -lt 8; $k++) {
	$items = 1..10 | ForEach-Object { $goods[$rng.Next($goods.Length)] }
	$prices = 1..10 | ForEach-Object { [decimal]($rng.Next(99, 2999)) / 100 }
	$store = ("FRESH MART", "CORNER SHOP", "GREEN GROCER")[$k % 3]
	Receipt (Join-Path $Out "receipt$k`__orig.png") $items $prices $store
	$prices[9] = $prices[9] + 0.60
	Receipt (Join-Path $Out "receipt$k`__diff.png") $items $prices $store
}

# ---------------------------------------------------------------- forms: two pages of one form
function FormPage([string]$path, [int]$page, [string[]]$questions) {
	$c = New-Canvas 1700 2200 ([System.Drawing.Color]::White)
	$g = $c[1]
	Text $g "Application for a residential parking permit" "Arial" 44 ([System.Drawing.Color]::Black) 120 100 "Bold"
	Text $g "Form RP-7   Please write in capital letters" "Arial" 28 ([System.Drawing.Color]::Black) 120 170
	$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::Black), 2
	$y = 300
	foreach ($q in $questions) {
		Text $g $q "Arial" 30 ([System.Drawing.Color]::Black) 120 $y
		$g.DrawRectangle($pen, 120, $y + 50, 1460, 80)
		$y += 190
	}
	Text $g ("Page {0} of 2" -f $page) "Arial" 26 ([System.Drawing.Color]::Black) 760 2100
	Save $c $path
}
$asks = "Full name", "Address", "Postcode", "Vehicle registration", "Make and model", "Colour", "Telephone", "Email address", "Date of birth", "Driving licence number", "Signature", "Date", "Proof of address supplied", "Permit zone", "Start date", "Payment reference"
for ($k = 0; $k -lt 4; $k++) {
	$pick = $asks | Sort-Object { $rng.Next() }
	FormPage (Join-Path $Out "form$k`__orig.png") 1 ($pick[0..8])
	FormPage (Join-Path $Out "form$k`__diff.png") 2 ($pick[7..15])
}

# ---------------------------------------------------------------- desktop windows: one line changed
function Window([string]$path, [string[]]$lines) {
	$c = New-Canvas 1920 1080 ([System.Drawing.Color]::FromArgb(243, 243, 243))
	$g = $c[1]
	$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)), 260, 120, 1400, 840)
	Text $g "Settings" "Segoe UI" 15 ([System.Drawing.Color]::Black) 280 130
	Text $g "System > Display" "Segoe UI" 28 ([System.Drawing.Color]::Black) 300 180 "Bold"
	$y = 260
	foreach ($l in $lines) { Text $g $l "Segoe UI" 15 ([System.Drawing.Color]::Black) 300 $y; $y += 34 }
	Save $c $path
}
$settings = "Brightness", "Night light", "Scale 125%", "Display resolution 1920 x 1080", "Display orientation Landscape", "Multiple displays", "Advanced display", "Graphics", "HDR", "Refresh rate 60 Hz", "Color profile sRGB", "Text size 100%"
for ($k = 0; $k -lt 6; $k++) {
	$l = $settings | Sort-Object { $rng.Next() } | Select-Object -First 12
	Window (Join-Path $Out "window$k`__orig.png") $l
	$l[5] = "Refresh rate 144 Hz"
	Window (Join-Path $Out "window$k`__diff.png") $l
}

# ---------------------------------------------------------------- camera photos: with a caption, and with no words
$camera = if ($Photos -and (Test-Path $Photos)) { Get-ChildItem $Photos -Filter *.jpg | Sort-Object Name | Select-Object -First 16 } else { @() }
function Captioned([string]$src, [string]$path, [string]$caption) {
	$img = [System.Drawing.Image]::FromFile($src)
	$c = New-Canvas $img.Width $img.Height ([System.Drawing.Color]::Black)
	$g = $c[1]
	$g.DrawImage($img, 0, 0, $img.Width, $img.Height)
	$px = [Math]::Max(24, [int]($img.Height / 18))
	$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(160, 0, 0, 0))), 0, $img.Height - 2 * $px, $img.Width, 2 * $px)
	Text $g $caption "Segoe UI" $px ([System.Drawing.Color]::White) ($px / 2) ($img.Height - 1.7 * $px)
	$img.Dispose()
	Save $c $path
}
$places = "Lake Tahoe", "Grand Canyon", "Lisbon", "Kyoto", "Edinburgh", "Banff", "Cape Town", "Prague"
for ($k = 0; $k -lt [Math]::Min(8, $camera.Count); $k++) {
	Captioned $camera[$k].FullName (Join-Path $Out "caption$k`__orig.jpg") ("{0}, summer 2019" -f $places[$k])
	Captioned $camera[$k].FullName (Join-Path $Out "caption$k`__diff.jpg") ("{0}, summer 2021" -f $places[$k])
}
for ($k = 8; $k -lt [Math]::Min(16, $camera.Count); $k++) {
	Copy-Item $camera[$k].FullName (Join-Path $Out ("photo{0}__orig.jpg" -f ($k - 8)))
}

# ---------------------------------------------------------------- the copies
# HEIC through Windows' own encoder (the HEIF and HEVC Video Extensions): WPF's encoder fails writing to its stream.
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapEncoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
$asTaskAction = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' } | Select-Object -First 1
function Await($op, [Type]$t) { $task = $asTask.MakeGenericMethod($t).Invoke($null, @($op)); $task.Wait(-1) | Out-Null; $task.Result }
function AwaitAction($op) { $task = $asTaskAction.Invoke($null, @($op)); $task.Wait(-1) | Out-Null }
function Heic([string]$src, [string]$dst) {
	$in = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($src)) ([Windows.Storage.StorageFile])
	$rs = Await ($in.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
	$dec = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($rs)) ([Windows.Graphics.Imaging.BitmapDecoder])
	$bmp = Await ($dec.GetSoftwareBitmapAsync([Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8, [Windows.Graphics.Imaging.BitmapAlphaMode]::Premultiplied,
		(New-Object Windows.Graphics.Imaging.BitmapTransform), [Windows.Graphics.Imaging.ExifOrientationMode]::RespectExifOrientation,
		[Windows.Graphics.Imaging.ColorManagementMode]::DoNotColorManage)) ([Windows.Graphics.Imaging.SoftwareBitmap])
	$folder = Await ([Windows.Storage.StorageFolder]::GetFolderFromPathAsync((Split-Path $dst))) ([Windows.Storage.StorageFolder])
	$out = Await ($folder.CreateFileAsync((Split-Path $dst -Leaf), [Windows.Storage.CreationCollisionOption]::ReplaceExisting)) ([Windows.Storage.StorageFile])
	$ws = Await ($out.OpenAsync([Windows.Storage.FileAccessMode]::ReadWrite)) ([Windows.Storage.Streams.IRandomAccessStream])
	try {
		$enc = Await ([Windows.Graphics.Imaging.BitmapEncoder]::CreateAsync([Windows.Graphics.Imaging.BitmapEncoder]::HeifEncoderId, $ws)) ([Windows.Graphics.Imaging.BitmapEncoder])
		$enc.SetSoftwareBitmap($bmp)
		AwaitAction ($enc.FlushAsync())
	}
	finally { $ws.Dispose(); $rs.Dispose() }
}
function FF([string[]]$a) { & $Ffmpeg -hide_banner -loglevel error -y @a; if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed: $a" } }
foreach ($orig in Get-ChildItem $Out -Filter "*__orig.*") {
	$stem = $orig.Name.Substring(0, $orig.Name.IndexOf("__"))
	$src = $orig.FullName
	FF @("-i", $src, "-vf", "scale=trunc(iw/4)*2:-2", (Join-Path $Out "$stem`__half.png"))
	FF @("-i", $src, "-vf", "scale=trunc(iw/8)*2:-2", (Join-Path $Out "$stem`__quarter.png"))
	FF @("-i", $src, "-vf", "scale=1024:-2", (Join-Path $Out "$stem`__w1024.png"))
	if ($orig.Extension -eq ".png") { FF @("-i", $src, "-q:v", "3", (Join-Path $Out "$stem`__jpeg.jpg")) }
	else { FF @("-i", $src, "-q:v", "8", (Join-Path $Out "$stem`__jpeg.jpg")); FF @("-i", $src, (Join-Path $Out "$stem`__png.png")) }
	try { Heic $src (Join-Path $Out "$stem`__heic.heic") } catch { Write-Warning "no HEIC for $stem`: $($_.Exception.Message)" }
	$diff = Join-Path $Out ("$stem`__diff" + $orig.Extension)
	if (Test-Path $diff) { FF @("-i", $diff, "-vf", "scale=trunc(iw/4)*2:-2", (Join-Path $Out "$stem`__diff-half.png")) }
}
Write-Host ("{0} pictures in {1}" -f (Get-ChildItem $Out).Count, $Out)
