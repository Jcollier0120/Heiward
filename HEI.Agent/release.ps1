# Builds Heiward's release files: one self-contained exe per architecture
# (Heiward-<version>-x64.exe, Heiward-<version>-arm64.exe), SHA256SUMS.txt, and beside them the GNU AGPL's text and the
# third-party notices every copy carries (LICENSE.txt, THIRD-PARTY-NOTICES.txt; tools\notices.ps1 writes the notices), in
# artifacts\heiward. With -Publish it then creates the GitHub release v<version> at the commit it built, and uploads every file.
# It fills the Steward's kit first (tools\kit.ps1), at the version kit.json pins.
# Each exe is code-signed before SHA256SUMS.txt is written (below, "Signing"); -Publish refuses unsigned files unless
# told -Unsigned.
# Needs the .NET 10 SDK on PATH, for -Publish the GitHub CLI (gh), signed in, and for signing the Windows SDK's
# signtool and the signing certificate (with SimplySign Desktop connected, for the cloud one).
#
#   powershell -ExecutionPolicy Bypass -File HEI.Agent\release.ps1              build (signed when the certificate is here)
#   powershell -ExecutionPolicy Bypass -File HEI.Agent\release.ps1 -Publish     build and sign, then release and upload
param(
	[string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\heiward'),
	# Create the GitHub release and upload the files once they're built.
	[switch]$Publish,
	# Release notes: text, or the path of a file with them. Default: CHANGELOG.md's entry for the version (or its commits), then which file to download and what Windows says on the first run.
	[string]$Notes,
	# The repository to release to (owner/name).
	[string]$Repo = 'Jcollier0120/Heiward',
	# Build without signing, and publish so: for a PC without the certificate.
	[switch]$Unsigned,
	# The code-signing certificate, by its SHA-1 thumbprint, in Cert:\CurrentUser\My: Heiward's publisher's, from Certum.
	[string]$Thumbprint = '6B654D9C34800D4307D42ACD63B925EDA5CE8D33',
	# The RFC 3161 timestamp server: a timestamped signature stays valid after the certificate expires.
	[string]$TimestampUrl = 'http://time.certum.pl'
)
$ErrorActionPreference = 'Stop'

# Signing. The key of Heiward's certificate is in Certum's cloud (SimplySign): SimplySign Desktop, connected with its
# ID and the token from the phone app, shows it to Windows as a smart card. Its key provider (SimplySign CSP) comes as
# x64 and x86 DLLs only, which an Arm64 process can't load, so this always runs the x64 signtool: on an Arm64 PC
# Windows runs it emulated, and it loads the x64 provider. Everything is checked before the build, so a release
# can't stop half done for want of the certificate.
function Find-SignTool {
	$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
	Get-ChildItem $kits -Directory -ErrorAction SilentlyContinue |
		Where-Object { $_.Name -match '^\d+(\.\d+){3}$' -and (Test-Path (Join-Path $_.FullName 'x64\signtool.exe')) } |
		Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 |
		ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' }
}
# Why this PC can't sign now, in a line; nothing when it can.
function Get-SignProblem([string]$SignTool, $Cert) {
	if (-not $SignTool) { return "The Windows SDK's signtool isn't installed (winget install Microsoft.WindowsSDK.10.0.26100 --override ""/features OptionId.SigningTools /quiet /norestart"")" }
	if (-not $Cert) { return "The signing certificate ($Thumbprint) isn't in Cert:\CurrentUser\My. Its key is in Certum's cloud: start SimplySign Desktop and connect (your SimplySign ID and the token from the phone app)" }
	if (-not $Cert.HasPrivateKey) { return "The signing certificate ($Thumbprint) is here without its key" }
	# The cloud key needs SimplySign Desktop running: without it, signtool would wait on a sign-in no one sees.
	$provider = (certutil -user -store My $Thumbprint 2>$null | Select-String '^\s*Provider = (.+)$' | Select-Object -First 1).Matches.Groups[1].Value
	if ($provider -match 'SimplySign' -and -not (Get-Process SimplySignDesktop -ErrorAction SilentlyContinue)) {
		return 'SimplySign Desktop isn''t running, and the signing key is in its cloud: start it and connect (your SimplySign ID and the token from the phone app)'
	}
	# Running isn't connected: a SimplySign Desktop whose session has ended leaves the certificate in the store, key and
	# all, and signtool then finds "no certificates that met all the given criteria". So a copy of signtool itself is
	# signed first (untimestamped, and thrown away), the one way to know the key answers.
	$probe = Join-Path ([IO.Path]::GetTempPath()) "heiward-signprobe-$([guid]::NewGuid().ToString('N')).exe"
	try {
		Copy-Item $SignTool $probe
		$r = Invoke-SignTool @('sign', '/sha1', $Thumbprint, '/fd', 'SHA256', $probe) 2
		if ($r.TimedOut) { return "signtool didn't sign a trial file within 2 minutes: SimplySign Desktop's sign-in is likely waiting. Connect it (your SimplySign ID and the token from the phone app)" }
		if (-not $r.Ok) { return (Get-SignFailure $r.Text) }
	}
	finally {
		Remove-Item $probe -ErrorAction SilentlyContinue
	}
}
# What a failed signtool sign says, put in a line that says what to do.
function Get-SignFailure([string]$Text) {
	if ($Text -match 'No certificates were found that met all the given criteria') {
		return "signtool can't reach the signing certificate's key ($Thumbprint): SimplySign Desktop isn't connected. Connect it (your SimplySign ID and the token from the phone app)"
	}
	# The key's provider (SimplySign CSP) is gone from under the certificate: SimplySign Desktop is running without its
	# virtual card (disconnected, or half started), or was installed without its x64 provider.
	if ($Text -match 'The specified CSP could not be found') {
		return "signtool can't load the signing key's provider, SimplySign CSP ($Thumbprint): SimplySign Desktop is running but not connected. Disconnect and connect it again (your SimplySign ID and the token from the phone app); if that doesn't do it, reinstall SimplySign Desktop, x64"
	}
	"signtool couldn't sign a trial file: $Text"
}
# Quoted as one argument for Start-Process, which joins its arguments with spaces as they are.
function ConvertTo-Argument([string]$Text) { if ($Text -match '[\s"]') { '"' + ($Text -replace '"', '\"') + '"' } else { $Text } }
# Runs signtool, at most $Minutes; its output, and whether it succeeded.
function Invoke-SignTool([string[]]$Arguments, [int]$Minutes) {
	$log = Join-Path ([IO.Path]::GetTempPath()) "heiward-signtool-$([guid]::NewGuid().ToString('N'))"
	try {
		$p = Start-Process -FilePath $script:signtool -ArgumentList (($Arguments | ForEach-Object { ConvertTo-Argument $_ }) -join ' ') `
			-NoNewWindow -PassThru -RedirectStandardOutput "$log.out" -RedirectStandardError "$log.err"
		# Without its handle taken now, Windows PowerShell's process object has no ExitCode once it has exited.
		$null = $p.Handle
		if (-not $p.WaitForExit($Minutes * 60000)) {
			$p.Kill()
			return [pscustomobject]@{ Ok = $false; TimedOut = $true; Text = "signtool didn't finish within $Minutes minutes. Is SimplySign Desktop connected? Its sign-in may be waiting." }
		}
		$text = ((Get-Content "$log.out", "$log.err" -ErrorAction SilentlyContinue) -join "`n").Trim()
		[pscustomobject]@{ Ok = $p.ExitCode -eq 0; TimedOut = $false; Text = $text }
	}
	finally {
		Remove-Item "$log.out", "$log.err" -ErrorAction SilentlyContinue
	}
}
# Signs one file and timestamps it, a few times over if the timestamp server fails; then checks the signature is
# Windows' to trust, and the certificate's.
function Set-ReleaseSignature([string]$File) {
	$sign = @('sign', '/sha1', $Thumbprint, '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', '/d', 'Heiward', '/du', "https://github.com/$Repo", $File)
	for ($try = 1; ; $try++) {
		$r = Invoke-SignTool $sign 5
		if ($r.Ok) { break }
		# A key out of reach stays so: only the timestamp server's hiccups are worth another try.
		if ($r.Text -match 'No certificates were found|The specified CSP could not be found') { throw "Couldn't sign $(Split-Path $File -Leaf): $(Get-SignFailure $r.Text)." }
		if ($r.TimedOut -or $try -ge 3) { throw "Couldn't sign $(Split-Path $File -Leaf): $($r.Text)" }
		Write-Warning "Signing $(Split-Path $File -Leaf) failed (try $try of 3), trying again in 15 s: $($r.Text)"
		Start-Sleep -Seconds 15
	}
	$v = Invoke-SignTool @('verify', '/pa', $File) 2
	if (-not $v.Ok) { throw "$(Split-Path $File -Leaf) is signed, but its signature doesn't verify: $($v.Text)" }
	$s = Get-AuthenticodeSignature $File
	if ($s.Status -ne 'Valid' -or $s.SignerCertificate.Thumbprint -ne $Thumbprint -or -not $s.TimeStamperCertificate) {
		throw "$(Split-Path $File -Leaf)'s signature isn't the certificate's, timestamped: $($s.Status), $($s.SignerCertificate.Subject)."
	}
}

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
# The tag of the newest release before $Version in this commit's history (v1.9.99), or null.
function Get-ReleaseBefore([string]$Version) {
	# git writes to stderr now and then, which Windows PowerShell would turn into a stopping error.
	$ErrorActionPreference = 'Continue'
	@(git -C $PSScriptRoot tag --merged HEAD --list 'v[0-9]*' 2>$null) |
		Where-Object { $_ -match '^v\d+\.\d+\.\d+$' -and [version]$_.Substring(1) -lt [version]$Version } |
		Sort-Object { [version]$_.Substring(1) } -Descending | Select-Object -First 1
}
function Get-CommitsSince([string]$Version) {
	$ErrorActionPreference = 'Continue'
	$since = Get-ReleaseBefore $Version
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

# The certificates a copy of Heiward updates to (Authenticode.cs's PublisherCertificates, as a release's source has it):
# each one's thumbprint, and its expiry where that source records one (since 1.9.100).
function Get-PinnedCertificates([string]$Source) {
	foreach ($m in [regex]::Matches($Source, '"([0-9A-Fa-f]{40})"(\s*,\s*new DateOnly\((\d{4}),\s*(\d{1,2}),\s*(\d{1,2})\))?')) {
		[pscustomobject]@{
			Thumbprint = $m.Groups[1].Value.ToUpperInvariant()
			Expires = if ($m.Groups[2].Success) { [datetime]::new([int]$m.Groups[3].Value, [int]$m.Groups[4].Value, [int]$m.Groups[5].Value) } else { $null }
		}
	}
}
$pinnedCertificates = @(Get-PinnedCertificates (Get-Content (Join-Path $PSScriptRoot 'Authenticode.cs') -Raw))

# The early warning (AuthenticodeTests.ARenewedCertificate_IsPinnedInTime fails from the same day): from three months
# before the last pinned certificate expires, a renewed one is due (README.md, "Renewing the certificate").
$lastExpiry = $pinnedCertificates | Where-Object { $_.Expires } | ForEach-Object { $_.Expires } | Sort-Object -Descending | Select-Object -First 1
if ($lastExpiry -and [datetime]::UtcNow.Date -ge $lastExpiry.AddMonths(-3)) {
	Write-Warning ("Heiward's code-signing certificate expires on {0:yyyy-MM-dd}, and no certificate that outlasts it is pinned. Renew it now, in the order HEI.Agent\README.md (""Renewing the certificate"") gives: get the renewed certificate from Certum, pin it in Authenticode.cs beside the current one, release that signed with the current one, and only then sign with the renewed one." -f $lastExpiry)
}

$signtool = $null
$signer = $null
if (-not $Unsigned) {
	# The copies on PCs update themselves only to an exe signed by a certificate they know (Authenticode.cs,
	# PublisherCertificates): signed by any other, a release would be downloaded and thrown away. A renewed certificate is
	# added there first, in a release signed by the one before; only the release after that is signed by it.
	if (($pinnedCertificates | ForEach-Object { $_.Thumbprint }) -notcontains $Thumbprint.ToUpperInvariant()) {
		throw "The certificate $Thumbprint isn't in Authenticode.cs's PublisherCertificates, so the copies already installed wouldn't update to this release. Add it there, and release that signed by a certificate they know, first."
	}
	# The copies run the release before this one: it must know the certificate already. Before 1.9.98 a copy checked no signature.
	$before = Get-ReleaseBefore $version
	if ($before) {
		$ErrorActionPreference = 'Continue'
		$beforeSource = (git -C $PSScriptRoot show "${before}:HEI.Agent/Authenticode.cs" 2>$null) -join "`n"
		$found = $LASTEXITCODE -eq 0
		$ErrorActionPreference = 'Stop'
		if ($found) {
			$known = @(Get-PinnedCertificates $beforeSource | ForEach-Object { $_.Thumbprint })
			if ($known -notcontains $Thumbprint.ToUpperInvariant()) {
				throw "The copies on $before update only to an exe signed by $($known -join ' or '), not $Thumbprint. Release this signed by a certificate they know (the default -Thumbprint), and sign with $Thumbprint from the release after it (README.md, ""Renewing the certificate"")."
			}
		}
	}
	$signtool = Find-SignTool
	$cert = Get-Item "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction SilentlyContinue
	$problem = Get-SignProblem $signtool $cert
	if ($problem) {
		if ($Publish) { throw "$problem. Releases are signed: fix that, or publish unsigned with -Unsigned." }
		Write-Warning "$problem. Building unsigned files (-Unsigned does so without this warning)."
		$Unsigned = $true
	}
	else {
		# The early warning counts from the expiry Authenticode.cs records: it must be the certificate's own.
		$recorded = ($pinnedCertificates | Where-Object { $_.Thumbprint -eq $Thumbprint.ToUpperInvariant() } | Select-Object -First 1).Expires
		$actual = $cert.NotAfter.ToUniversalTime().Date
		if ($recorded -ne $actual) {
			throw ("Authenticode.cs records the certificate $Thumbprint as expiring on {0}, but it expires on {1:yyyy-MM-dd} (UTC). Put new DateOnly({1:yyyy}, {1:%M}, {1:%d}) beside its thumbprint there." -f $(if ($recorded) { $recorded.ToString('yyyy-MM-dd') } else { 'no date' }), $actual)
		}
		$signer = $cert.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
		Write-Host "Signing as $signer, with $signtool (the certificate expires on $($actual.ToString('yyyy-MM-dd')))"
	}
}
elseif ($Publish) {
	Write-Warning 'Publishing unsigned: the copies already installed check a release''s signature, and won''t update to this one by themselves (Authenticode.cs).'
}

# Only this script's own files are replaced, so -Out can point at a folder that holds other things.
New-Item -ItemType Directory -Force $Out | Out-Null
$texts = 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt'
Get-ChildItem $Out -File | Where-Object { $_.Name -like 'Heiward-*.exe' -or $_.Name -eq 'SHA256SUMS.txt' -or $texts -contains $_.Name } | Remove-Item
$work = Join-Path ([IO.Path]::GetTempPath()) "heiward-release-$([guid]::NewGuid().ToString('N'))"

try {
	# Heiward.exe (HEI.Launcher), what Windows' Startup apps lists: built once (it runs on any processor), signed, and
	# embedded in both builds of hei.exe, which write it beside themselves at install.
	Write-Host "Building Heiward.exe..."
	$launcherProject = Join-Path $root 'HEI.Launcher\HEI.Launcher.csproj'
	dotnet build $launcherProject -c Release --source https://api.nuget.org/v3/index.json -o (Join-Path $work 'launcher') -nologo -v q
	if ($LASTEXITCODE -ne 0) { throw "The Heiward.exe build failed." }
	$launcher = Join-Path $work 'launcher\Heiward.exe'
	if (-not $Unsigned) {
		Write-Host "Signing Heiward.exe..."
		Set-ReleaseSignature $launcher
	}
	foreach ($arch in 'x64', 'arm64') {
		Write-Host "Building $arch..."
		# The source is explicit so a PC whose NuGet config lists none can still fetch the runtime packs.
		dotnet publish $project -c Release -r "win-$arch" --self-contained `
			-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
			"-p:HeiwardLauncher=$launcher" `
			--source https://api.nuget.org/v3/index.json -o (Join-Path $work $arch) -nologo -v q
		if ($LASTEXITCODE -ne 0) { throw "The $arch build failed." }
		$exe = Join-Path $Out "Heiward-$version-$arch.exe"
		Copy-Item (Join-Path $work "$arch\hei.exe") $exe
		if (-not $Unsigned) {
			Write-Host "Signing $arch..."
			Set-ReleaseSignature $exe
		}
	}
	# The texts a publish puts beside hei.exe (HEI.Agent.csproj), the same for both: hei.exe carries them too.
	foreach ($name in $texts) { Copy-Item (Join-Path $work "x64\$name") (Join-Path $Out $name) }
}
finally {
	Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

Get-ChildItem $Out -Filter 'Heiward-*.exe' | Sort-Object Name | ForEach-Object {
	"$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
} | Set-Content (Join-Path $Out 'SHA256SUMS.txt') -Encoding ascii

$files = Get-ChildItem $Out -File | Where-Object { $_.Name -like "Heiward-$version-*.exe" -or $_.Name -eq 'SHA256SUMS.txt' -or $texts -contains $_.Name } | Sort-Object Name
Write-Host "Built in ${Out}:"
$files | ForEach-Object { '  {0,-30} {1,7:N1} MB' -f $_.Name, ($_.Length / 1MB) }

if (-not $Publish) {
	Write-Host "`nTo release them: rerun with -Publish (creates $tag on $Repo and uploads these files)."
	return
}

if (-not $Notes) {
	# A signed file names its publisher, but Windows' SmartScreen still warns about a new download until enough people
	# have run it: the reputation comes with time, not with the certificate.
	$firstRun = if ($Unsigned) {
		'These builds aren''t code-signed: on the first run Windows says "Windows protected your PC". Click **More info**, then **Run anyway**.'
	}
	else {
		"Both files are code-signed by $signer, the publisher Windows names when you run them. Right after a release, until enough people have run it, Windows may still say ""Windows protected your PC"": click **More info**, check the publisher is $signer, then **Run anyway**."
	}
	$Notes = @"
Heiward $version, built from $($commit.Substring(0, 7)), with the Steward's kit $kit.

$entry

### Installing

Download **Heiward-$version-x64.exe** for Intel or AMD PCs, or **Heiward-$version-arm64.exe** for Arm PCs such as Snapdragon, and run it. It sets up everything it needs by itself.

$firstRun ``SHA256SUMS.txt`` lists each file's SHA-256 (PowerShell: ``Get-FileHash``).
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
