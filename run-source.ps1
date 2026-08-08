param([string]$DatabasePath = "")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
& (Join-Path $root "validate-source.ps1")

$arguments = @()
if (-not [string]::IsNullOrWhiteSpace($DatabasePath)) {
    $arguments += "--database"
    $arguments += $DatabasePath
}
dotnet run --project (Join-Path $root "TheRadioVault.Desktop.Avalonia\TheRadioVault.Desktop.Avalonia.csproj") -- @arguments
