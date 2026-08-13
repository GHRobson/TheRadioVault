param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [string]$ReportPath = ""
)

$ErrorActionPreference = "Stop"
$errors = [System.Collections.Generic.List[string]]::new()
function Add-FoundationError([string]$message) { $errors.Add($message) }

$version = (Get-Content (Join-Path $Root 'VERSION.txt') -Raw).Trim()
$avaloniaProjectPath = Join-Path $Root 'TheRadioVault.Desktop.Avalonia\TheRadioVault.Desktop.Avalonia.csproj'
$presentationProjectPath = Join-Path $Root 'TheRadioVault.Presentation\TheRadioVault.Presentation.csproj'

$requiredPaths = @(
    $avaloniaProjectPath,
    $presentationProjectPath,
    (Join-Path $Root 'TheRadioVault.Services\Contracts\ILibraryBrowseService.cs'),
    (Join-Path $Root 'TheRadioVault.Services\Services\LibraryBrowseService.cs'),
    (Join-Path $Root 'TheRadioVault.Services\Contracts\ILocalPlaybackLibraryService.cs'),
    (Join-Path $Root 'TheRadioVault.Services\Contracts\ILibraryActionService.cs'),
    (Join-Path $Root 'TheRadioVault.Services\Services\LibraryActionService.cs'),
    (Join-Path $Root 'TheRadioVault.Services\Contracts\IQueueService.cs'),
    (Join-Path $Root 'TheRadioVault.Services\Contracts\IMomentsService.cs'),
    (Join-Path $Root 'TheRadioVault.Services\Contracts\IResearchWorkspaceService.cs'),
    (Join-Path $Root 'TheRadioVault.Services\Contracts\IBroadcastDetailsService.cs'),
    (Join-Path $Root 'TheRadioVault.Services\Services\BroadcastDetailsService.cs'),
    (Join-Path $Root 'TheRadioVault.Services\Models\BroadcastDetailsModels.cs'),
    (Join-Path $Root 'TheRadioVault.Services\Models\ConnectedAccessModels.cs'),
    (Join-Path $Root 'TheRadioVault.Services\Models\ResearchWorkspaceModels.cs'),
    (Join-Path $Root 'TheRadioVault.Services\Services\ResearchWorkspaceService.cs'),
    (Join-Path $Root 'TheRadioVault.Services\Services\LocalPlaybackLibraryService.cs'),
    (Join-Path $Root 'TheRadioVault.Services\Models\LocalPlaybackModels.cs'),
    (Join-Path $Root 'TheRadioVault.Presentation\ViewModels\PlaybackViewModel.cs'),
    (Join-Path $Root 'TheRadioVault.Presentation\ViewModels\QueueViewModel.cs'),
    (Join-Path $Root 'TheRadioVault.Presentation\ViewModels\MomentsViewModel.cs'),
    (Join-Path $Root 'TheRadioVault.Presentation\ViewModels\ResearchWorkspaceViewModel.cs'),
    (Join-Path $Root 'TheRadioVault.Presentation\ViewModels\NowPlayingViewModel.cs'),
    (Join-Path $Root 'TheRadioVault.Presentation\ViewModels\FullBroadcastInfoViewModel.cs'),
    (Join-Path $Root 'TheRadioVault.Presentation\ViewModels\DesktopToolsViewModel.cs'),
    (Join-Path $Root 'TheRadioVault.Desktop.Avalonia\Playback\NAudioPlaybackEngine.cs'),
    (Join-Path $Root 'TheRadioVault.Desktop.Avalonia\Interactions\ElasticScroll.cs'),
    (Join-Path $Root 'TheRadioVault.Desktop.Avalonia\Composition\AvaloniaApplicationHost.cs'),
    (Join-Path $Root 'TheRadioVault.Desktop.Avalonia\App.axaml'),
    (Join-Path $Root 'TheRadioVault.Desktop.Avalonia\Views\MainWindow.axaml'),
    (Join-Path $Root 'TheRadioVault.Desktop.Avalonia\Views\DashboardView.axaml'),
    (Join-Path $Root 'TheRadioVault.Desktop.Avalonia\Views\LibraryView.axaml'),
    (Join-Path $Root 'TheRadioVault.Desktop.Avalonia\Views\QueueView.axaml'),
    (Join-Path $Root 'TheRadioVault.Desktop.Avalonia\Views\MomentsView.axaml'),
    (Join-Path $Root 'TheRadioVault.Desktop.Avalonia\Views\ResearchWorkspaceView.axaml'),
    (Join-Path $Root 'TheRadioVault.Desktop.Avalonia\Views\NowPlayingView.axaml'),
    (Join-Path $Root 'TheRadioVault.Desktop.Avalonia\Views\FullBroadcastInfoView.axaml'),
    (Join-Path $Root 'TheRadioVault.Desktop.Avalonia\Views\DesktopToolsView.axaml'),
    (Join-Path $Root 'TheRadioVault.Presentation\ViewModels\MainWindowViewModel.cs'),
    (Join-Path $Root 'TheRadioVault.Presentation\ViewModels\DashboardViewModel.cs'),
    (Join-Path $Root 'TheRadioVault.Presentation\ViewModels\LibraryViewModel.cs')
)
foreach ($requiredPath in $requiredPaths) {
    if (-not (Test-Path $requiredPath)) {
        Add-FoundationError "Avalonia foundation file is missing: $($requiredPath.Substring($Root.Length + 1))"
    }
}

