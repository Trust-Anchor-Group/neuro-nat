<#
.SYNOPSIS
  Draws NAT's icon file (src/Neuro.Nat.Desktop/Assets/NAT.ico) from the same drawing the app uses
  for its live icons (IconArt.cs in the App project): the logo's ring around a white dot, on a dark
  tile, with plain arcs at 16 to 24 pixels. Run it again only when the design changes.
#>
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')

$env:NAT_MAKE_ICON = '1'
try {
	dotnet test (Join-Path $root 'tests\Neuro.Nat.App.Tests\Neuro.Nat.App.Tests.csproj') --filter 'FullyQualifiedName~IconTests.TheIconFileHoldsEverySize'
	if ($LASTEXITCODE -ne 0) { throw 'Drawing the icon failed.' }
}
finally {
	Remove-Item Env:NAT_MAKE_ICON
}

Write-Host "Wrote $(Join-Path $root 'src\Neuro.Nat.Desktop\Assets\NAT.ico')"
