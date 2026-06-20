@echo off
REM ============================================================================
REM  Build the RaceReadyCheck Bridge into ONE Windows .exe and zip it for upload.
REM  Just double-click this file. Requires the .NET 8 SDK (one-time install):
REM    https://dotnet.microsoft.com/download/dotnet/8.0  (the "SDK x64" installer)
REM ============================================================================
setlocal
cd /d "%~dp0"

echo.
echo === Building the bridge (this can take a minute the first time) ===
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if errorlevel 1 (
  echo.
  echo BUILD FAILED. Make sure the .NET 8 SDK is installed, then run this again.
  pause
  exit /b 1
)

set "OUT=bin\Release\net8.0-windows\win-x64\publish"

echo.
echo === Packaging RaceReadyCheckBridge.zip ===
if exist RaceReadyCheckBridge.zip del RaceReadyCheckBridge.zip
if exist _pkg rmdir /s /q _pkg
mkdir _pkg
copy "%OUT%\RaceReadyCheckBridge.exe" _pkg\ >nul
copy "bridge.config.sample.json" _pkg\ >nul
if exist "rrc.ico" copy "rrc.ico" _pkg\ >nul
if exist "README.md" copy "README.md" _pkg\ >nul
powershell -NoProfile -Command "Compress-Archive -Path '_pkg\*' -DestinationPath 'RaceReadyCheckBridge.zip' -Force"
rmdir /s /q _pkg

echo.
echo ============================================================================
echo  DONE.  Created:  RaceReadyCheckBridge.zip
echo  Next: upload that zip with WinSCP to your server's  bridge\  folder
echo        (the same folder as index.html). The website download button then works.
echo ============================================================================
pause
