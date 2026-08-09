[CmdletBinding()]
param(
    [ValidateSet("osx-arm64", "osx-x64")]
    [string]$RuntimeIdentifier = "osx-arm64",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "TheRadioVault.Server\TheRadioVault.Server.csproj"
$version = (Get-Content (Join-Path $root "VERSION.txt") -Raw).Trim()
$artifactRoot = Join-Path $root "artifacts\macos-server\$RuntimeIdentifier"
$publishRoot = Join-Path $artifactRoot "publish"
$bundleRoot = Join-Path $artifactRoot "Radio Vault Server.app"
$contentsRoot = Join-Path $bundleRoot "Contents"
$macOsRoot = Join-Path $contentsRoot "MacOS"
$resourcesRoot = Join-Path $contentsRoot "Resources"
$zipPath = Join-Path $artifactRoot "RadioVault.Server-$version-$RuntimeIdentifier-unsigned.zip"

function Assert-WorkspacePath([string]$Path) {
    $resolvedRoot = [IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the Radio Vault workspace: $resolvedPath"
    }
}

function Write-BigEndianUInt32([IO.BinaryWriter]$Writer, [uint32]$Value) {
    $bytes = [BitConverter]::GetBytes($Value)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($bytes) }
    $Writer.Write($bytes)
}

function Write-Icns([string]$SourcePng, [string]$Destination) {
    $png = [IO.File]::ReadAllBytes($SourcePng)
    $stream = [IO.File]::Create($Destination)
    try {
        $writer = [IO.BinaryWriter]::new($stream, [Text.Encoding]::ASCII, $true)
        try {
            $writer.Write([Text.Encoding]::ASCII.GetBytes("icns"))
            Write-BigEndianUInt32 $writer ([uint32](16 + $png.Length))
            $writer.Write([Text.Encoding]::ASCII.GetBytes("ic09"))
            Write-BigEndianUInt32 $writer ([uint32](8 + $png.Length))
            $writer.Write($png)
        }
        finally { $writer.Dispose() }
    }
    finally { $stream.Dispose() }
}

Assert-WorkspacePath $artifactRoot
if ($SkipPublish) {
    if (-not (Test-Path -LiteralPath $publishRoot)) {
        throw "SkipPublish requires an existing publish directory: $publishRoot"
    }
    foreach ($generatedPath in @($bundleRoot, $zipPath, (Join-Path $artifactRoot "manifest.json"),
        (Join-Path $artifactRoot "RadioVaultServer.entitlements"), (Join-Path $artifactRoot "finalize-macos-server.sh"))) {
        if (Test-Path -LiteralPath $generatedPath) { Remove-Item -LiteralPath $generatedPath -Recurse -Force }
    }
}
elseif (Test-Path -LiteralPath $artifactRoot) {
    Remove-Item -LiteralPath $artifactRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $publishRoot, $macOsRoot, $resourcesRoot -Force | Out-Null

if (-not $SkipPublish) {
    & dotnet publish $project `
        -c $Configuration `
        -r $RuntimeIdentifier `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:ContinuousIntegrationBuild=true `
        -o $publishRoot
    if ($LASTEXITCODE -ne 0) { throw "The macOS server publish failed with exit code $LASTEXITCODE." }
}

$executable = Join-Path $publishRoot "RadioVault.Server"
if (-not (Test-Path -LiteralPath $executable)) {
    throw "The macOS server application host was not produced: $executable"
}
Copy-Item -Path (Join-Path $publishRoot "*") -Destination $macOsRoot -Recurse -Force

$shortVersion = if ($version -match '^(\d+\.\d+\.\d+)') { $Matches[1] } else { "0.1.0" }
$versionParts = $shortVersion.Split('.')
$bundleVersion = "{0}.{1}.{2}" -f ([int]$versionParts[1]), ([int]$versionParts[2]), 9
$plistTemplate = Get-Content (Join-Path $root "installer\macos\ServerInfo.plist") -Raw
$plist = $plistTemplate.Replace("@SHORT_VERSION@", $shortVersion).Replace("@BUNDLE_VERSION@", $bundleVersion)
[IO.File]::WriteAllText((Join-Path $contentsRoot "Info.plist"), $plist, [Text.UTF8Encoding]::new($false))

$logoPath = Join-Path $root "TheRadioVault.Server\Assets\RadioVault.Server-Logo.png"
Copy-Item -LiteralPath $logoPath -Destination (Join-Path $resourcesRoot "RadioVaultServer.png") -Force
Write-Icns $logoPath (Join-Path $resourcesRoot "RadioVaultServer.icns")
Copy-Item -LiteralPath (Join-Path $root "installer\macos\RadioVault.entitlements") `
    -Destination (Join-Path $artifactRoot "RadioVaultServer.entitlements") -Force
Copy-Item -LiteralPath (Join-Path $root "installer\macos\finalize-macos-server.sh") `
    -Destination (Join-Path $artifactRoot "finalize-macos-server.sh") -Force

$files = @(
    Get-ChildItem -LiteralPath $bundleRoot -Recurse -File
    Get-Item -LiteralPath (Join-Path $artifactRoot "RadioVaultServer.entitlements")
    Get-Item -LiteralPath (Join-Path $artifactRoot "finalize-macos-server.sh")
) | Sort-Object FullName
$manifest = [ordered]@{
    product = "Radio Vault Server"
    version = $version
    runtimeIdentifier = $RuntimeIdentifier
    bundleIdentifier = "com.theradiovault.server"
    entryPoint = "Radio Vault Server.app/Contents/MacOS/RadioVault.Server"
    signed = $false
    requiresMacFinalization = $true
    files = @($files | ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($artifactRoot.Length + 1).Replace('\', '/')
            length = $_.Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        }
    })
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $artifactRoot "manifest.json")
$archiveFiles = @($files) + @(Get-Item -LiteralPath (Join-Path $artifactRoot "manifest.json"))

Add-Type -AssemblyName System.IO.Compression
$zipStream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew)
$zipArchive = [IO.Compression.ZipArchive]::new($zipStream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $archiveFiles) {
        $entryPath = $file.FullName.Substring($artifactRoot.Length + 1).Replace('\', '/')
        $entry = $zipArchive.CreateEntry($entryPath, [IO.Compression.CompressionLevel]::Optimal)
        $entryStream = $entry.Open()
        $sourceStream = [IO.File]::OpenRead($file.FullName)
        try { $sourceStream.CopyTo($entryStream) }
        finally { $sourceStream.Dispose(); $entryStream.Dispose() }
    }
}
finally {
    $zipArchive.Dispose()
    $zipStream.Dispose()
}

Write-Host "Radio Vault Mac Server bundle created."
Write-Host "Bundle: $bundleRoot"
Write-Host "Unsigned transfer archive: $zipPath"
Write-Host "After extracting the ZIP on macOS, run: zsh finalize-macos-server.sh"
