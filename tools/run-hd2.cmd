@echo off
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-hd2.ps1" %*
if errorlevel 1 pause
