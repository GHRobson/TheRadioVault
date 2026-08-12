$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$version = (Get-Content (Join-Path $root "VERSION.txt") -Raw).Trim()
$project = Join-Path $root "TheRadioVault.Desktop.Avalonia\TheRadioVault.Desktop.Avalonia.csproj"
$solution = Join-Path $root "TheRadioVault.sln"
$tests = Join-Path $root "TheRadioVault.Tests\TheRadioVault.Tests.csproj"
$webTests = Join-Path $root "TheRadioVault.Web.Tests\TheRadioVault.Web.Tests.csproj"
$sourceChecks = Join-Path $root "TheRadioVault.SourceChecks\TheRadioVault.SourceChecks.csproj"

Write-Host "Radio Vault Avalonia-only release gate: $version" -ForegroundColor Cyan
& (Join-Path $root "validate-source.ps1")
& (Join-Path $root "tools\Test-AvaloniaFoundation.ps1") -Root $root

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET 8 SDK is required to run the release gate."
}

dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw "Solution restore failed." }
dotnet build $solution -c Release --no-restore -warnaserror -p:ContinuousIntegrationBuild=true -p:Deterministic=true
if ($LASTEXITCODE -ne 0) { throw "The Avalonia-only solution build failed." }
dotnet run --project $tests -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw "One or more Radio Vault smoke tests failed." }
dotnet run --project $webTests -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw "One or more Radio Vault Web behavioral tests failed." }
dotnet run --project $sourceChecks -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw "One or more Radio Vault source-boundary checks failed." }

$assemblyPath = Join-Path $root "TheRadioVault.Desktop.Avalonia\bin\Release\net8.0\TheRadioVault.dll"
if (-not (Test-Path $assemblyPath)) { throw "The expected Avalonia assembly was not produced." }
$productVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($assemblyPath).ProductVersion
if ([string]::IsNullOrWhiteSpace($productVersion) -or -not $productVersion.StartsWith($version, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Built version '$productVersion' does not match VERSION.txt '$version'."
}

Write-Host "Avalonia-only architecture, deterministic subsystem tests, source-boundary checks and product-version checks passed." -ForegroundColor Green
