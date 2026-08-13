$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$version = (Get-Content (Join-Path $root "VERSION.txt") -Raw).Trim()
& (Join-Path $root "build.ps1")

$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "publish\local-win-x64"
$package = Join-Path $artifacts "RadioVault-$version-local-win-x64.zip"
if (Test-Path $package) { Remove-Item $package -Force }

$buildInfo = [ordered]@{
    product = "The Radio Vault"
    version = $version
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    runtime = "win-x64"
    shell = "avalonia-native-client-transition"
    databaseSchema = 51
    networkRuntime = "dedicated-server-loopback-full-client-services"
    radioVaultAnywhere = $true
    nativeDesktopFederation = $true
    remoteLibrary = $true
    remoteStreaming = $true
    pairing = $true
    handoff = $true
    nativeClientCutover = "paired-server-startup-full-workspaces-canonical-range-streaming-settings-and-playback-handoff"
    transcriptionWorker = "dedicated-server"
    transcriptionControl = "authenticated-loopback"
    wiki = "exploration-dashboard-inline-links-interactive-timeline-canonical-topic-auto-merge-quality-audit-rvwiki-packs"
}
$buildInfo | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $publish "BUILD_INFO.json") -Encoding UTF8
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $package -CompressionLevel Optimal
Write-Host "Package created: $package" -ForegroundColor Green
