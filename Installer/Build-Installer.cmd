@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-Installer.ps1"
exit /b %ERRORLEVEL%
