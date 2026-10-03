# Fills kit\ with the Steward's kit (https://github.com/Jcollier0120/Steward), at the version and with the
# parts kit.json pins: {"kit": "1.0.0", "parts": ["spec"]}. Each part lands in kit\<part>\ (the spec part in
# kit\spec\), with kit\VERSION and kit\PARTS beside them. kit\ is git-ignored: never edit it here.
#
#   powershell -NoProfile -File tools\kit.ps1                the pinned kit; nothing to do when kit\ already holds it
#   powershell -NoProfile -File tools\kit.ps1 -From <dir>    a kit tree on this PC (a Steward checkout's kit\),
#                                                            copied every time; or set STEWARD_KIT to one
#
# Without -From, the pinned version comes from the first of: a sibling checkout ..\Steward\kit at that version;
# %USERPROFILE%\.steward\kits\<version>, the cache every agent on this PC shares; the Steward's kit release
# kit-v<version> over HTTPS (no sign-in), or through gh if that fails, checked against its SHA256SUMS.txt and kept
# in that cache. A fill from a kit tree is for development: it leaves kit\FROM, the build warns when its version
# isn't the pinned one, and -ForRelease (release.ps1, store.ps1) refuses it.
#
# Heiward's counterpart of the Steward's tools/kit.ts, which every Node agent has: the same sources, in the same
# order. Windows PowerShell 5.1 or later, no dependencies. STEWARD_KITS moves the cache and STEWARD_RELEASES the
# downloads (for tests), as for tools/kit.ts.
param(
	# A kit tree to copy, instead of the pinned kit.
	[string]$From = $env:STEWARD_KIT,
	# The repository whose kit.json is read and whose kit\ is filled.
	[string]$Root = (Split-Path $PSScriptRoot -Parent),
	# For a release build: the pinned kit only, never a kit tree.
	[switch]$ForRelease
)
$ErrorActionPreference = 'Stop'
# Windows PowerShell's progress bar slows Invoke-WebRequest down a lot.
$ProgressPreference = 'SilentlyContinue'

$repo = if ($env:STEWARD_REPO) { $env:STEWARD_REPO } else { 'Jcollier0120/Steward' }
$releases = if ($env:STEWARD_RELEASES) { $env:STEWARD_RELEASES.TrimEnd('/') } else { "https://github.com/$repo/releases/download" }
$userHome = if ($env:USERPROFILE) { $env:USERPROFILE } else { $HOME }
$kits = if ($env:STEWARD_KITS) { $env:STEWARD_KITS } else { Join-Path (Join-Path $userHome '.steward') 'kits' }
# .NET's file calls below resolve a relative path against the process's folder, not PowerShell's.
$kits = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($kits)

# A file's text, trimmed, or $null when there's no such file.
function Read-Text([string]$file) {
	if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return $null }
	return [IO.File]::ReadAllText($file).Trim()
}

# UTF-8 without a BOM, as tools/kit.ts writes them.
function Write-Text([string]$file, [string]$text) { [IO.File]::WriteAllText($file, $text) }

# Copies a kit tree's parts into kit\, then PARTS and VERSION (last, so a fill cut short is never taken for done).
function Copy-Kit([string]$tree, [string]$how, [bool]$dev) {
	$version = Read-Text (Join-Path $tree 'VERSION')
	if (-not $version) { throw "$tree isn't a kit tree: it has no VERSION" }
	foreach ($part in $parts) {
		if (-not (Test-Path -LiteralPath (Join-Path $tree $part) -PathType Container)) { throw "$tree has no $part part (it is the kit $version)" }
	}
	if (Test-Path -LiteralPath $into) { Remove-Item -LiteralPath $into -Recurse -Force }
	New-Item -ItemType Directory -Path $into | Out-Null
	foreach ($part in $parts) { Copy-Item -LiteralPath (Join-Path $tree $part) -Destination (Join-Path $into $part) -Recurse }
	if ($dev) { Write-Text (Join-Path $into 'FROM') "$tree`n" }
	Write-Text (Join-Path $into 'PARTS') "$($parts -join ' ')`n"
	Write-Text (Join-Path $into 'VERSION') "$version`n"
	Write-Host "kit\: the Steward's kit $version ($($parts -join ', ')), from $how"
	if ($pin.kit -and $version -ne $pin.kit) { Write-Warning "kit.json pins $($pin.kit): this is for development, and a release refuses it" }
}

