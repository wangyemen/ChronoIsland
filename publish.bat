@echo off
chcp 65001 >nul
title ChronoIsland - publish
set "ROOT=%~dp0"
set "DOTNET_ROOT=%ROOT%.tools\dotnet"
set "DOTNET_CLI_HOME=%ROOT%.tools\home"
set "NUGET_PACKAGES=%ROOT%.tools\nuget"
set "DOTNET_NOLOGO=1"

for /f %%a in ('powershell -NoProfile -Command "$x=[xml](Get-Content '%ROOT%ChronoIsland.csproj' -Raw); $x.Project.PropertyGroup.Version"') do set "VER=%%a"
if "%VER%"=="" set "VER=0.0.0"
echo Version: %VER%

echo [1/3] Publishing self-contained build...
call "%DOTNET_ROOT%\dotnet.exe" publish "%ROOT%ChronoIsland.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o "%ROOT%publish"
if errorlevel 1 goto fail

echo [2/3] Packing zip...
set "ZIP=%ROOT%ChronoIsland-%VER%-win-x64.zip"
if exist "%ZIP%" del /q "%ZIP%"
powershell -NoProfile -Command "Compress-Archive -Path '%ROOT%publish\*' -DestinationPath '%ZIP%' -CompressionLevel Optimal"
if errorlevel 1 goto fail

echo [3/3] Done
echo.
echo   zip: %ZIP%
echo.
echo Next:
echo   1. GitHub Release with tag v%VER%
echo   2. Upload the zip as release asset
echo   3. Edit version.txt: line1 = %VER%, line2 = asset URL
echo.
pause
exit /b 0

:fail
echo FAILED - see output above.
pause
exit /b 1
