@echo off
setlocal
cd /d "%~dp0"

echo Building Radio Vault 0.33.0 Alpha 2...
where dotnet >nul 2>nul
if errorlevel 1 (
  echo.
  echo The .NET 8 SDK could not be found. Install Visual Studio 2022 with .NET desktop development or the .NET 8 SDK.
  pause
  exit /b 1
)

dotnet restore "TheRadioVault.Desktop.Avalonia\TheRadioVault.Desktop.Avalonia.csproj"
if errorlevel 1 goto :failed

dotnet build "TheRadioVault.Desktop.Avalonia\TheRadioVault.Desktop.Avalonia.csproj" -c Release --no-restore
if errorlevel 1 goto :failed

set "APP=%~dp0TheRadioVault.Desktop.Avalonia\bin\Release\net8.0\TheRadioVault.exe"
if not exist "%APP%" (
  echo Build completed but the expected Avalonia executable was not found:
  echo %APP%
  pause
  exit /b 1
)

start "" "%APP%"
exit /b 0

:failed
echo.
echo Radio Vault Avalonia build failed.
pause
exit /b 1