if (Test-Path $avaloniaProjectPath) {
    [xml]$avaloniaProjectXml = Get-Content $avaloniaProjectPath -Raw
    $propertyGroup = @($avaloniaProjectXml.Project.PropertyGroup)[0]
    $targetFramework = [string]$propertyGroup.TargetFramework
    $useWpf = [string]$propertyGroup.UseWPF
    $projectVersion = [string]$propertyGroup.Version
    $assemblyName = [string]$propertyGroup.AssemblyName
    if ($assemblyName -ne 'TheRadioVault') { Add-FoundationError "Avalonia must own TheRadioVault.exe; found '$assemblyName'." }
    if ($targetFramework -ne 'net8.0') { Add-FoundationError "Avalonia shell must target net8.0, found '$targetFramework'." }
    if ($useWpf -eq 'true') { Add-FoundationError 'Avalonia shell must not enable WPF.' }
    if ($projectVersion -ne $version) { Add-FoundationError "Avalonia project version '$projectVersion' does not match VERSION.txt '$version'." }

    $packageVersions = @{}
    foreach ($group in @($avaloniaProjectXml.Project.ItemGroup)) {
        foreach ($package in @($group.PackageReference)) {
            if ($null -ne $package -and -not [string]::IsNullOrWhiteSpace([string]$package.Include)) {
                $packageVersions[[string]$package.Include] = [string]$package.Version
            }
        }
    }
    foreach ($requiredPackage in @('Avalonia','Avalonia.Desktop','Avalonia.Themes.Fluent')) {
        if (-not $packageVersions.ContainsKey($requiredPackage)) {
            Add-FoundationError "Avalonia package is missing: $requiredPackage"
        }
        elseif ($packageVersions[$requiredPackage] -ne '12.1.0') {
            Add-FoundationError "$requiredPackage must be pinned to 12.1.0 for Alpha 8."
        }
    }
    if (-not $packageVersions.ContainsKey('NAudio')) {
        Add-FoundationError 'The Avalonia local-playback engine requires the NAudio package.'
    }
    elseif ($packageVersions['NAudio'] -ne '2.3.0') {
        Add-FoundationError "NAudio must be pinned to 2.3.0; found '$($packageVersions['NAudio'])'."
    }

    $projectReferences = @()
    foreach ($group in @($avaloniaProjectXml.Project.ItemGroup)) {
        foreach ($reference in @($group.ProjectReference)) {
            if ($null -ne $reference -and -not [string]::IsNullOrWhiteSpace([string]$reference.Include)) {
                $projectReferences += [string]$reference.Include
            }
        }
    }
    foreach ($requiredReference in @('TheRadioVault.Application','TheRadioVault.Core','TheRadioVault.Data','TheRadioVault.Services','TheRadioVault.Presentation','TheRadioVault.Web')) {
        if (-not ($projectReferences -match $requiredReference)) {
            Add-FoundationError "Avalonia project reference is missing: $requiredReference"
        }
    }
    if ($projectReferences -match 'TheRadioVault\\TheRadioVault.csproj|TheRadioVault.Platform.Windows') {
        Add-FoundationError 'Avalonia project must not reference a retired WPF project.'
    }
}

