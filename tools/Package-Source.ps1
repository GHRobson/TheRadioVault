param(
    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$version = (Get-Content (Join-Path $root "VERSION.txt") -Raw).Trim()
$excludedDirectories = '[\\/](bin|obj|artifacts|\.git|\.vs|\.vscode|\.idea|TestResults|coverage|packages)[\\/]'
$rootInstaller = '^RadioVault\.(Client|Server)-.+-Setup\.exe$'
$excludedNames = @('.DS_Store', 'Thumbs.db')
$excludedExtensions = @(
    '.user', '.suo', '.tmp', '.bak', '.orig', '.log', '.dmp', '.stackdump',
    '.db', '.sqlite', '.sqlite3', '.sqlite-wal', '.sqlite-shm',
    '.pfx', '.p12', '.key', '.pem', '.mobileprovision'
)
$files = @(Get-ChildItem $root -Recurse -File | Where-Object {
    $_.FullName -notmatch $excludedDirectories -and
    $_.Name -ne 'SOURCE_MANIFEST.sha256.json' -and
    $_.Name -notin $excludedNames -and
    $_.Extension -notin $excludedExtensions -and
    -not ($_.Name -eq '.env' -or ($_.Name -like '.env.*' -and $_.Name -ne '.env.example')) -and
    -not ($_.DirectoryName -eq $root -and $_.Name -match $rootInstaller)
})
$manifestEntries = @($files | ForEach-Object {
    [ordered]@{
        path = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
        bytes = $_.Length
        sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
} | Sort-Object path)
$manifest = [ordered]@{
    version = $version
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    fileCount = $manifestEntries.Count
    files = $manifestEntries
}
$manifestPath = Join-Path $root "SOURCE_MANIFEST.sha256.json"
$manifest | ConvertTo-Json -Depth 5 | Set-Content $manifestPath -Encoding UTF8

$allFiles = @($files) + @(Get-Item $manifestPath)
$destinationPath = [IO.Path]::GetFullPath($Destination)
$destinationDirectory = Split-Path $destinationPath -Parent
New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
if (Test-Path $destinationPath) { Remove-Item $destinationPath -Force }

Add-Type -AssemblyName System.IO.Compression
$archiveStream = [IO.File]::Open($destinationPath, [IO.FileMode]::CreateNew)
$archive = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $allFiles) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
        $entryStream = $entry.Open()
        $sourceStream = [IO.File]::OpenRead($file.FullName)
        try { $sourceStream.CopyTo($entryStream) }
        finally { $sourceStream.Dispose(); $entryStream.Dispose() }
    }
}
finally {
    $archive.Dispose()
    $archiveStream.Dispose()
}

Write-Host "Source package created: $destinationPath" -ForegroundColor Green
