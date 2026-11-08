@echo off
chcp 65001 >nul 2>&1
setlocal EnableExtensions

rem Elevate: the game usually lives under Program Files, which needs admin rights.
net session >nul 2>&1
if errorlevel 1 (
    rem Pass the target path through an environment variable. PowerShell reads it with
    rem $env:, so a path containing single quotes, & or % never goes through cmd
    rem quoting and cannot break the command.
    set "SP2_ELEV_TARGET=%~f0"
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath $env:SP2_ELEV_TARGET -Verb RunAs" >nul 2>&1
    if errorlevel 1 (
        echo Failed to request administrator rights. Right-click this file and use "Run as administrator".
        pause
    )
    endlocal
    exit /b
)

cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*

echo.
endlocal
