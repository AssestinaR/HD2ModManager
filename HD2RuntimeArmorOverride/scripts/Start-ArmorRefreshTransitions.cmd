@echo off
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Watch-ArmorRefresh.ps1" -NativeOwnerTransitions -CaptureSeconds 10
echo.
pause
