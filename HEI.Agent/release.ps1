# Builds Heiward's release files: one self-contained exe per architecture
# (Heiward-<version>-x64.exe, Heiward-<version>-arm64.exe) and SHA256SUMS.txt, in artifacts\heiward.
# With -Publish it then creates the GitHub release v<version> at the commit it built, and uploads every file.
# It fills the Steward's kit first (tools\kit.ps1), at the version kit.json pins.
# Needs the .NET 10 SDK on PATH, and for -Publish the GitHub CLI (gh), signed in.
#
#   powershell -ExecutionPolicy Bypass -File HEI.Agent\release.ps1              build only
#   powershell -ExecutionPolicy Bypass -File HEI.Agent\release.ps1 -Publish     build, then release and upload
param(
	[string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\heiward'),
	# Create the GitHub release and upload the files once they're built.
	[switch]$Publish,
	# Release notes: text, or the path of a file with them. Default: CHANGELOG.md's entry for the version (or its commits), then which file to download and the unsigned-build note.
	[string]$Notes,
	# The repository to release to (owner/name).
	[string]$Repo = 'Jcollier0120/Heiward'
)
$ErrorActionPreference = 'Stop'

if (-not ((dotnet --list-sdks 2>$null) -match '^10\.')) {
	throw 'The .NET 10 SDK is not on PATH ("dotnet --list-sdks" lists no 10.x). Install it, or put its folder first on PATH.'
}
if ($Publish) {
	if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'The GitHub CLI (gh) is not on PATH, and -Publish needs it.' }
	# The release is tagged at the commit built, so that commit must be exactly what's released.
	if (git -C $PSScriptRoot status --porcelain) { throw 'The working tree has uncommitted changes. Commit them (or build without -Publish) first.' }
}

# The Steward's kit, at exactly the version kit.json pins: never a kit tree from -From or STEWARD_KIT (HEI.Agent\README.md, "The kit").
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $root 'tools\kit.ps1') -ForRelease
if ($LASTEXITCODE -ne 0) { throw "tools\kit.ps1 couldn't fill the Steward's kit (above)." }
$kit = (Get-Content (Join-Path $root 'kit\VERSION') -Raw).Trim()

$project = Join-Path $PSScriptRoot 'HEI.Agent.csproj'
$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.VersionPrefix | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No VersionPrefix in $project." }
$tag = "v$version"

$commit = git -C $PSScriptRoot rev-parse HEAD
if (git -C $PSScriptRoot status --porcelain) { Write-Warning 'The working tree has uncommitted changes, and the build includes them.' }
Write-Host "Heiward $version from commit $($commit.Substring(0, 7)), with the Steward's kit $kit"

# What the release brings: CHANGELOG.md's entry for the version, under "## <version>" (the Steward's kit,
# spec\RELEASE-NOTES.md: What's new, What changed, and Before you update, always there). A version with no
# entry lists its commits since the release before instead, and says so; a release never says nothing.
function Get-ChangelogEntry([string]$Text, [string]$Version) {
	$lines = ($Text -replace "`r`n", "`n") -split "`n"
	$heading = '^## +\[?v?' + [regex]::Escape($Version) + '\]?(\s|$|[^\w.])'
	$at = -1
	for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match $heading) { $at = $i; break } }
	if ($at -lt 0) { return $null }
	$end = $lines.Count
	for ($i = $at + 1; $i -lt $lines.Count; $i++) { if ($lines[$i].StartsWith('## ')) { $end = $i; break } }
	if ($end -le $at + 1) { return $null }
	$body = ($lines[($at + 1)..($end - 1)] -join "`n").Trim()
	if ($body) { return $body }
	return $null
}
function Get-CommitsSince([string]$Version) {
	# git writes to stderr now and then, which Windows PowerShell would turn into a stopping error.
	$ErrorActionPreference = 'Continue'
	$since = @(git -C $PSScriptRoot tag --merged HEAD --list 'v[0-9]*' 2>$null) |
		Where-Object { $_ -match '^v\d+\.\d+\.\d+$' -and [version]$_.Substring(1) -lt [version]$Version } |
		Sort-Object { [version]$_.Substring(1) } -Descending | Select-Object -First 1
	$range = @(if ($since) { "$since..HEAD" } else { '-n'; '10' })
	$log = (git -C $PSScriptRoot log --first-parent '--format=%s%x1f%b%x1e' @range 2>$null) -join "`n"
	$lines = foreach ($c in ($log -split [char]0x1e)) {
		$c = $c.Trim()
		if (-not $c) { continue }
		$subject, $body = $c -split [char]0x1f, 2
		if ($subject -match '^Merge pull request #\d+ from ') { (("$body".Trim() -split "`n")[0]).Trim() }
		elseif ($subject -match '^(Merge (remote-tracking )?branch |Merge commit |wip|fixup!|squash!)') { }
		else { $subject.Trim() }
	}
	[pscustomobject]@{ Since = $since; Lines = @($lines | Where-Object { $_ } | Select-Object -Unique -First 30) }
}
$changelog = Join-Path $root 'CHANGELOG.md'
$entry = if (Test-Path $changelog) { Get-ChangelogEntry (Get-Content $changelog -Raw -Encoding UTF8) $version } else { $null }
if ($entry) {
	Write-Host "Notes: CHANGELOG.md's entry for $version"
	if ($entry -notmatch "(?m)^### +What's new\s*$" -and $entry -notmatch '(?m)^### +What changed\s*$') { Write-Warning "CHANGELOG.md's entry for $version has neither ""### What's new"" nor ""### What changed""." }
	if ($entry -notmatch '(?m)^### +Before you update\s*$') { Write-Warning "CHANGELOG.md's entry for $version has no ""### Before you update"" (write ""Nothing: it updates itself as usual."" when updating needs nothing)." }
}
else {
	Write-Warning "CHANGELOG.md has no ""## $version"" entry: the notes list the commits instead. Write the entry before publishing."
	$commits = Get-CommitsSince $version
	$said = if ($commits.Since) { "its commits since $($commits.Since)" } else { 'its latest commits' }
	$entry = "### What changed`n`nNo changelog entry was written for this version."
	if ($commits.Lines.Count) { $entry += " These are ${said}:`n`n" + (($commits.Lines | ForEach-Object { "- $_" }) -join "`n") }
}
if ($Publish) {
	# gh reports "release not found" on stderr, which Windows PowerShell would turn into a stopping error.
	$ErrorActionPreference = 'Continue'
	gh release view $tag --repo $Repo --json tagName 2>&1 | Out-Null
	$exists = $LASTEXITCODE -eq 0
	$ErrorActionPreference = 'Stop'
	if ($exists) { throw "Release $tag already exists on $Repo. Raise VersionPrefix in HEI.Agent.csproj first." }
}

