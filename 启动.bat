@echo off
chcp 65001 >nul
title ChronoIsland - starting...
rem Locate dotnet runtime from project-local SDK
set "DOTNET_ROOT=%~dp0.tools\dotnet"
set "DOTNET_CLI_HOME=%~dp0.tools\home"
set "NUGET_PACKAGES=%~dp0.tools\nuget"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"

rem Prefer a published build if it exists
if exist "%~dp0publish\ChronoIsland.exe" (
  start "" "%~dp0publish\ChronoIsland.exe"
  exit /b
)

rem Otherwise run from build output via local runtime
if exist "%~dp0bin\Debug\net10.0-windows10.0.19041.0\win-x64\ChronoIsland.dll" (
  start "" "%DOTNET_ROOT%\dotnet.exe" "%~dp0bin\Debug\net10.0-windows10.0.19041.0\win-x64\ChronoIsland.dll"
  exit /b
)

echo ChronoIsland is not built yet.
echo Run build first:  dot.bat build -c Debug
pause