if (Test-Path $presentationProjectPath) {
    [xml]$presentationProjectXml = Get-Content $presentationProjectPath -Raw
    $presentationPropertyGroup = @($presentationProjectXml.Project.PropertyGroup)[0]
    if ([string]$presentationPropertyGroup.TargetFramework -ne 'net8.0') {
        Add-FoundationError 'TheRadioVault.Presentation must remain cross-platform net8.0.'
    }
    $presentationText = (Get-ChildItem (Join-Path $Root 'TheRadioVault.Presentation') -Recurse -Filter '*.cs' -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
    if ($presentationText -match '(?m)^\s*using\s+(?:global::)?Avalonia\b|(?m)^\s*using\s+(?:global::)?System\.Windows(?!\.Input)\b|\bMicrosoft\.Win32\b') {
        Add-FoundationError 'The toolkit-neutral presentation project contains a concrete UI-platform dependency.'
    }
    if ($presentationText -match '\bSqliteDatabase\b|\bDatabaseService\b|\bMicrosoft\.Data\.Sqlite\b') {
        Add-FoundationError 'Presentation view models must consume service contracts rather than database implementations.'
    }
}

$lifetimeSources = @(
    (Join-Path $Root 'TheRadioVault.Desktop.Avalonia\Platform\AvaloniaApplicationLifetime.cs'),
    (Join-Path $Root 'TheRadioVault.Desktop.Avalonia\Composition\AvaloniaApplicationHost.cs')
)
foreach ($lifetimeSourcePath in $lifetimeSources) {
    if (-not (Test-Path $lifetimeSourcePath)) { continue }
    $lifetimeText = Get-Content $lifetimeSourcePath -Raw
    if ($lifetimeText -match 'using\s+Avalonia\.Controls\.ApplicationLifetimes\s*;') {
        Add-FoundationError "Avalonia lifetime source must use explicit aliases: $($lifetimeSourcePath.Substring($Root.Length + 1))"
    }
    foreach ($aliasMarker in @(
        'using AvaloniaDesktopLifetime = Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;',
        'using RadioVaultApplicationLifetime = TheRadioVault.Application.Abstractions.IApplicationLifetime;')) {
        if ($lifetimeText -notmatch [regex]::Escape($aliasMarker)) {
            Add-FoundationError "Avalonia lifetime alias is missing: $aliasMarker"
        }
    }
}

$avaloniaSourceRoot = Join-Path $Root 'TheRadioVault.Desktop.Avalonia'
$avaloniaSourceText = ''
if (Test-Path $avaloniaSourceRoot) {
    $avaloniaSourceText = (Get-ChildItem $avaloniaSourceRoot -Recurse -Filter '*.cs' -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
    if ($avaloniaSourceText -match '(?m)^\s*using\s+(?:global::)?System\.Windows(?!\.Input)\b|(?m)^\s*(?:using|global::)\s*(?:PresentationFramework|WindowsBase)\b') {
        Add-FoundationError 'Avalonia source contains a WPF dependency.'
    }
    foreach ($marker in @(
        'ApplicationServiceRegistry','services.CreateCompositionReport','services.Freeze()',
        'ILibraryBrowseService','ILocalPlaybackLibraryService','PlaybackSessionCoordinator',
        'PlaybackViewModel','QueueViewModel','MomentsViewModel','ResearchWorkspaceViewModel','ILibraryActionService','IQueueService','IMomentsService','IResearchWorkspaceService','IBroadcastDetailsService','NowPlayingViewModel','FullBroadcastInfoViewModel','DesktopToolsViewModel','ResearchWorkspaceService','NAudioPlaybackEngine','AvaloniaRadioVaultAnywhereService','ElasticScroll')) {
        if ($avaloniaSourceText -notmatch [regex]::Escape($marker)) {
            Add-FoundationError "Avalonia composition/platform marker is missing: $marker"
        }
    }
}

$playbackServiceText = ''
$playbackServicePath = Join-Path $Root 'TheRadioVault.Services\Services\LocalPlaybackLibraryService.cs'
if (Test-Path $playbackServicePath) {
    $playbackServiceText = Get-Content $playbackServicePath -Raw
    foreach ($marker in @('GetPreferredPlaybackPlan','LocalPlaybackSegment','ExpandPlaybackEpisodeIdsAsync','playback_state','IncrementPlayCount')) {
        if ($playbackServiceText -notmatch [regex]::Escape($marker)) {
            Add-FoundationError "Canonical playback service marker is missing: $marker"
        }
    }
}

$playbackVmPath = Join-Path $Root 'TheRadioVault.Presentation\ViewModels\PlaybackViewModel.cs'
if (Test-Path $playbackVmPath) {
    $playbackVmText = Get-Content $playbackVmPath -Raw
    foreach ($marker in @('PlaybackProgressCoordinator','PlaybackCompletionCoordinator','LoadAndPlayAsync','SeekTo','SegmentText','FlushAsync')) {
        if ($playbackVmText -notmatch [regex]::Escape($marker)) {
            Add-FoundationError "Playback view-model marker is missing: $marker"
        }
    }
}

$themeServicePath = Join-Path $Root 'TheRadioVault.Desktop.Avalonia\Platform\AvaloniaThemeService.cs'
if (Test-Path $themeServicePath) {
    $themeServiceText = Get-Content $themeServicePath -Raw
    foreach ($marker in @('Dispatcher.UIThread.CheckAccess()','Dispatcher.UIThread.Post(ApplyVariant)')) {
        if ($themeServiceText -notmatch [regex]::Escape($marker)) {
            Add-FoundationError "Avalonia theme UI-thread marker is missing: $marker"
        }
    }
}

$appThemePath = Join-Path $Root 'TheRadioVault.Desktop.Avalonia\App.axaml'
if (Test-Path $appThemePath) {
    $appThemeText = Get-Content $appThemePath -Raw
    foreach ($marker in @(
        'RequestedThemeVariant="Default"','ResourceDictionary.ThemeDictionaries','x:Key="Dark"','x:Key="Light"',
        'RvBackgroundBrush','RvAccentBrush','Border.shell-nav-frame.selected','Border.shell-nav-child-frame.selected','ListBox.library-list > ListBoxItem:selected',
    'ProgressBar.thin','Style Selector="ScrollViewer"','interactions:ElasticScroll.IsEnabled','IsScrollChainingEnabled','IsScrollInertiaEnabled')) {
        if ($appThemeText -notmatch [regex]::Escape($marker)) {
            Add-FoundationError "Avalonia design/scroll marker is missing: $marker"
        }
    }
}

$viewFiles = @(Get-ChildItem (Join-Path $Root 'TheRadioVault.Desktop.Avalonia\Views') -Recurse -Include '*.axaml','*.cs' -File)
$viewText = ($viewFiles | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
if ($viewText -match '\bSqliteDatabase\b|\bDatabaseService\b|\bCanonicalLibraryQueryService\b') {
    Add-FoundationError 'Avalonia views must not perform data access directly.'
}
foreach ($marker in @(
    'DashboardView','LibraryView','QueueView','MomentsView','CurrentPage','Technical archive details',
    'Playback.PlayPauseCommand','Playback.SkipBackCommand','Playback.SkipForwardCommand',
    'Playback.PositionMs','Playback.DurationMs','PlayCommand','ToggleFavouriteCommand','AddToQueueCommand','CreateCurrentCommand','PageTitle','ExpansionGlyph','ShowGridCommand','Full broadcast information','Text="Settings"')) {
    if ($viewText -notmatch [regex]::Escape($marker)) {
        Add-FoundationError "Avalonia playback vertical-slice marker is missing: $marker"
    }
}
foreach ($axamlFile in Get-ChildItem (Join-Path $Root 'TheRadioVault.Desktop.Avalonia\Views') -Recurse -File -Filter '*.axaml') {
    $axamlText = Get-Content $axamlFile.FullName -Raw
    if ($axamlText -match '<Grid\b[^>]*\bPadding\s*=') {
        Add-FoundationError "Unsupported Grid.Padding remains in $($axamlFile.FullName.Substring($Root.Length + 1))."
    }
    if ($axamlText -match '<TextBox\b[^>]*\bWatermark\s*=') {
        Add-FoundationError "Obsolete TextBox.Watermark remains in $($axamlFile.FullName.Substring($Root.Length + 1))."
    }
}

$solutionText = Get-Content (Join-Path $Root 'TheRadioVault.sln') -Raw
$solutionLaunchPath = Join-Path $Root 'TheRadioVault.slnLaunch'
if (-not (Test-Path $solutionLaunchPath)) {
    Add-FoundationError 'The shared Avalonia-default Visual Studio launch profile is missing.'
}
else {
    $solutionLaunchText = Get-Content $solutionLaunchPath -Raw
    if ($solutionLaunchText -notmatch 'TheRadioVault\\Desktop\\Avalonia|TheRadioVault.Desktop.Avalonia') {
        Add-FoundationError 'The shared solution launch profile does not start the Avalonia project.'
    }
    if ($solutionLaunchText -match 'TheRadioVault\\TheRadioVault.csproj') {
        Add-FoundationError 'The shared default launch profile references the retired WPF project.'
    }
}
$avaloniaProjectIndex = $solutionText.IndexOf('TheRadioVault.Desktop.Avalonia\TheRadioVault.Desktop.Avalonia.csproj')
if ($avaloniaProjectIndex -lt 0) {
    Add-FoundationError 'The Avalonia project is missing from TheRadioVault.sln.'
}
if ($solutionText -match 'TheRadioVault\\TheRadioVault.csproj|TheRadioVault.Platform.Windows') {
    Add-FoundationError 'TheRadioVault.sln still contains a retired WPF project.'
}

$report = [ordered]@{
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    buildIdentity = $version
    foundationVersion = '0.35-alpha9-knowledge-portability'
    avaloniaVersion = '12.1.0'
    naudioVersion = '2.3.0'
    targetFramework = 'net8.0'
    databaseSchema = 51
    lanCapabilityGeneration = 41
    apiVersion = 'v1'
    defaultDesktopShell = 'avalonia'
    canonicalExecutable = 'TheRadioVault.exe'
    retiredWpfShellRemoved = $true
    localLibraryReadOnly = $false
    designFoundation = $true
    designSystem = 'radio-vault-avalonia-v1'
    themeVariants = @('Dark','Light')
    uxAuditCompleted = $true
    progressiveDisclosure = $true
    contextualSecondaryActions = $true
    automaticSearchFiltering = $true
    technicalMetadataCollapsedByDefault = $true
    persistentPlaybackShell = $true
    dashboardVerticalSlice = $true
    libraryVerticalSlice = $true
    libraryGridMigrated = $true
    compactBroadcastPeopleContext = $true
    fullBroadcastInformationMigrated = $true
    nowPlayingInformationParity = $true
    desktopToolsWorkspaceStarted = $true
    advancedMaintenanceOwnedByAvalonia = $true
    playbackMigrated = $true
    canonicalMultipartPlayback = $true
    progressPersistence = $true
    elasticOverscroll = $true
    elasticOverscrollStabilized = $true
    elasticOverscrollDirectTracking = $true
    expandableShowNavigation = $true
    favouriteActionsMigrated = $true
    queueMigrated = $true
    momentsMigrated = $true
    researchWorkspaceMigrated = $true
    metadataStudioMigrated = $true
    sourceDiagnosticsMigrated = $true
    researchImportHistoryMigrated = $true
    remoteResearchWritesGuarded = $true
    reducedMotionAware = $true
    nativeDesktopFederationPostponed = $false
    browserCompanionAvailable = $true
    remoteClientMigrated = $true
    connectedAccessWorkspaceMigrated = $true
    encryptedRemoteCache = $true
    automaticReconnect = $true
    remotePlaybackMigrated = $true
    remoteProgressWriteThrough = $true
    remoteFavouriteQueueMomentsWrites = $true
    remoteResearchReadParity = $true
    remoteMetadataWriteParity = $true
    remoteOwnedLocalDatabaseWritesBlocked = $true
    errors = $errors
    passed = ($errors.Count -eq 0)
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $Root 'artifacts\architecture\avalonia-foundation-report.json'
}
$reportDirectory = Split-Path -Parent $ReportPath
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$report | ConvertTo-Json -Depth 8 | Set-Content -Path $ReportPath -Encoding UTF8

if ($errors.Count -gt 0) {
    Write-Host 'Avalonia foundation validation failed:' -ForegroundColor Red
    $errors | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    throw "Avalonia foundation validation found $($errors.Count) issue(s)."
}

Write-Host "Avalonia-only shared-infrastructure foundation validation passed. Report: $ReportPath" -ForegroundColor Green
