$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "TheRadioVault.Server\TheRadioVault.Server.csproj"
dotnet run --project $project -c Release -- @args
if ($LASTEXITCODE -ne 0) { throw "RadioVault Server exited with code $LASTEXITCODE." }
