@echo off
REM ChronoIsland local .NET launcher
setlocal
set "ROOT=%~dp0"
set "DOTNET_ROOT=%ROOT%tools\dotnet"
set "DOTNET_CLI_HOME=%ROOT%tools\home"
set "NUGET_PACKAGES=%ROOT%tools\nuget"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "PATH=%DOTNET_ROOT%;%PATH%"
if not exist "%ROOT%tools\home" mkdir "%ROOT%tools\home"
if not exist "%ROOT%tools\nuget" mkdir "%ROOT%tools\nuget"
echo DOTNET_ROOT=%DOTNET_ROOT%
echo NUGET_PACKAGES=%NUGET_PACKAGES%
"%DOTNET_ROOT%\dotnet.exe" %*
endlocal
