[CmdletBinding()]
param(
    [string]$ClientPublish = "artifacts/ci/windows-client",
    [string]$ServerPublish = "artifacts/ci/windows-server",
    [string]$OutputDirectory = "artifacts/ci/windows-installers"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Resolve-WorkspacePath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $root $Path))
}

function Assert-WorkspacePath([string]$Path) {
    $resolvedRoot = [IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $Path.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use a path outside the Radio Vault workspace: $Path"
    }
}

$clientPath = Resolve-WorkspacePath $ClientPublish
$serverPath = Resolve-WorkspacePath $ServerPublish
$outputPath = Resolve-WorkspacePath $OutputDirectory
Assert-WorkspacePath $clientPath
Assert-WorkspacePath $serverPath
Assert-WorkspacePath $outputPath

if (-not (Test-Path -LiteralPath (Join-Path $clientPath "TheRadioVault.exe"))) {
    throw "The published Windows Client was not found at $clientPath."
}
if (-not (Test-Path -LiteralPath (Join-Path $serverPath "RadioVault.Server.exe"))) {
    throw "The published Windows Server was not found at $serverPath."
}

$iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
$isccPath = if ($iscc) { $iscc.Source } else { $null }
if (-not $isccPath) {
    foreach ($candidate in @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    )) {
        if (Test-Path -LiteralPath $candidate) {
            $isccPath = $candidate
            break
        }
    }
}
if (-not $isccPath) {
    throw "Inno Setup 6 was not found. Install it before creating Windows setup executables."
}

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Recurse -Force
}
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

$version = (Get-Content (Join-Path $root "VERSION.txt") -Raw).Trim()
$env:RV_VERSION = $version
$env:RV_CLIENT_PUBLISH = $clientPath
$env:RV_SERVER_PUBLISH = $serverPath
$env:RV_INSTALLER_OUTPUT = $outputPath

foreach ($definition in @("RadioVault.Client.iss", "RadioVault.Server.iss")) {
    & $isccPath (Join-Path $root "installer\$definition")
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup failed to compile $definition with exit code $LASTEXITCODE."
    }
}

$clientInstaller = Join-Path $outputPath "RadioVault.Client-$version-Setup.exe"
$serverInstaller = Join-Path $outputPath "RadioVault.Server-$version-Setup.exe"
foreach ($installer in @($clientInstaller, $serverInstaller)) {
    if (-not (Test-Path -LiteralPath $installer)) {
        throw "The expected Windows installer was not created: $installer"
    }
}

Write-Host "Windows Client installer: $clientInstaller"
Write-Host "Windows Server installer: $serverInstaller"
