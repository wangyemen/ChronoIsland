@echo off
chcp 65001 >nul
title ChronoIsland - publishing...
set "DOTNET_ROOT=%~dp0.tools\dotnet"
set "DOTNET_CLI_HOME=%~dp0.tools\home"
set "NUGET_PACKAGES=%~dp0.tools\nuget"
set "DOTNET_NOLOGO=1"

echo Publishing self-contained build (first time may take a few minutes)...
call "%DOTNET_ROOT%\dotnet.exe" publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o "%~dp0publish"
if errorlevel 1 (
  echo.
  echo Publish FAILED. See messages above.
  pause
  exit /b 1
)
echo.
echo Done: %~dp0publish\ChronoIsland.exe
echo This folder can be copied anywhere and run standalone.
pause
