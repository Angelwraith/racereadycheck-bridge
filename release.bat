@echo off
REM ============================================================
REM  RaceReadyCheck Bridge - one-click release helper
REM  Double-click this, or run it from a terminal in this folder.
REM  It will: stage + commit + push your changes, then (optionally)
REM  create and push a version tag, which makes GitHub build the
REM  .exe and publish a new Release automatically.
REM ============================================================
setlocal enabledelayedexpansion
cd /d "%~dp0"

echo(
echo ============================================
echo   RaceReadyCheck Bridge - push a new version
echo ============================================
echo(

REM --- make sure this is a git repo ---
git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
    echo This folder is not a git repository.
    pause
    exit /b 1
)

echo Changes in this folder:
git status --short
echo(

REM --- show the latest existing version tag for reference ---
set "LATEST="
for /f "delims=" %%t in ('git tag --sort^=-v:refname 2^>nul') do (
    set "LATEST=%%t"
    goto :gotlatest
)
:gotlatest
if defined LATEST (echo Latest released tag: !LATEST!) else (echo No version tags yet.)
echo(

REM --- 1) commit + push source ---
set "MSG="
set /p "MSG=Commit message (leave blank to skip committing): "
if not "!MSG!"=="" (
    echo(
    echo ^> git add -A
    git add -A
    echo ^> git commit -m "!MSG!"
    git commit -m "!MSG!"
    echo ^> git push
    git push
    if errorlevel 1 (
        echo(
        echo *** Push failed - read the message above, fix it, and run again. ***
        pause
        exit /b 1
    )
)

REM --- 2) tag + push tag (this is what triggers the build/release) ---
echo(
set "VER="
set /p "VER=New version tag to RELEASE (e.g. v0.4.2, blank = no new build): "
if "!VER!"=="" (
    echo(
    echo Source pushed. No tag entered, so no new build/release was triggered.
    pause
    exit /b 0
)

echo(
echo ^> git tag !VER!
git tag !VER!
if errorlevel 1 (
    echo(
    echo *** Could not create tag !VER! - does it already exist? ***
    pause
    exit /b 1
)
echo ^> git push origin !VER!
git push origin !VER!
if errorlevel 1 (
    echo(
    echo *** Tag push failed. ***
    pause
    exit /b 1
)

echo(
echo ============================================
echo   Done! GitHub is now building !VER!.
echo   Progress: Actions tab on the repo
echo   Result:   github.com/Angelwraith/racereadycheck-bridge/releases
echo ============================================
echo(
pause
endlocal
