<#
.SYNOPSIS
  Builds the NAT installer for Windows (per user, no admin rights) with Velopack.

.DESCRIPTION
  Publishes a self-contained win-x64 build and packs it with vpk into releases/:
    Neuro.Nat-win-Setup.exe     the installer to hand out (Start menu and desktop shortcuts,
                               installs to %LocalAppData%\Neuro.Nat, starts with Windows)
    Neuro.Nat-<ver>-full.nupkg  the update package, plus deltas and releases.win.json
  Installed copies update themselves from the same files: copy the releases folder to the update
  source (a folder or share), or pass -PublishGitHub to upload a GitHub release.

  The installer is not signed, so SmartScreen warns on first run ("More info" > "Run anyway").

.PARAMETER Version
  Release version (SemVer, for example 0.2.0). Defaults to <Version> in Directory.Build.props.
  Each release that should reach installed copies needs a higher version than the last.

.PARAMETER UpdateSource
  Where installed copies look for updates: a GitHub repository URL, another URL, or a folder or
  share such as \\server\share\NAT. Defaults to UpdateSource in Directory.Build.props.

.PARAMETER PublishGitHub
  Also upload the release to GitHub releases (needs a token in GITHUB_TOKEN).

.PARAMETER Repository
  The GitHub repository to upload to. Defaults to UpdateSource in Directory.Build.props.

.EXAMPLE
  ./tools/installer/Build-Installer.ps1
  ./tools/installer/Build-Installer.ps1 -Version 0.2.0 -UpdateSource '\\nas\apps\NAT'
#>
param(
	[string]$Version,
	[string]$UpdateSource,
	[switch]$PublishGitHub,
	[string]$Repository
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$vpkVersion = '1.2.161'   # keep in step with Velopack in Directory.Packages.props

if (-not $Version) {
	$Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw 'No version: pass -Version or set <Version> in Directory.Build.props.' }

# The update address is written in one place only: UpdateSource in Directory.Build.props.
if (-not $Repository) {
	$Repository = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.UpdateSource | ForEach-Object { if ($_ -is [System.Xml.XmlElement]) { $_.InnerText } else { $_ } } | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Repository) { throw 'No repository: pass -Repository or set <UpdateSource> in Directory.Build.props.' }

$vpk = Get-Command vpk -ErrorAction SilentlyContinue
if (-not $vpk) {
	Write-Host "Installing the Velopack CLI (vpk $vpkVersion)..."
	dotnet tool install -g vpk --version $vpkVersion
	if ($LASTEXITCODE -ne 0) { throw 'Could not install vpk.' }
	$env:PATH += ";$env:USERPROFILE\.dotnet\tools"
}

$publish = Join-Path $root 'artifacts\installer\publish'
$releases = Join-Path $root 'artifacts\installer\releases'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

$publishArgs = @(
	'publish', (Join-Path $root 'src\Neuro.Nat.Desktop\Neuro.Nat.Desktop.csproj'),
	'-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
	'-o', $publish, "-p:Version=$Version"
)
if ($UpdateSource) { $publishArgs += "-p:UpdateSource=$UpdateSource" }
Write-Host "Publishing NAT $Version..."
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# With an earlier release in the releases folder, vpk also makes a small delta update.
Write-Host 'Packing the installer...'
vpk pack `
	--packId Neuro.Nat `
	--packVersion $Version `
	--packDir $publish `
	--mainExe NAT.exe `
	--packTitle 'NAT' `
	--packAuthors 'NAT' `
	--icon (Join-Path $root 'src\Neuro.Nat.Desktop\Assets\NAT.ico') `
	--shortcuts 'Desktop,StartMenuRoot' `
	--outputDir $releases
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed.' }

if ($PublishGitHub) {
	if (-not $env:GITHUB_TOKEN) { throw 'Set GITHUB_TOKEN to upload a GitHub release.' }
	vpk upload github --repoUrl $Repository --token $env:GITHUB_TOKEN --outputDir $releases --publish --releaseName "NAT $Version" --tag "v$Version"
	if ($LASTEXITCODE -ne 0) { throw 'vpk upload failed.' }
}

Write-Host ''
Write-Host "Installer: $(Join-Path $releases 'Neuro.Nat-win-Setup.exe')"
