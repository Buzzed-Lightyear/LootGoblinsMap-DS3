@echo off
rem Development: builds and starts the app (never elevated; it refuses to run as administrator)
rem with the repository's config.json, so maps come from out\ and its own data goes to work\appdata.
rem Uses the .NET 10 SDK in deps\dotnet when present, otherwise the one on PATH.
rem Extra arguments are passed through, e.g.
rem   .\run-tracker.cmd --overlay
rem   .\run-tracker.cmd --debug --replay work\replay_path.txt
rem   .\run-tracker.cmd --probe ^| more
setlocal
set "ROOT=%~dp0"
set "DOTNET=dotnet"
if exist "%ROOT%deps\dotnet\dotnet.exe" set "DOTNET=%ROOT%deps\dotnet\dotnet.exe"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
"%DOTNET%" build "%ROOT%src\Tracker\Tracker.csproj" -c Release -v q > "%TEMP%\lootgoblins-tracker-build.log" 2>&1
if errorlevel 1 (
  type "%TEMP%\lootgoblins-tracker-build.log"
  echo Build failed.
  exit /b 1
)
"%DOTNET%" "%ROOT%src\Tracker\bin\Release\net10.0-windows\LootGoblinsMap.dll" --config "%ROOT%config.json" %*
