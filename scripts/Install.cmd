@echo off
where pwsh.exe >nul 2>&1
if errorlevel 1 (
  echo PowerShell 7 is required. Windows PowerShell 5.1 is not supported.
  pause
  exit /b 1
)
pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -LaunchSettings
if errorlevel 1 pause
