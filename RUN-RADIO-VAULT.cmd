@echo off
setlocal
cd /d "%~dp0"

set "DEBUG_APP=%~dp0TheRadioVault.Desktop.Avalonia\bin\Debug\net8.0\TheRadioVault.exe"
set "RELEASE_APP=%~dp0TheRadioVault.Desktop.Avalonia\bin\Release\net8.0\TheRadioVault.exe"

if exist "%DEBUG_APP%" (
  start "" "%DEBUG_APP%"
  exit /b 0
)
if exist "%RELEASE_APP%" (
  start "" "%RELEASE_APP%"
  exit /b 0
)

echo The Avalonia executable has not been built yet. Building it now...
call "%~dp0BUILD-AND-RUN.cmd"
exit /b %ERRORLEVEL%
