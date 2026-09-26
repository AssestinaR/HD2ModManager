@echo off
chcp 65001 >nul
cd /d "%~dp0"
if exist "%~dp0bin\hd2mod.exe" (
  "%~dp0bin\hd2mod.exe" diagnostics armor wizard --manager-root "%~dp0."
) else (
  "%~dp0hd2mod.exe" diagnostics armor wizard --manager-root "%~dp0."
)
echo.
pause
