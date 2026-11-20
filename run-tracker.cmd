@echo off
rem Starts the live tracker (never elevated; it refuses to run as administrator).
rem Uses the .NET 10 SDK in deps\dotnet when present, otherwise the one on PATH.
rem Extra arguments are passed through, e.g.  .\run-tracker.cmd --replay work\replay_path.txt
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
"%DOTNET%" "%ROOT%src\Tracker\bin\Release\net10.0-windows\Tracker.dll" --config "%ROOT%config.json" %*
