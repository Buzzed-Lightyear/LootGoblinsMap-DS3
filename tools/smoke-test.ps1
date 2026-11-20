# Smoke test of a packaged app, without the game: the version it reports, the license files
# next to it, and a replay on the made-up demo map rendered to a picture in both themes.
#
#   powershell -ExecutionPolicy Bypass -File tools\smoke-test.ps1 -AppDir <unzipped folder> -Version 0.1.0-alpha.1
param(
    [Parameter(Mandatory = $true)][string]$AppDir,
    [string]$Version = "",
    [string]$WorkDir = ""
)

$ErrorActionPreference = "Stop"
$exe = Join-Path $AppDir "LootGoblinsMap.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "LootGoblinsMap.exe not found in $AppDir" }
if ($WorkDir -eq "") { $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ("lgm-smoke-" + [guid]::NewGuid().ToString("N")) }
if (Test-Path $WorkDir) { Remove-Item -Recurse -Force $WorkDir }
New-Item -ItemType Directory -Force $WorkDir | Out-Null

function Invoke-App([string[]]$Arguments) {
    # A windowed program: piping its output makes the shell wait for it and collect what it prints.
    $output = & $exe @Arguments | Out-String
    if ($LASTEXITCODE -ne 0) { throw "LootGoblinsMap.exe $($Arguments -join ' ') exited with $LASTEXITCODE`n$output" }
    return $output
}

$failed = $false
function Check([string]$What, [bool]$Ok) {
    if ($Ok) { Write-Host "  ok    $What" } else { Write-Host "  FAIL  $What"; $script:failed = $true }
}

foreach ($file in @("LICENSE.txt", "THIRD-PARTY-NOTICES.md", "README.txt", "Fonts\EBGaramond-Medium.ttf", "Fonts\OFL.txt")) {
    Check "ships $file" (Test-Path -LiteralPath (Join-Path $AppDir $file))
}

$reported = (Invoke-App @("--version")).Trim()
Write-Host "  $reported"
Check "reports a version" ($reported -match '^Loot Goblins Map \d+\.\d+\.\d+')
if ($Version -ne "") { Check "the version is $Version" ($reported -eq "Loot Goblins Map $Version") }

$data = Join-Path $WorkDir "data"
$demo = Invoke-App @("--data", $data, "--make-demo")
Check "writes the demo maps" (Test-Path -LiteralPath (Join-Path $data "maps\index.json"))
Check "the demo walks follow the floor" (-not $demo.Contains("straight line"))

foreach ($theme in @("parchment", "plain")) {
    foreach ($view in @("minimap", "full")) {
        $png = Join-Path $WorkDir "$theme-$view.png"
        $summary = Invoke-App @("--data", $data, "--replay", (Join-Path $data "demo_walk.txt"), "--png", $png, "--view", $view, "--theme", $theme, "--size", "1280x720")
        $size = 0
        if (Test-Path -LiteralPath $png) { $size = (Get-Item -LiteralPath $png).Length }
        Check "renders $theme $view ($size bytes)" ($size -gt 20000)
        Check "  the replay switched maps once and picked up six items" ($summary.Contains("map switches: 1, flag flips: 6, bonfire rests: 1"))
        Check "  the replay never left the floor" ($summary.Contains("0 off the shown map"))
    }
}

$export = Join-Path $WorkDir "export"
$null = Invoke-App @("--data", $data, "--export-feedback", $export)
Check "exports a feedback zip" (@(Get-ChildItem -LiteralPath $export -Filter "*.zip").Count -eq 1)

if ($failed) { Write-Host "smoke test: FAILED (pictures and data left in $WorkDir)"; exit 1 }
Write-Host "smoke test: OK (pictures in $WorkDir)"
exit 0
