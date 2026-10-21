@echo off
chcp 65001 >nul 2>&1
setlocal EnableExtensions

net session >nul 2>&1
if errorlevel 1 (
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs" >nul 2>&1
    if errorlevel 1 (
        echo Failed to request administrator rights. Right-click this file and use "Run as administrator".
        pause
    )
    exit /b
)

cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Uninstall %*

echo.
endlocal
