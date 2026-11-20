# Fails when a file tracked by git could be game data. Run before every push and by CI.
#
#   powershell -ExecutionPolicy Bypass -File tools\check-no-game-data.ps1
#   powershell -ExecutionPolicy Bypass -File tools\check-no-game-data.ps1 -Folder <unzipped release>
#
# With -Folder it checks every file in that folder instead (the release package).
#
# A file fails when it
#   - has a game data extension (archives, maps, navigation, params, text, textures, saves);
#   - is one of the builder's output files (map.json, navpolys.bin, index.json, build_report.md);
#   - sits under out/ or work/ (git only);
#   - is an image outside the folders reserved for pictures of the synthetic demo map;
#   - is larger than 2 MB (the one exception: the released executable itself).
param([string]$Folder = "")

$ErrorActionPreference = "Stop"

$gameExtensions = @(
    ".bdt", ".bhd", ".bhd5", ".dcx", ".bnd", ".msb", ".nva", ".hkx", ".nvmhktbnd", ".hkxbhd", ".hkxbdt",
    ".tpf", ".tpfbhd", ".tpfbdt", ".fmg", ".msgbnd", ".param", ".parambnd", ".gparam", ".drawparam",
    ".flver", ".flv", ".mtd", ".mtdbnd", ".emevd", ".emeld", ".esd", ".talkesdbnd", ".luabnd", ".luagnl", ".luainfo", ".hks",
    ".anibnd", ".chrbnd", ".objbnd", ".partsbnd", ".texbnd", ".ffxbnd", ".fxr", ".shaderbnd", ".rumblebnd", ".breakobj",
    ".dds", ".fsb", ".fev", ".bik", ".bk2", ".entryfilelist", ".sl2", ".btl", ".btab", ".nvmbnd", ".ngp", ".mcp", ".mcg",
    ".ccm", ".loc", ".gfx", ".sib", ".clm2", ".btpb", ".ltl"
)
$builderOutputs = @("map.json", "navpolys.bin", "index.json", "build_report.md")
$imageExtensions = @(".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tga", ".tif", ".tiff", ".ico", ".svg")
$imageFolders = @("docs/images/", "site/images/")
$maxBytes = 2MB

$problems = New-Object System.Collections.Generic.List[string]

if ($Folder -ne "") {
    $root = (Resolve-Path $Folder).Path
    $files = Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object { $_.FullName.Substring($root.Length).TrimStart('\', '/').Replace('\', '/') }
    $what = "files in $root"
} else {
    $root = (& git rev-parse --show-toplevel).Trim()
    $files = (& git -C $root ls-files) | Where-Object { $_ -ne "" }
    $what = "files tracked by git"
}

foreach ($file in $files) {
    $name = [System.IO.Path]::GetFileName($file).ToLowerInvariant()
    $ext = [System.IO.Path]::GetExtension($file).ToLowerInvariant()
    $lower = $file.ToLowerInvariant()
    $full = Join-Path $root $file

    if ($gameExtensions -contains $ext) { $problems.Add("$file : game data file type ($ext)") }
    if ($builderOutputs -contains $name) { $problems.Add("$file : a file the map builder writes from game data") }
    if ($Folder -eq "" -and ($lower.StartsWith("out/") -or $lower.StartsWith("work/"))) { $problems.Add("$file : under out/ or work/") }
    if ($imageExtensions -contains $ext) {
        $allowed = $false
        foreach ($prefix in $imageFolders) { if ($lower.StartsWith($prefix)) { $allowed = $true } }
        if (-not $allowed) { $problems.Add("$file : an image outside " + ($imageFolders -join ", ")) }
    }
    if (Test-Path -LiteralPath $full) {
        $size = (Get-Item -LiteralPath $full).Length
        $isProgram = ($Folder -ne "" -and $name -eq "lootgoblinsmap.exe")
        if ($size -gt $maxBytes -and -not $isProgram) { $problems.Add("$file : $([math]::Round($size / 1MB, 1)) MB, larger than 2 MB") }
    }
}

Write-Host "game-data guard: checked $($files.Count) $what"
if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "  FAIL  $_" }
    Write-Host "game-data guard: $($problems.Count) problem(s). Game data must never be committed or released."
    exit 1
}
Write-Host "game-data guard: OK"
exit 0
