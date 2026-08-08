@echo off
setlocal
cd /d "%~dp0"
echo Running Radio Vault 0.33.0 Alpha 2 release gate...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0release-gate.ps1"
set "EXITCODE=%ERRORLEVEL%"
if not "%EXITCODE%"=="0" (
  echo.
  echo Release gate failed with exit code %EXITCODE%.
  pause
  exit /b %EXITCODE%
)
echo.
echo Radio Vault RC release gate passed.
pause
