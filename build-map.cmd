@echo off
rem Regenerates out\ from the game archives (read in place, nothing is copied or changed).
rem Uses the .NET 10 SDK in deps\dotnet when present, otherwise the one on PATH.
setlocal
set "ROOT=%~dp0"
set "DOTNET=dotnet"
if exist "%ROOT%deps\dotnet\dotnet.exe" set "DOTNET=%ROOT%deps\dotnet\dotnet.exe"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
"%DOTNET%" run --project "%ROOT%src\MapBuilder\MapBuilder.csproj" -c Release -- all --config "%ROOT%config.json" %*
