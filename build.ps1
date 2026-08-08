$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
& (Join-Path $root "release-gate.ps1")

$publish = Join-Path $root "artifacts\publish\local-win-x64"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Path $publish -Force | Out-Null

dotnet publish (Join-Path $root "TheRadioVault.Desktop.Avalonia\TheRadioVault.Desktop.Avalonia.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:ContinuousIntegrationBuild=true -p:Deterministic=true `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw "Local-only publish failed." }

Write-Host "Local-only Avalonia application: $publish" -ForegroundColor Green
