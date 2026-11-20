# Smoke test of a packaged app, without the game: the version it reports, the license files
# next to it, and a replay on the made-up demo map rendered to a picture in both themes.
#
#   powershell -ExecutionPolicy Bypass -File tools\smoke-test.ps1 -AppDir <unzipped folder> -Version 0.1.0-alpha.1
#
# The app refuses to run as administrator. GitHub's hosted runners are administrators, so
# there this script checks the version, the shipped files and that the app does refuse; the
# rendering is covered by the test suite on the runner and by this script on a normal account.
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

# A windowed program: piping its output makes the shell wait for it and collect what it prints.
function Invoke-App([string[]]$Arguments, [int]$ExpectedExit = 0) {
    $output = & $exe @Arguments | Out-String
    if ($LASTEXITCODE -ne $ExpectedExit) { throw "LootGoblinsMap.exe $($Arguments -join ' ') exited with $LASTEXITCODE, expected $ExpectedExit`n$output" }
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
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($elevated) {
    Write-Host "  this account is an administrator: the app must refuse to do anything more"
    # Exit code 3 is the app's "I am elevated and will not continue"; Invoke-App throws on any other.
    $null = Invoke-App @("--data", $data, "--make-demo") 3
    Check "refuses to run as administrator (exit code 3)" $true
    Check "  and wrote nothing" (-not (Test-Path -LiteralPath (Join-Path $data "maps")))
    if ($failed) { Write-Host "smoke test: FAILED"; exit 1 }
    Write-Host "smoke test: OK (reduced: version, files and the refusal to run elevated; rendering needs a normal account)"
    exit 0
}

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
