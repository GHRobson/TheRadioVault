$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$version = (Get-Content (Join-Path $root "VERSION.txt") -Raw).Trim()
$project = Join-Path $root "TheRadioVault.Server\TheRadioVault.Server.csproj"
. (Join-Path $root "tools\Build-Identity.ps1")
$identity = Get-RadioVaultBuildIdentity -Root $root -Version $version
$env:RADIOVAULT_BUILD_IDENTITY = $identity.EmbeddedIdentity

& (Join-Path $root "release-gate.ps1")

$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "publish\server-win-x64"
$package = Join-Path $artifacts "RadioVault.Server-$version-win-x64.zip"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
if (Test-Path $package) { Remove-Item $package -Force }
New-Item -ItemType Directory -Path $publish -Force | Out-Null

dotnet publish $project `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:ContinuousIntegrationBuild=true -p:Deterministic=true `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw "RadioVault Server publish failed." }

$features = @{
    userInterface = "settings-only"
    databaseSchema = 51
    radioVaultAnywhere = $true
    nativeClientCutover = "paired-server-startup-full-workspaces-canonical-range-streaming-settings-and-playback-handoff"
    nativeFederation = $true
    handoff = $true
    transcriptionWorker = "server-owned-whisper-diarization-voice-and-batches"
    transcriptionSetup = "server-settings"
    wiki = "server-owned-exploration-canonical-topic-auto-merge-interactive-timeline-quality-audit-rvwiki-packs"
}
Write-RadioVaultBuildInfo -Destination (Join-Path $publish "BUILD_INFO.json") `
    -Product "RadioVault Server" -Version $version -Runtime "win-x64" -Role "authoritative-server" `
    -Identity $identity -Features $features
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $package -CompressionLevel Optimal
Write-Host "RadioVault Server package created: $package" -ForegroundColor Green