# The kit release, downloaded and checked, in the cache; its folder.
function Get-KitRelease([string]$version) {
	$cache = Join-Path $kits $version
	if ((Read-Text (Join-Path $cache 'VERSION')) -eq $version) { return $cache }
	$zipName = "kit-$version.zip"
	$tmp = Join-Path ([IO.Path]::GetTempPath()) "steward-kit-$([guid]::NewGuid().ToString('N'))"
	New-Item -ItemType Directory -Path $tmp | Out-Null
	try {
		$https = $null
		try {
			# Windows PowerShell may not offer TLS 1.2 by itself, and GitHub takes nothing older.
			[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
			foreach ($name in @($zipName, 'SHA256SUMS.txt')) {
				Invoke-WebRequest -UseBasicParsing -TimeoutSec 60 -Uri "$releases/kit-v$version/$name" -OutFile (Join-Path $tmp $name)
			}
		}
		catch { $https = $_.Exception.Message }
		if ($https) {
			$gh = Get-Command gh -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1 | ForEach-Object { $_.Path }
			if (-not $gh -and (Test-Path -LiteralPath 'C:\tools\gh\bin\gh.exe')) { $gh = 'C:\tools\gh\bin\gh.exe' }
			$why = 'no gh to try'
			if ($gh) {
				# gh's complaints come on stderr, which Windows PowerShell would turn into a stopping error.
				$ErrorActionPreference = 'Continue'
				$said = & $gh release download "kit-v$version" --repo $repo --pattern $zipName --pattern SHA256SUMS.txt --dir $tmp --clobber 2>&1 | ForEach-Object { "$_" }
				$code = $LASTEXITCODE
				$ErrorActionPreference = 'Stop'
				$why = if ($code -eq 0) { $null } else { (@($said) -join ' ').Trim() }
				if ($code -ne 0 -and -not $why) { $why = "exit $code" }
			}
			if ($why) {
				throw ("couldn't get the Steward's kit ${version}: the release kit-v$version of $repo didn't download (HTTPS: $https; gh: $why).`n" +
					"  Publish it from the Steward (npm run kit-release -- --publish), or fill from a kit tree: tools\kit.ps1 -From <Steward checkout>\kit")
			}
		}
		$zip = Join-Path $tmp $zipName
		$listed = $null
		foreach ($line in [IO.File]::ReadAllLines((Join-Path $tmp 'SHA256SUMS.txt'))) {
			if ($line.Trim() -match '^([0-9a-fA-F]{64})\s+\*?(.+)$' -and $Matches[2] -eq $zipName) { $listed = $Matches[1]; break }
		}
		# -ne ignores case: Get-FileHash says uppercase, SHA256SUMS.txt lowercase.
		if (-not $listed -or $listed -ne (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash) { throw "$zipName doesn't match its SHA256SUMS.txt" }
		$out = Join-Path $tmp 'kit'
		Add-Type -AssemblyName System.IO.Compression.FileSystem
		[IO.Compression.ZipFile]::ExtractToDirectory($zip, $out)
		if ((Read-Text (Join-Path $out 'VERSION')) -ne $version) { throw "$zipName holds no kit $version" }
		if (Test-Path -LiteralPath $cache) { Remove-Item -LiteralPath $cache -Recurse -Force }
		New-Item -ItemType Directory -Force -Path $kits | Out-Null
		Copy-Item -LiteralPath $out -Destination $cache -Recurse
		return $cache
	}
	finally {
		Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
	}
}

try {
	if (-not (Test-Path -LiteralPath $Root -PathType Container)) { throw "-Root ${Root}: no such folder" }
	$Root = (Resolve-Path -LiteralPath $Root).ProviderPath
	$into = Join-Path $Root 'kit'
	$pinFile = Join-Path $Root 'kit.json'
	if (-not (Test-Path -LiteralPath $pinFile -PathType Leaf)) { throw "there's no $pinFile, which pins the kit: {`"kit`": `"1.0.0`", `"parts`": [`"spec`"]}" }
	try { $pin = (Read-Text $pinFile) | ConvertFrom-Json }
	catch { throw "$pinFile isn't JSON: $($_.Exception.Message)" }
	$parts = @($pin.parts | Where-Object { $_ } | ForEach-Object { [string]$_ })
	if (-not $parts.Count) { throw "$pinFile takes no kit parts: it needs `"parts`", such as [`"spec`"]" }
	foreach ($part in $parts) {
		if ($part -notmatch '^[a-z][a-z0-9-]*$' -or $part -eq 'test') { throw "${pinFile}: `"$part`" is no kit part" }
	}

	if ($From -and $ForRelease) {
		throw "a release takes the kit kit.json pins, not the kit tree $From (from -From or STEWARD_KIT): unset STEWARD_KIT and build again"
	}
	if ($From) {
		if (-not (Test-Path -LiteralPath $From -PathType Container)) { throw "the kit tree $From (from -From or STEWARD_KIT) isn't a folder" }
		$tree = (Resolve-Path -LiteralPath $From).ProviderPath
		Copy-Kit $tree $tree $true
	}
	else {
		$version = [string]$pin.kit
		if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "$pinFile pins no kit: it needs {`"kit`": `"<x.y.z>`"}" }
		$sibling = Join-Path (Join-Path (Split-Path $Root -Parent) 'Steward') 'kit'
		$filled = (Read-Text (Join-Path $into 'VERSION')) -eq $version -and (Read-Text (Join-Path $into 'PARTS')) -eq ($parts -join ' ') -and
			-not (Test-Path -LiteralPath (Join-Path $into 'FROM'))
		if ($filled) {
			# Already filled at the pinned version.
		}
		elseif ((Read-Text (Join-Path $sibling 'VERSION')) -eq $version) { Copy-Kit $sibling $sibling $false }
		else { Copy-Kit (Get-KitRelease $version) "the kit release kit-v$version" $false }
	}
}
catch {
	[Console]::Error.WriteLine("tools\kit.ps1: $($_.Exception.Message)")
	exit 1
}
exit 0
