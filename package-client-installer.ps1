$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$version = (Get-Content (Join-Path $root "VERSION.txt") -Raw).Trim()
$publish = Join-Path $root "artifacts\publish\local-win-x64"
$clientExe = Join-Path $publish "TheRadioVault.exe"

& (Join-Path $root "package-release.ps1")
if ($LASTEXITCODE -ne 0) { throw "Radio Vault Client package preparation failed." }
if (-not (Test-Path $clientExe)) { throw "Radio Vault Client package preparation produced no executable." }

$compilerCandidates = @(
    $env:RV_INNO_COMPILER,
    (Join-Path (Split-Path $root -Parent) "..\tools\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path $env:LOCALAPPDATA "Inno Setup 6\ISCC.exe")
)
$compiler = $compilerCandidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path $_) } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($compiler)) {
    throw "Inno Setup 6 is required to build the standard Windows installer."
}

$installerOutput = Join-Path $root "artifacts\installer"
New-Item -ItemType Directory -Path $installerOutput -Force | Out-Null
$expectedInstaller = Join-Path $installerOutput "RadioVault.Client-$version-Setup.exe"
if (Test-Path $expectedInstaller) { Remove-Item $expectedInstaller -Force }

$env:RV_VERSION = $version
$env:RV_CLIENT_PUBLISH = $publish
$env:RV_INSTALLER_OUTPUT = $installerOutput
try {
    & $compiler /Qp (Join-Path $root "installer\RadioVault.Client.iss")
    if ($LASTEXITCODE -ne 0) { throw "Radio Vault Client installer compilation failed." }
}
finally {
    Remove-Item Env:RV_VERSION -ErrorAction SilentlyContinue
    Remove-Item Env:RV_CLIENT_PUBLISH -ErrorAction SilentlyContinue
    Remove-Item Env:RV_INSTALLER_OUTPUT -ErrorAction SilentlyContinue
}

if (-not (Test-Path $expectedInstaller)) {
    throw "The installer compiler completed without producing the expected client setup executable."
}

Write-Host "Radio Vault Client installer created: $expectedInstaller" -ForegroundColor Green
