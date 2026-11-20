# Builds the release package: the published app plus license, notices and the short readme,
# zipped, with a SHA-256 file next to the zip. The zip is then unpacked again and that copy is
# smoke-tested without the game, so what is tested is what ships.
#
# Releases are made by GitHub Actions (.github/workflows/release.yml), which runs this script.
# Run it yourself only to try the packaging; never upload what it produces from your machine.
#
#   powershell -ExecutionPolicy Bypass -File tools\package.ps1 -Version 0.1.0-alpha.1
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutDir = "artifacts",
    [string]$Dotnet = "dotnet"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Version '$Version' is not of the form 1.2.3 or 1.2.3-label." }

$out = Join-Path $root $OutDir
$name = "LootGoblinsMap-v$Version-win-x64"
$stage = Join-Path (Join-Path $out "stage") $name
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $stage | Out-Null

Write-Host "== publish $Version"
& $Dotnet publish (Join-Path $root "src\Tracker\Tracker.csproj") -c Release -r win-x64 "-p:Version=$Version" -o $stage --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Copy-Item (Join-Path $root "LICENSE") (Join-Path $stage "LICENSE.txt")
Copy-Item (Join-Path $root "THIRD-PARTY-NOTICES.md") $stage
Copy-Item (Join-Path $root "packaging\README.txt") $stage
New-Item -ItemType Directory -Force (Join-Path $stage "licenses") | Out-Null
Copy-Item (Join-Path $root "src\MapBuilder\ThirdParty\SoulsFormats\PROVENANCE.md") (Join-Path $stage "licenses\PROVENANCE-SoulsFormats.md")
Copy-Item (Join-Path $root "src\Tracker\ThirdParty\SoulMemory\PROVENANCE.md") (Join-Path $stage "licenses\PROVENANCE-SoulMemory.md")

Write-Host "== zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $out "$name.zip"
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
# The usual checksum file layout: "<hash>  <file name>", readable by sha256sum -c.
[System.IO.File]::WriteAllText("$zip.sha256", "$hash  $name.zip`n")
Write-Host ("{0}  {1}  ({2:N1} MB)" -f $hash, "$name.zip", ((Get-Item $zip).Length / 1MB))

Write-Host "== unpack the zip and list what is in it"
$unpacked = Join-Path $out "unpacked"
[System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $unpacked)
$app = Join-Path $unpacked $name
Get-ChildItem -LiteralPath $unpacked -Recurse -File | ForEach-Object {
    "{0,12:N0}  {1}" -f $_.Length, $_.FullName.Substring($unpacked.Length + 1)
}

Write-Host "== game-data guard on the package"
& (Join-Path $PSScriptRoot "check-no-game-data.ps1") -Folder $unpacked
if ($LASTEXITCODE -ne 0) { throw "the package failed the game-data guard" }

Write-Host "== smoke test of the unpacked zip (no game needed)"
& (Join-Path $PSScriptRoot "smoke-test.ps1") -AppDir $app -Version $Version -WorkDir (Join-Path $out "smoke")
if ($LASTEXITCODE -ne 0) { throw "smoke test failed" }

Write-Host "package: OK  $zip"