# Only this script's own files are replaced, so -Out can point at a folder that holds other things.
New-Item -ItemType Directory -Force $Out | Out-Null
Get-ChildItem $Out -File | Where-Object { $_.Name -like 'Heiward-*.exe' -or $_.Name -eq 'SHA256SUMS.txt' } | Remove-Item
$work = Join-Path ([IO.Path]::GetTempPath()) "heiward-release-$([guid]::NewGuid().ToString('N'))"

try {
	foreach ($arch in 'x64', 'arm64') {
		Write-Host "Building $arch..."
		# The source is explicit so a PC whose NuGet config lists none can still fetch the runtime packs.
		dotnet publish $project -c Release -r "win-$arch" --self-contained `
			-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
			--source https://api.nuget.org/v3/index.json -o (Join-Path $work $arch) -nologo -v q
		if ($LASTEXITCODE -ne 0) { throw "The $arch build failed." }
		Copy-Item (Join-Path $work "$arch\hei.exe") (Join-Path $Out "Heiward-$version-$arch.exe")
	}
}
finally {
	Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

Get-ChildItem $Out -Filter 'Heiward-*.exe' | Sort-Object Name | ForEach-Object {
	"$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
} | Set-Content (Join-Path $Out 'SHA256SUMS.txt') -Encoding ascii

$files = Get-ChildItem $Out -File | Where-Object { $_.Name -like "Heiward-$version-*.exe" -or $_.Name -eq 'SHA256SUMS.txt' } | Sort-Object Name
Write-Host "Built in ${Out}:"
$files | ForEach-Object { '  {0,-30} {1,7:N1} MB' -f $_.Name, ($_.Length / 1MB) }

if (-not $Publish) {
	Write-Host "`nTo release them: rerun with -Publish (creates $tag on $Repo and uploads these files)."
	return
}

if (-not $Notes) {
	$Notes = @"
Heiward $version, built from $($commit.Substring(0, 7)), with the Steward's kit $kit.

$entry

### Installing

Download **Heiward-$version-x64.exe** for Intel or AMD PCs, or **Heiward-$version-arm64.exe** for Arm PCs such as Snapdragon, and run it. It sets up everything it needs by itself.

These builds aren't code-signed yet: on the first run Windows says "Windows protected your PC". Click **More info**, then **Run anyway**. ``SHA256SUMS.txt`` lists each file's SHA-256 (PowerShell: ``Get-FileHash``).
"@
}
elseif (Test-Path $Notes) {
	$Notes = Get-Content $Notes -Raw
}

Write-Host "`nCreating release $tag on $Repo at $($commit.Substring(0, 7)) and uploading $($files.Count) files..."
# The notes go to gh in a file: Windows PowerShell passes a double quote inside an argument to
# other programs unescaped, which splits it ("Windows protected your PC" became four arguments,
# and gh took "protected" for a file to upload). UTF-8 without a BOM, so none shows in the notes.
$notesFile = Join-Path ([IO.Path]::GetTempPath()) "heiward-notes-$([guid]::NewGuid().ToString('N')).md"
[IO.File]::WriteAllText($notesFile, $Notes, (New-Object System.Text.UTF8Encoding $false))
try {
	# Named one by one: PowerShell doesn't expand wildcards for other programs.
	gh release create $tag @($files | ForEach-Object FullName) --repo $Repo --target $commit --title "Heiward $version" --notes-file $notesFile
	if ($LASTEXITCODE -ne 0) { throw 'gh release create failed.' }
}
finally {
	Remove-Item $notesFile -ErrorAction SilentlyContinue
}
Write-Host "Released: https://github.com/$Repo/releases/tag/$tag"
