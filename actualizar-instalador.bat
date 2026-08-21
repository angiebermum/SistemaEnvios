@echo off
setlocal
chcp 65001 >nul

set "REPOSITORY_ROOT=%~dp0"
pushd "%REPOSITORY_ROOT%" >nul 2>&1
if errorlevel 1 (
    echo ERROR: No fue posible abrir la raiz del proyecto: %REPOSITORY_ROOT%
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
    "$installerDirectory = '%REPOSITORY_ROOT%Installer'; $scriptPath = Join-Path $installerDirectory 'Build-Installer.ps1'; $scriptText = Get-Content -Raw -Encoding UTF8 -LiteralPath $scriptPath; Invoke-Command -NoNewScope -ScriptBlock ([ScriptBlock]::Create($scriptText)) -ArgumentList $installerDirectory"
set "INSTALLER_RESULT=%ERRORLEVEL%"

popd >nul

if not "%INSTALLER_RESULT%"=="0" (
    echo.
    echo El instalador no fue actualizado. Revise el error indicado arriba.
    exit /b %INSTALLER_RESULT%
)

exit /b 0
