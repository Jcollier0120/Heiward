# Builds Heiward's release files: one self-contained exe per architecture
# (Heiward-<version>-x64.exe, Heiward-<version>-arm64.exe) and SHA256SUMS.txt, in artifacts\heiward.
# Needs the .NET 10 SDK on PATH. Publishing them is a separate step (VDF.Agent\README.md, "Making a release").
param(
	[string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\heiward')
)
$ErrorActionPreference = 'Stop'

if (-not ((dotnet --list-sdks 2>$null) -match '^10\.')) {
	throw 'The .NET 10 SDK is not on PATH ("dotnet --list-sdks" lists no 10.x). Install it, or put its folder first on PATH.'
}

$project = Join-Path $PSScriptRoot 'VDF.Agent.csproj'
$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.VersionPrefix | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No VersionPrefix in $project." }

$commit = git -C $PSScriptRoot rev-parse --short HEAD
if (git -C $PSScriptRoot status --porcelain) { Write-Warning 'The working tree has uncommitted changes, and the build includes them.' }
Write-Host "Heiward $version from commit $commit"

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

Write-Host "Done: $Out"
Get-ChildItem $Out -File | Where-Object { $_.Name -like 'Heiward-*.exe' -or $_.Name -eq 'SHA256SUMS.txt' } |
	ForEach-Object { '  {0,-30} {1,7:N1} MB' -f $_.Name, ($_.Length / 1MB) }
