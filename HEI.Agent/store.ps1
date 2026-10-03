# Builds Heiward's Microsoft Store package: Heiward-<version>.msixbundle (x64 and arm64) in artifacts\store,
# the file to upload in Partner Center. The Store signs it after certification, so no certificate is needed here.
# Each architecture's unpacked package stays next to it in layout-<arch>, for trying it out locally (docs\STORE.md).
# It fills the Steward's kit first (tools\kit.ps1), at the version kit.json pins.
# Needs the .NET 10 SDK on PATH. MakeAppx and MakePri come from the Windows SDK build tools NuGet package,
# and FFmpeg from the pinned build below, both downloaded once into artifacts\tools: no Windows SDK install needed.
#
#   powershell -ExecutionPolicy Bypass -File HEI.Agent\store.ps1
param(
	[string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\store'),
	# Build only these architectures (one gives a bundle with one package, for a quick local try).
	[ValidateSet('x64', 'arm64')]
	[string[]]$Arch = @('x64', 'arm64')
)
$ErrorActionPreference = 'Stop'
# Windows PowerShell's progress bar slows Invoke-WebRequest down a lot.
$ProgressPreference = 'SilentlyContinue'

# Microsoft.Windows.SDK.BuildTools: pinned, so every build packs with the same MakeAppx.
$buildToolsVersion = '10.0.28000.2705'

# FFmpeg ships inside the package, in bin\ where Heiward looks for it first. The Store signs it with the
# rest, so Smart App Control lets it load, and nothing is downloaded after install. Pinned by release and
# SHA-256 like FfmpegDownloader.WinArm64Builds (HEI.Core): only tested archives go in.
$ffmpegBuilds = @{
	arm64 = @{
		Url = 'https://github.com/Jcollier0120/ffmpeg-winarm64-lean/releases/download/n8.1.3-4/ffmpeg-8.1.3-lean-lgpl-shared-win-arm64.zip'
		Sha256 = '77db6db962015a72f0316620bcaa4d824903b1bcc96c59a6a8b32316cb09ded5'
	}
	# The same lean configuration built for x64 (the GitHub exe downloads BtbN's build there).
	x64 = @{
		Url = 'https://github.com/Jcollier0120/ffmpeg-winarm64-lean/releases/download/n8.1.3-4/ffmpeg-8.1.3-lean-lgpl-shared-win-x64.zip'
		Sha256 = 'eb505d5ec3769bf786b4946202801aba62a9fba268d135b4571ef4a15d164bcd'
	}
}

if (-not ((dotnet --list-sdks 2>$null) -match '^10\.')) {
	throw 'The .NET 10 SDK is not on PATH ("dotnet --list-sdks" lists no 10.x). Install it, or put its folder first on PATH.'
}

$root = Split-Path $PSScriptRoot -Parent
$tools = Join-Path $root 'artifacts\tools'
$storeDir = Join-Path $PSScriptRoot 'Store'
$assets = Join-Path $storeDir 'Assets'
if (-not (Get-ChildItem $assets -Filter '*.png' -ErrorAction SilentlyContinue)) {
	throw "No logos in $assets. make-icon.ps1 renders them from wwwroot\favicon.svg."
}
foreach ($a in $Arch) {
	if (-not $ffmpegBuilds[$a]) { throw "No FFmpeg build is pinned for $a yet (`$ffmpegBuilds in store.ps1)." }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem

# The Steward's kit, at exactly the version kit.json pins, as for a GitHub release (HEI.Agent\README.md, "The kit").
& (Join-Path $root 'tools\kit.ps1') -ForRelease
if ($LASTEXITCODE -ne 0) { throw "tools\kit.ps1 couldn't fill the Steward's kit (above)." }
$kit = (Get-Content (Join-Path $root 'kit\VERSION') -Raw).Trim()

$project = Join-Path $PSScriptRoot 'HEI.Agent.csproj'
$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.VersionPrefix | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No VersionPrefix in $project." }
# The Store keeps the fourth number for itself: a package it takes ends in .0.
$packageVersion = "$version.0"

$manifest = Get-Content (Join-Path $storeDir 'AppxManifest.xml') -Raw

function Get-BuildTool([string]$name) {
	$dir = Join-Path $tools "sdk-buildtools-$buildToolsVersion"
	if (-not (Test-Path $dir)) {
		Write-Host "Downloading the Windows SDK build tools $buildToolsVersion..."
		$nupkg = Join-Path ([IO.Path]::GetTempPath()) "sdk-buildtools-$([guid]::NewGuid().ToString('N')).zip"
		try {
			Invoke-WebRequest -UseBasicParsing -OutFile $nupkg "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$buildToolsVersion/microsoft.windows.sdk.buildtools.$buildToolsVersion.nupkg"
			# Unpacked next to the final folder, then renamed: an interrupted download leaves no half folder behind.
			$partial = "$dir.partial"
			Remove-Item $partial -Recurse -Force -ErrorAction SilentlyContinue
			[IO.Compression.ZipFile]::ExtractToDirectory($nupkg, $partial)
			Move-Item $partial $dir
		}
		finally {
			Remove-Item $nupkg -ErrorAction SilentlyContinue
		}
	}
	# The package has the tools for x86, x64 and arm64: take this PC's own, else the x64 ones.
	$hostArch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
	$tools = Get-ChildItem (Join-Path $dir 'bin') -Recurse -Filter "$name.exe"
	$tool = $tools | Where-Object { $_.Directory.Name -eq $hostArch } | Select-Object -First 1
	if (-not $tool) { $tool = $tools | Where-Object { $_.Directory.Name -eq 'x64' } | Select-Object -First 1 }
	if (-not $tool) { throw "No $name.exe in $dir." }
	$tool.FullName
}

# hei.exe is a console program, so from the Start menu it would open a console window. Heiward.exe is the
# same .NET app host with the Windows GUI subsystem, which is how the SDK writes it for a WinExe project:
# it runs hei.dll without a window.
function Copy-Windowed([string]$from, [string]$to) {
	$bytes = [IO.File]::ReadAllBytes($from)
	$pe = [BitConverter]::ToInt32($bytes, 0x3C)
	if ([BitConverter]::ToUInt32($bytes, $pe) -ne 0x4550) { throw "$from is not a Windows program." }
	# The optional header follows the 4-byte signature and the 20-byte file header. Subsystem sits
	# 68 bytes into it in both 32- and 64-bit programs.
	$subsystem = $pe + 24 + 68
	if ([BitConverter]::ToUInt16($bytes, $subsystem) -ne 3) { throw "$from is not a console program." }
	$bytes[$subsystem] = 2 # IMAGE_SUBSYSTEM_WINDOWS_GUI
	[IO.File]::WriteAllBytes($to, $bytes)
}

# FFmpeg's bin\ goes to the layout's bin\, its licenses and build notes to licenses\FFmpeg.
function Add-Ffmpeg([string]$a, [string]$layout) {
	$build = $ffmpegBuilds[$a]
	$zip = Join-Path $tools (Split-Path $build.Url -Leaf)
	if (-not (Test-Path $zip) -or (Get-FileHash $zip -Algorithm SHA256).Hash -ne $build.Sha256) {
		Write-Host "Downloading $(Split-Path $zip -Leaf)..."
		New-Item -ItemType Directory -Force $tools | Out-Null
		Invoke-WebRequest -UseBasicParsing -OutFile $zip $build.Url
		if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $build.Sha256) {
			Remove-Item $zip
			throw "$($build.Url) doesn't have the pinned SHA-256."
		}
	}
	$unpacked = Join-Path $work "ffmpeg-$a"
	[IO.Compression.ZipFile]::ExtractToDirectory($zip, $unpacked)
	# The archive holds one folder named after the build.
	$top = (Get-ChildItem $unpacked -Directory | Select-Object -First 1).FullName
	if (-not (Test-Path (Join-Path $top 'bin\ffmpeg.exe'))) { throw "$zip has no bin\ffmpeg.exe." }
	Copy-Item (Join-Path $top 'bin') (Join-Path $layout 'bin') -Recurse
	$licenses = Join-Path $layout 'licenses\FFmpeg'
	New-Item -ItemType Directory -Force $licenses | Out-Null
	Copy-Item (Join-Path $top 'licenses\*'), (Join-Path $top 'readme.txt') $licenses
}

function Invoke-Tool([string]$exe) {
	# Quiet unless it fails: MakePri and MakeAppx list every file they touch.
	$output = & $exe @args 2>&1
	if ($LASTEXITCODE -ne 0) {
		$output | Write-Host
		throw "$(Split-Path $exe -Leaf) failed (exit $LASTEXITCODE)."
	}
}

$makeAppx = Get-BuildTool 'makeappx'
$makePri = Get-BuildTool 'makepri'

$commit = git -C $PSScriptRoot rev-parse HEAD
if (git -C $PSScriptRoot status --porcelain) { Write-Warning 'The working tree has uncommitted changes, and the build includes them.' }
Write-Host "Heiward $version (package $packageVersion) from commit $($commit.Substring(0, 7)), with the Steward's kit $kit"

New-Item -ItemType Directory -Force $Out | Out-Null
$bundle = Join-Path $Out "Heiward-$version.msixbundle"
Remove-Item $bundle -ErrorAction SilentlyContinue
$work = Join-Path ([IO.Path]::GetTempPath()) "heiward-store-$([guid]::NewGuid().ToString('N'))"
$packages = Join-Path $work 'packages'
New-Item -ItemType Directory -Force $packages | Out-Null

try {
	foreach ($a in $Arch) {
		Write-Host "Building $a..."
		$layout = Join-Path $Out "layout-$a"
		Remove-Item $layout -Recurse -Force -ErrorAction SilentlyContinue
		# Not a single file, unlike the GitHub exe: that one unpacks its native libraries into %TEMP% at
		# startup, outside the package and its signature. Here every file is the package's own.
		dotnet publish $project -c Release -r "win-$a" --self-contained `
			--source https://api.nuget.org/v3/index.json -o $layout -nologo -v q
		if ($LASTEXITCODE -ne 0) { throw "The $a build failed." }
		Get-ChildItem $layout -Recurse -Filter '*.pdb' | Remove-Item
		Copy-Windowed (Join-Path $layout 'hei.exe') (Join-Path $layout 'Heiward.exe')
		Add-Ffmpeg $a $layout

		Copy-Item $assets (Join-Path $layout 'Assets') -Recurse
		# The desktop shortcut's icon: a shortcut the package declares takes an .ico, not an exe's icon.
		Copy-Item (Join-Path $PSScriptRoot 'heiward.ico') (Join-Path $layout 'Assets\heiward.ico')
		$appxManifest = Join-Path $layout 'AppxManifest.xml'
		[IO.File]::WriteAllText($appxManifest, $manifest.Replace('$Version$', $packageVersion).Replace('$Arch$', $a), (New-Object System.Text.UTF8Encoding $false))

		# resources.pri lets Windows pick the logo for each size and scale (Square44x44Logo.targetsize-24_altform-unplated.png
		# and so on). It indexes only the logos: pointed at the whole layout, it would list every DLL too.
		$pri = Join-Path $work "pri-$a"
		Copy-Item $assets (Join-Path $pri 'Assets') -Recurse
		Invoke-Tool $makePri createconfig /cf (Join-Path $work 'priconfig.xml') /dq en-US /o
		Invoke-Tool $makePri new /pr $pri /cf (Join-Path $work 'priconfig.xml') /mn $appxManifest /of (Join-Path $layout 'resources.pri') /o

		Invoke-Tool $makeAppx pack /d $layout /p (Join-Path $packages "Heiward-$version-$a.msix") /o
	}
	Write-Host 'Bundling...'
	Invoke-Tool $makeAppx bundle /d $packages /p $bundle /bv $packageVersion /o
}
finally {
	Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ('Built {0} ({1:N1} MB).' -f $bundle, ((Get-Item $bundle).Length / 1MB))
Write-Host 'Upload it in Partner Center; docs\STORE.md has the rest.'
