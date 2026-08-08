$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$version = (Get-Content (Join-Path $root "VERSION.txt") -Raw).Trim()
$project = Join-Path $root "TheRadioVault.Server\TheRadioVault.Server.csproj"

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

$buildInfo = [ordered]@{
    product = "RadioVault Server"
    version = $version
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    runtime = "win-x64"
    role = "authoritative-server"
    userInterface = "settings-only"
    databaseSchema = 47
    radioVaultAnywhere = $true
    nativeClientCutover = "paired-server-startup-full-workspaces-canonical-range-streaming-settings-and-playback-handoff"
    nativeFederation = $true
    handoff = $true
    transcriptionWorker = "server-owned-whisper-diarization-voice-and-batches"
    transcriptionSetup = "server-settings"
    wiki = "server-owned-exploration-canonical-topic-auto-merge-interactive-timeline-quality-audit-rvwiki-packs"
}
$buildInfo | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $publish "BUILD_INFO.json") -Encoding UTF8
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $package -CompressionLevel Optimal
Write-Host "RadioVault Server package created: $package" -ForegroundColor Green
