$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$version = (Get-Content (Join-Path $root "VERSION.txt") -Raw).Trim()
$publish = Join-Path $root "artifacts\publish\server-win-x64"
$serverExe = Join-Path $publish "RadioVault.Server.exe"

& (Join-Path $root "package-server.ps1")
if ($LASTEXITCODE -ne 0) { throw "Radio Vault Server package preparation failed." }
if (-not (Test-Path $serverExe)) { throw "Radio Vault Server package preparation produced no executable." }

$compilerCandidates = @(
    $env:RV_INNO_COMPILER,
    (Join-Path (Split-Path $root -Parent) "..\tools\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path $env:LOCALAPPDATA "Inno Setup 6\ISCC.exe")
)
$compiler = $compilerCandidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path $_) } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($compiler)) {
    throw "Inno Setup 6 is required to build the standard Windows installer. Install JRSoftware.InnoSetup, then run this script again."
}

$installerOutput = Join-Path $root "artifacts\installer"
New-Item -ItemType Directory -Path $installerOutput -Force | Out-Null
$expectedInstaller = Join-Path $installerOutput "RadioVault.Server-$version-Setup.exe"
if (Test-Path $expectedInstaller) { Remove-Item $expectedInstaller -Force }

$env:RV_VERSION = $version
$env:RV_SERVER_PUBLISH = $publish
$env:RV_INSTALLER_OUTPUT = $installerOutput
try {
    & $compiler /Qp (Join-Path $root "installer\RadioVault.Server.iss")
    if ($LASTEXITCODE -ne 0) { throw "Radio Vault Server installer compilation failed." }
}
finally {
    Remove-Item Env:RV_VERSION -ErrorAction SilentlyContinue
    Remove-Item Env:RV_SERVER_PUBLISH -ErrorAction SilentlyContinue
    Remove-Item Env:RV_INSTALLER_OUTPUT -ErrorAction SilentlyContinue
}

if (-not (Test-Path $expectedInstaller)) {
    throw "The installer compiler completed without producing the expected setup executable."
}

Write-Host "Radio Vault Server installer created: $expectedInstaller" -ForegroundColor Green
