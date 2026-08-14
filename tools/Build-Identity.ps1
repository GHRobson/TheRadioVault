function Get-RadioVaultBuildIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [string]$Version = (Get-Content (Join-Path $Root "VERSION.txt") -Raw).Trim()
    )

    $commit = @($env:RADIOVAULT_BUILD_IDENTITY, $env:GITHUB_SHA) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1
    $sourceDirty = $false
    if ($commit -and $commit.EndsWith('.dirty', [StringComparison]::OrdinalIgnoreCase)) {
        $sourceDirty = $true
        $commit = $commit.Substring(0, $commit.Length - 6)
    }
    if ([string]::IsNullOrWhiteSpace($commit)) {
        try { $commit = (& git -C $Root rev-parse HEAD 2>$null).Trim() }
        catch { $commit = $null }
    }
    if ([string]::IsNullOrWhiteSpace($commit) -or $commit -notmatch '^[0-9a-fA-F]{7,64}$') {
        $commit = $null
    }

    if ($commit -and -not $sourceDirty -and -not $env:GITHUB_SHA) {
        try { $sourceDirty = -not [string]::IsNullOrWhiteSpace(((& git -C $Root status --porcelain 2>$null) -join "`n")) }
        catch { $sourceDirty = $false }
    }

    $dirtySuffix = if ($sourceDirty) { ".dirty" } else { "" }
    $embeddedIdentity = if ($commit) { $commit.ToLowerInvariant() + $dirtySuffix } else { "local" }
    $shortIdentity = if ($commit) {
        $commit.ToLowerInvariant().Substring(0, [Math]::Min(12, $commit.Length)) + $dirtySuffix
    } else { "local" }
    [pscustomobject]@{
        Version = $Version
        Commit = $commit
        SourceDirty = $sourceDirty
        EmbeddedIdentity = $embeddedIdentity
        ShortIdentity = $shortIdentity
        BuildIdentity = "$Version+$shortIdentity"
    }
}

function Write-RadioVaultBuildInfo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$Product,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$Runtime,
        [Parameter(Mandatory = $true)][string]$Role,
        [Parameter(Mandatory = $true)]$Identity,
        [hashtable]$Features = @{}
    )

    $document = [ordered]@{
        product = $Product
        version = $Version
        buildIdentity = $Identity.BuildIdentity
        commit = $Identity.Commit
        sourceDirty = $Identity.SourceDirty
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        runtime = $Runtime
        role = $Role
    }
    foreach ($key in $Features.Keys) { $document[$key] = $Features[$key] }
    $document | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Destination -Encoding UTF8
}
