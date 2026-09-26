@echo off
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Watch-ArmorRefreshHotkey.ps1"
echo.
pause
