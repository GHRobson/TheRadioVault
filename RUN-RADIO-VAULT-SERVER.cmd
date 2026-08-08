@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-server-source.ps1" %*
if errorlevel 1 pause
endlocal
