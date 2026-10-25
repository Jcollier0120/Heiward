# Writes THIRD-PARTY-NOTICES.txt: the work of others that Heiward (hei.exe) includes or downloads, with the license texts
# of what hei.exe carries, kept with every copy as their makers ask (an MIT license's "this permission notice shall be
# included"). hei.exe carries it beside the GNU AGPL's full text (LICENSE): About on its page shows both, and its install
# writes both beside hei.exe, as LICENSE.txt and THIRD-PARTY-NOTICES.txt (HEI.Agent.csproj, LicenseTexts.cs). A release
# uploads both beside the exes (release.ps1).
#
# It is made from:
# - the NuGet packages hei.exe carries: HEI.Agent's restore for win-x64 (obj\project.assets.json), those with files that
#   ship (runtime or native), each with its license and notice files from the NuGet cache, or the license its nuspec names;
# - the .NET runtimes a release is published with, self-contained: their runtime packs' license and third-party notices,
#   at the version that restore takes;
# - the code Heiward includes from others, and what it downloads from where its makers publish it when a feature needs it:
#   $builtOn and $downloads below, to keep in step with HEI.Core's pins (FfmpegDownloader, AiComponents, NpuComponents,
#   GpuComponents).
#
# After a package is added or updated, a pin raised, or a new .NET SDK, make it again: HEI.Agent.Tests' NoticesTests fail
# while something hei.exe carries or downloads has no notice.
#
#   powershell -ExecutionPolicy Bypass -File tools\notices.ps1
#
# Needs the .NET 10 SDK on PATH, and the internet or the NuGet cache for the restore. Windows PowerShell 5.1 or later.
param(
	# The repository: its HEI.Agent is restored, and its THIRD-PARTY-NOTICES.txt written.
	[string]$Root = (Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference = 'Stop'

$rule = '=' * 100
$mitText = @'
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
'@

# A package whose nuspec names neither a license expression nor a license file: its license, and why.
$licenseOf = @{
	# Its nuspec has only a licenseUrl, https://go.microsoft.com/fwlink/?linkid=869050: Mono's LICENSE, under which its
	# class libraries, Mono.Posix among them, are MIT.
	'Mono.Posix.NETStandard' = 'MIT'
}

# The code in Heiward's source that others wrote.
$builtOn = @(
	@{
		Heading = 'Video Duplicate Finder'
		Lines = @(
			'https://github.com/0x90d/videoduplicatefinder'
			'Heiward is built on it. Copyright (C) 0x90d and contributors.'
			'License: GNU Affero General Public License, version 3 or later, as Heiward is (LICENSE.txt).'
		)
	}
	@{
		Heading = 'AcoustID.NET 1.3.3'
		Lines = @(
			'https://github.com/wo80/AcoustID.NET'
			'Heiward''s audio fingerprints (HEI.Core\Chromaprint in its source) are derived from it. Copyright (C) wo80.'
			'License: GNU Lesser General Public License, version 2.1 (https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html).'
			'The changed source is part of Heiward''s: https://github.com/Jcollier0120/Heiward'
		)
	}
)

# What Heiward downloads from where its makers publish it, when a feature needs it: none of it is in hei.exe, and each
# comes with its own license. In step with HEI.Core's pins, which NoticesTests checks.
$downloads = @(
	@{
		Heading = 'FFmpeg 8.1.3, lean LGPL build n8.1.3-6'
		Lines = @(
			'https://github.com/Jcollier0120/Manor-releases/releases/tag/ffmpeg-v8.1.3-6'
			'ffmpeg-8.1.3-lean-lgpl-shared-win-x64.zip and ffmpeg-8.1.3-lean-lgpl-shared-win-arm64.zip: FFmpeg, which reads videos'
			'and pictures, built from https://github.com/FFmpeg/FFmpeg/tree/n8.1.3 with dav1d 1.5.4 and zlib 1.3.2 (how it is built:'
			'https://github.com/Jcollier0120/ffmpeg-winarm64-lean), each .exe and .dll signed by Heiward''s publisher.'
			'License: GNU Lesser General Public License, version 2.1 or later (FFmpeg); BSD 2-Clause License (dav1d); zlib License'
			'(zlib). Each archive carries their texts, in licenses\.'
		)
	}
	@{
		Heading = 'FFmpeg 8.1.3, essentials build for Windows on Arm'
		Lines = @(
			'https://github.com/tordona/ffmpeg-win-arm64/releases/tag/8.1.3'
			'ffmpeg-8.1.3-essentials-shared-win-arm64.7z: only on Arm PCs, and only should the build above fail to download.'
			'License: GNU General Public License, version 3 (it includes GPL libraries, x264 and x265 among them). The archive'
			'carries its license.'
		)
	}
	@{
		Heading = 'ONNX Runtime 1.27.0'
		Lines = @(
			'https://github.com/microsoft/onnxruntime/releases/tag/v1.27.0'
			'onnxruntime-win-x64-1.27.0.zip and onnxruntime-win-arm64-1.27.0.zip: runs the AI model that compares pictures.'
			'License: MIT, Copyright (c) Microsoft Corporation. Each archive carries its LICENSE and ThirdPartyNotices.txt.'
		)
	}
	@{
		Heading = 'DINOv2 small, quantized (dinov2-small-int8.onnx)'
		Lines = @(
			'https://huggingface.co/Xenova/dinov2-small (onnx/model_quantized.onnx), mirrored at'
			'https://github.com/0x90d/videoduplicatefinder/releases/tag/ai-models-v1'
			'The AI model that compares pictures on the processor: Xenova''s ONNX export of Meta''s DINOv2 (facebook/dinov2-small).'
			'License: Apache License 2.0 (https://www.apache.org/licenses/LICENSE-2.0). DINOv2: Copyright (c) Meta Platforms, Inc.'
		)
	}
	@{
		Heading = 'DINOv2 small (dinov2-small-fp32.onnx)'
		Lines = @(
			'https://huggingface.co/Xenova/dinov2-small (onnx/model.onnx)'
			'The same model at full precision, for the NPU and the graphics card.'
			'License: Apache License 2.0 (https://www.apache.org/licenses/LICENSE-2.0). DINOv2: Copyright (c) Meta Platforms, Inc.'
		)
	}
	@{
		Heading = 'Qualcomm.ML.OnnxRuntime.QNN 2.6.0'
		Lines = @(
			'https://www.nuget.org/packages/Qualcomm.ML.OnnxRuntime.QNN/2.6.0'
			'On a Snapdragon PC: the files that run the model on its NPU (ONNX Runtime''s QNN execution provider and Qualcomm''s'
			'HTP libraries).'
			'License: MIT, Copyright (c) Qualcomm Technologies, Inc. and/or its subsidiaries, and Copyright (c) Microsoft Corporation.'
		)
	}
	@{
		Heading = 'Intel.ML.OnnxRuntime.EP.OpenVINO 1.7.0'
		Lines = @(
			'https://www.nuget.org/packages/Intel.ML.OnnxRuntime.EP.OpenVINO/1.7.0'
			'On an Intel Core Ultra PC: OpenVINO and its execution provider, which run the model on its NPU.'
			'License: MIT, as its package gives it (Intel Corporation).'
		)
	}
	@{
		Heading = 'Microsoft.Windows.AI.MachineLearning 2.4.89'
		Lines = @(
			'https://www.nuget.org/packages/Microsoft.Windows.AI.MachineLearning/2.4.89'
			'On an AMD Ryzen AI PC: Windows ML''s catalog, through which Windows installs the NPU''s execution provider from its'
			'maker (AMD''s Vitis AI), under that maker''s terms.'
			'License: Microsoft Software License Terms, Microsoft Windows ML Runtime'
			'(https://www.nuget.org/packages/Microsoft.Windows.AI.MachineLearning/2.4.89/license).'
		)
	}
	@{
		Heading = 'Microsoft.ML.OnnxRuntime.DirectML 1.24.4'
		Lines = @(
			'https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.DirectML/1.24.4'
			'When the AI runs on the graphics card: ONNX Runtime with DirectML.'
			'License: MIT, Copyright (c) Microsoft Corporation.'
		)
	}
	@{
		Heading = 'Microsoft.AI.DirectML 1.15.4'
		Lines = @(
			'https://www.nuget.org/packages/Microsoft.AI.DirectML/1.15.4'
			'When the AI runs on the graphics card: DirectML.'
			'License: Microsoft Software License Terms, Microsoft DirectX Machine Learning'
			'(https://www.nuget.org/packages/Microsoft.AI.DirectML/1.15.4/license).'
		)
	}
)

if (-not ((dotnet --list-sdks 2>$null) -match '^10\.')) {
	throw 'The .NET 10 SDK is not on PATH ("dotnet --list-sdks" lists no 10.x). Install it, or put its folder first on PATH.'
}
$project = Join-Path $Root 'HEI.Agent\HEI.Agent.csproj'
Write-Host 'Restoring HEI.Agent for win-x64, as a release publishes it...'
dotnet restore $project -r win-x64 -p:SelfContained=true --source https://api.nuget.org/v3/index.json -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet restore of $project failed." }
$assets = Get-Content (Join-Path $Root 'HEI.Agent\obj\project.assets.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$packages = $assets.project.restore.packagesPath
$target = ($assets.targets.PSObject.Properties | Where-Object { $_.Name -like '*/win-x64' } | Select-Object -First 1).Value
$framework = ($assets.project.frameworks.PSObject.Properties | Select-Object -First 1).Value
if (-not $target -or -not $framework) { throw "HEI.Agent's project.assets.json has no win-x64 target." }

function Read-Text([string]$Path) { ([IO.File]::ReadAllText($Path) -replace "`r`n", "`n").Trim() }

# A NuGet package's folder in the cache.
function Get-PackageDir([string]$Name, [string]$Version) { Join-Path $packages "$($Name.ToLowerInvariant())\$($Version.ToLowerInvariant())" }

# A folder's license and notice files (LICENSE, LICENSE.txt, THIRD-PARTY-NOTICES.TXT...), licenses first.
function Get-LicenseFiles([string]$Dir) {
	Get-ChildItem $Dir -File | Where-Object { $_.Name -match '^(licen[cs]e|notice|third[-_]?party[-_]?notices|copying)(\.(txt|md))?$' } |
		Sort-Object @{ Expression = { [int]($_.Name -match 'notice') } }, Name
}

# Texts already written, by their text: a package that carries the same notices as one above points to it.
$written = @{}
function Format-Texts([string]$Heading, $Files) {
	foreach ($f in $Files) {
		$text = Read-Text $f.FullName
		if ($written.ContainsKey($text)) { "$($f.Name): the same as $($written[$text]), above."; '' }
		else {
			$written[$text] = "$Heading's $($f.Name)"
			$text
			''
		}
	}
}

$out = New-Object System.Collections.Generic.List[string]
function Add-Part([string]$Heading, [string[]]$Lines) {
	$out.Add($rule)
	$out.Add($Heading)
	foreach ($l in $Lines) { $out.Add($l) }
	$out.Add('')
}

$out.AddRange([string[]]@(
	'Heiward: third-party notices'
	''
	'Heiward is free software: you can share and change it under the GNU Affero General Public License, version 3. Its full'
	'text is in LICENSE.txt, beside this file, and Heiward''s source code is at https://github.com/Jcollier0120/Heiward.'
	''
	'Heiward includes and uses the work of others. This file lists it: the code Heiward is built on; what hei.exe carries,'
	'each with its license''s text, as its makers ask; and what Heiward downloads from where its makers publish it, only'
	'when a feature needs it. Those downloads aren''t part of hei.exe, and each comes with its own license.'
	''
	''
	'THE CODE HEIWARD IS BUILT ON'
	''
))
foreach ($p in $builtOn) { Add-Part $p.Heading $p.Lines }

$out.AddRange([string[]]@('', 'IN HEI.EXE', ''))
# The .NET runtimes it is published with, self-contained: each framework it references, by its runtime pack.
$runtimes = @(
	@{ Framework = 'Microsoft.NETCore.App'; Heading = '.NET runtime'; Url = 'https://github.com/dotnet/runtime'; Also = ' and its app host, which starts hei.exe' }
	@{ Framework = 'Microsoft.AspNetCore.App'; Heading = 'ASP.NET Core runtime'; Url = 'https://github.com/dotnet/aspnetcore'; Also = ', which serves its page' }
)
foreach ($r in $runtimes) {
	if (-not $framework.frameworkReferences.($r.Framework)) { throw "HEI.Agent no longer references $($r.Framework): take it out of `$runtimes in tools\notices.ps1." }
	$pack = $framework.downloadDependencies | Where-Object { $_.name -eq "$($r.Framework).Runtime.win-x64" } | Select-Object -First 1
	if (-not $pack) { throw "HEI.Agent's restore names no $($r.Framework).Runtime.win-x64 pack." }
	$version = $pack.version.Trim('[', ']').Split(',')[0].Trim()
	$dir = Get-PackageDir $pack.name $version
	$files = @(Get-LicenseFiles $dir)
	if (-not $files.Count) { throw "$dir has no license file." }
	$minor = ($version -split '\.')[0..1] -join '.'
	$heading = "$($r.Heading) $minor ($($r.Framework))"
	Add-Part $heading (@(
		$r.Url
		"hei.exe is published self-contained: it carries the $($r.Heading) it runs on ($version when this file was made)$($r.Also)."
		'License: MIT'
		''
	) + @(Format-Texts $heading $files))
}

# The NuGet packages with files that ship.
$shipped = foreach ($lib in $target.PSObject.Properties) {
	$v = $lib.Value
	if ($v.type -ne 'package') { continue }
	$files = @(foreach ($kind in 'runtime', 'native') { if ($v.$kind) { $v.$kind.PSObject.Properties.Name } }) | Where-Object { $_ -and $_ -notlike '*/_._' }
	if ($files) { $lib.Name }
}
foreach ($id in $shipped | Sort-Object) {
	$name, $version = $id -split '/', 2
	$dir = Get-PackageDir $name $version
	$nuspec = [xml](Get-Content (Get-ChildItem $dir -Filter '*.nuspec' | Select-Object -First 1).FullName -Raw -Encoding UTF8)
	$meta = $nuspec.package.metadata
	$license = $meta.license
	$files = @(Get-LicenseFiles $dir)
	$expression = if ($license -and $license.type -eq 'expression') { $license.'#text' } elseif ($licenseOf[$name]) { $licenseOf[$name] } else { $null }
	$said = if ($expression) { $expression } elseif ($license -and $license.type -eq 'file') { "as in its $($license.'#text'), below" } else { $null }
	if (-not $said -and -not $files.Count) { throw "Not written: the NuGet package $name $version names no license, and has no license file in $dir. Find its license, and add it to `$licenseOf in tools\notices.ps1." }
	if (-not $said) { $said = 'as in its license files, below' }
	$texts = @(Format-Texts "$name $version" $files)
	# A license by its expression alone, with no file to copy: MIT's text, with the package's copyright.
	if (-not ($files | Where-Object { $_.Name -match '^licen[cs]e' })) {
		if ($expression -ne 'MIT') { throw "Not written: $name $version is under $expression, and has no license file in $dir to copy. Add its license text to tools\notices.ps1." }
		# A nuspec's "(c)" sign, [char]0xA9: this file stays ASCII, as Windows PowerShell reads a script without a BOM as ANSI.
		$copyright = if ($meta.copyright) { $meta.copyright -replace "^$([char]0xA9)\s*", 'Copyright (c) ' } else { "Copyright (c) $($meta.authors)" }
		$texts = @('MIT License', '', $copyright, '', $mitText, '') + $texts
	}
	$where = if ($meta.projectUrl) { $meta.projectUrl } else { "https://www.nuget.org/packages/$name/$version" }
	Add-Part "$name $version" (@($where, 'A NuGet package hei.exe carries.', "License: $said", '') + $texts)
}

$out.AddRange([string[]]@('', 'WHAT HEIWARD DOWNLOADS WHEN A FEATURE NEEDS IT', ''))
foreach ($p in $downloads) { Add-Part $p.Heading $p.Lines }

# Trailing spaces off, one blank line at the end; CRLF, as git checks text out on Windows.
$text = ((($out -join "`n") -split "`n" | ForEach-Object { $_.TrimEnd() }) -join "`r`n").TrimEnd() + "`r`n"
$file = Join-Path $Root 'THIRD-PARTY-NOTICES.txt'
[IO.File]::WriteAllText($file, $text, (New-Object System.Text.UTF8Encoding $false))
Write-Host ('Wrote {0} ({1:N0} KB): {2} packages, {3} runtimes, {4} downloads.' -f $file, ($text.Length / 1KB), @($shipped).Count, $runtimes.Count, $downloads.Count)
