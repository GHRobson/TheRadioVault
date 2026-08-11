$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$version = (Get-Content (Join-Path $root "VERSION.txt") -Raw).Trim()
$projectPath = Join-Path $root "TheRadioVault.Desktop.Avalonia\TheRadioVault.Desktop.Avalonia.csproj"
$hostPath = Join-Path $root "TheRadioVault.Desktop.Avalonia\Composition\AvaloniaApplicationHost.cs"

if ($version -ne "0.41.0") {
    throw "Unexpected VERSION.txt value: $version"
}
if (-not (Test-Path $projectPath)) { throw "Avalonia project is missing." }
if (-not (Test-Path $hostPath)) { throw "Avalonia composition root is missing." }
$serverIconPath = Join-Path $root "TheRadioVault.Server\Assets\RadioVault.Server.ico"
$serverLogoPath = Join-Path $root "TheRadioVault.Server\Assets\RadioVault.Server-Logo.png"
if (-not (Test-Path $serverIconPath) -or -not (Test-Path $serverLogoPath)) { throw "Dedicated server brand assets are missing." }
$serverProjectText = Get-Content (Join-Path $root "TheRadioVault.Server\TheRadioVault.Server.csproj") -Raw
$serverAppText = Get-Content (Join-Path $root "TheRadioVault.Server\App.axaml.cs") -Raw
$serverWindowText = Get-Content (Join-Path $root "TheRadioVault.Server\Views\ServerSettingsWindow.axaml") -Raw
$serverInstallerText = Get-Content (Join-Path $root "installer\RadioVault.Server.iss") -Raw
foreach ($marker in @('Assets\RadioVault.Server.ico', 'Assets\RadioVault.Server-Logo.png')) {
    if (($serverProjectText + $serverAppText + $serverWindowText + $serverInstallerText) -notmatch [regex]::Escape($marker)) { throw "Server brand reference missing: $marker" }
}
if (Test-Path (Join-Path $root "TheRadioVault")) { throw "The retired WPF shell source directory is still present." }
if (Test-Path (Join-Path $root "TheRadioVault.Platform.Windows\TheRadioVault.Platform.Windows.csproj")) { throw "The retired WPF adapter project is still present." }
$solutionText = Get-Content (Join-Path $root "TheRadioVault.sln") -Raw
if ($solutionText -match 'TheRadioVault\\TheRadioVault.csproj|TheRadioVault.Platform.Windows') { throw "The main solution still references retired WPF projects." }
$runSourceText = Get-Content (Join-Path $root "run-source.ps1") -Raw
if ($runSourceText -match 'WpfReference|TheRadioVault\\TheRadioVault.csproj') { throw "The source launcher still exposes the retired WPF shell." }

[xml]$project = Get-Content $projectPath -Raw
$propertyGroup = @($project.Project.PropertyGroup)[0]
if ([string]$propertyGroup.Version -ne $version) {
    throw "Avalonia project version does not match VERSION.txt."
}

$references = @()
foreach ($group in @($project.Project.ItemGroup)) {
    foreach ($reference in @($group.ProjectReference)) {
        if ($null -ne $reference) { $references += [string]$reference.Include }
    }
}
if (-not ($references | Where-Object { $_ -match "TheRadioVault.Web" })) {
    throw "The active Avalonia project does not reference TheRadioVault.Web."
}
if (-not ($references | Where-Object { $_ -match "TheRadioVault.Infrastructure" })) {
    throw "The active Avalonia project does not reference TheRadioVault.Infrastructure."
}
$infrastructureProject = Join-Path $root "TheRadioVault.Infrastructure\TheRadioVault.Infrastructure.csproj"
if (-not (Test-Path $infrastructureProject)) { throw "The shared infrastructure project is missing." }
foreach ($requiredSource in @("WebArchiveProvider.cs", "WebServerPreferences.cs", "LocalWebServerService.cs", "WebServerManager.cs", "SecureWebCertificateService.cs")) {
    if (-not (Test-Path (Join-Path $root "TheRadioVault.Infrastructure\Services\$requiredSource"))) {
        throw "Radio Vault Anywhere infrastructure source missing: $requiredSource"
    }
}

$remoteDirectory = Join-Path $root "TheRadioVault.Desktop.Avalonia\Remote"
if (Test-Path $remoteDirectory) { throw "The active Avalonia Remote directory still exists." }
if (Test-Path (Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\ConnectedAccessView.axaml")) {
    throw "ConnectedAccessView is still present in the active Avalonia UI."
}

$hostText = Get-Content $hostPath -Raw
foreach ($required in @("LocalPlaybackLibraryService", "NAudioPlaybackEngine", "LoopbackPlaybackHandoffService", "DedicatedServerRadioVaultAnywhereService")) {
    if ($hostText -notmatch [regex]::Escape($required)) { throw "Local-only composition marker missing: $required" }
}
if ($hostText -match 'RegisterSingleton<IPlaybackHandoffService>\(new NullPlaybackHandoffService') {
    throw "The native client still registers the disabled playback-handoff adapter."
}
if ($hostText -match "AvaloniaRemote|RemoteResearchPackTransferService") {
    throw "The active Avalonia composition still contains native federation runtime code."
}

$sourceFiles = Get-ChildItem $root -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj|artifacts|\.git|\.vs)[\\/]' }
foreach ($file in $sourceFiles) {
    if ($file.Name -eq 'SOURCE_MANIFEST.sha256.json') { continue }
    if ($file.Extension -in @('.csproj','.props','.targets','.axaml','.xaml','.xml')) {
        try { [xml](Get-Content $file.FullName -Raw) | Out-Null }
        catch { throw "Invalid XML/XAML: $($file.FullName.Substring($root.Length + 1)): $($_.Exception.Message)" }
    }
    if ($file.Extension -in @('.cs','.axaml','.xaml','.ps1','.cmd','.md','.txt','.json')) {
        $text = Get-Content $file.FullName -Raw
        if ($text -match '(?m)^(<<<<<<< .+|=======|>>>>>>> .+)\r?$') {
            throw "Merge conflict marker found: $($file.FullName.Substring($root.Length + 1))"
        }
    }
}


$browsePath = Join-Path $root "TheRadioVault.Services\Services\LibraryBrowseService.cs"
$browseText = Get-Content $browsePath -Raw
foreach ($requiredShow in @("Ron & Fez", "Bennington", "Opie & Anthony", "The Ron & Ron Show", "Ron Bennington Interviews", "Unmasked")) {
    $catalogPath = Join-Path $root "TheRadioVault.Core\Services\KnownShowCatalog.cs"
    $catalogText = Get-Content $catalogPath -Raw
    if ($catalogText -notmatch [regex]::Escape($requiredShow)) { throw "First-class show missing from catalog: $requiredShow" }
}
if ($browseText -notmatch "LoadCollectionSummaries" -or $browseText -notmatch "KnownShowCatalog.Collections") {
    throw "First-class sidebar collection projection is missing."
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/SIDEBAR-SHOW-SECTIONS-ACCEPTANCE.md"))) {
    throw "Sidebar show-section acceptance guide is missing."
}

$loopbackHandoffPath = Join-Path $root "TheRadioVault.Infrastructure\Services\LoopbackPlaybackHandoffService.cs"
if (-not (Test-Path $loopbackHandoffPath)) { throw "Native loopback handoff service is missing." }
$loopbackClientPath = Join-Path $root "TheRadioVault.Infrastructure\Services\LoopbackServerClient.cs"
if (-not (Test-Path $loopbackClientPath)) { throw "Shared native loopback server client is missing." }
$loopbackHandoffText = (Get-Content $loopbackHandoffPath -Raw) + (Get-Content $loopbackClientPath -Raw)
foreach ($marker in @("X-RadioVault-Token", "127.0.0.1", "PlayerTransferBegin", "PlayerTransferSourceStopped", "DesktopClient")) {
    if ($loopbackHandoffText -notmatch [regex]::Escape($marker)) { throw "Native loopback handoff marker missing: $marker" }
}
$playbackViewModelText = Get-Content (Join-Path $root "TheRadioVault.Presentation\ViewModels\PlaybackViewModel.cs") -Raw
if ($playbackViewModelText -notmatch 'ShowMoveToThisDevice => _handoff\.IsAvailable && IsPlaybackElsewhere') {
    throw "Native reverse-handoff control is not enabled."
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA2-NATIVE-HANDOFF.md"))) {
    throw "Alpha 2 native handoff acceptance guide is missing."
}
$loopbackReadsPath = Join-Path $root "TheRadioVault.Infrastructure\Services\LoopbackLibraryReadServices.cs"
if (-not (Test-Path $loopbackReadsPath)) { throw "Native loopback Library read adapters are missing." }
$compositionText = Get-Content $hostPath -Raw
foreach ($marker in @("LoopbackServerClient", "LoopbackLibraryBrowseService", "LoopbackBroadcastDetailsService")) {
    if ($compositionText -notmatch [regex]::Escape($marker)) { throw "Native server-owned read marker missing: $marker" }
}
foreach ($forbidden in @("new LibraryBrowseService", "new BroadcastDetailsService")) {
    if ($compositionText -match [regex]::Escape($forbidden)) { throw "Native composition reopened a direct database read boundary: $forbidden" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA3-SERVER-LIBRARY-READS.md"))) {
    throw "Alpha 3 server-owned Library reads acceptance guide is missing."
}
$loopbackWritesPath = Join-Path $root "TheRadioVault.Infrastructure\Services\LoopbackUserStateServices.cs"
if (-not (Test-Path $loopbackWritesPath)) { throw "Native loopback user-state adapters are missing." }
foreach ($marker in @("LoopbackLibraryActionService", "LoopbackPlaybackLibraryService", "LoopbackQueueService", "LoopbackMomentsService")) {
    if ($compositionText -notmatch [regex]::Escape($marker)) { throw "Native server-owned write marker missing: $marker" }
}
foreach ($forbidden in @("new LibraryActionService", "new QueueService", "new MomentsService")) {
    if ($compositionText -match [regex]::Escape($forbidden)) { throw "Native composition reopened a direct database write boundary: $forbidden" }
}
$webRoutesText = Get-Content (Join-Path $root "TheRadioVault.Web\Contracts\WebApiRoutes.cs") -Raw
$webModelsText = Get-Content (Join-Path $root "TheRadioVault.Web\Models\WebModels.cs") -Raw
if ($webRoutesText -notmatch "MomentUpdate" -or $webModelsText -notmatch "IncrementPlayCount") {
    throw "The complete Alpha 4 Moment/progress write contract is missing."
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA4-SERVER-USER-STATE.md"))) {
    throw "Alpha 4 server-owned user-state acceptance guide is missing."
}
$loopbackResearchPath = Join-Path $root "TheRadioVault.Infrastructure\Services\LoopbackResearchServices.cs"
$loopbackTranscriptionPath = Join-Path $root "TheRadioVault.Infrastructure\Services\LoopbackTranscriptionServices.cs"
foreach ($requiredPath in @($loopbackResearchPath, $loopbackTranscriptionPath)) {
    if (-not (Test-Path $requiredPath)) { throw "Alpha 5 server-owned Research/transcript adapter is missing: $requiredPath" }
}
foreach ($marker in @("LoopbackResearchWorkspaceService", "LoopbackResearchPackTransferService", "LoopbackTranscriptRepository", "LoopbackSpeakerIdentityRepository", "LoopbackTranscriptionCoordinator")) {
    if ($compositionText -notmatch [regex]::Escape($marker)) { throw "Alpha 5 server-owned Research/transcript composition marker missing: $marker" }
}
foreach ($forbidden in @("new ResearchWorkspaceService", "new LocalResearchPackTransferService", "new SqliteTranscriptRepository", "new SqliteSpeakerIdentityRepository")) {
    if ($compositionText -match [regex]::Escape($forbidden)) { throw "Native composition reopened a direct Alpha 5 database boundary: $forbidden" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA5-SERVER-RESEARCH-TRANSCRIPTS.md"))) {
    throw "Alpha 5 server-owned Research and transcripts acceptance guide is missing."
}
$serverTranscriptionPath = Join-Path $root "TheRadioVault.Infrastructure\Services\ServerTranscriptionRuntime.cs"
if (-not (Test-Path $serverTranscriptionPath)) { throw "Alpha 6 server transcription runtime is missing." }
$serverTranscriptionText = Get-Content $serverTranscriptionPath -Raw
foreach ($marker in @("TranscriptionCoordinator", "TranscriptionBatchCoordinator", "VoiceLearningCoordinator", "WhisperDownloadService", "NAudioTranscriptionAudioPreparer")) {
    if ($serverTranscriptionText -notmatch [regex]::Escape($marker)) { throw "Alpha 6 server transcription marker missing: $marker" }
}
foreach ($marker in @("LoopbackTranscriptionCoordinator", "LoopbackTranscriptionBatchCoordinator", "LoopbackVoiceLearningCoordinator")) {
    if ($compositionText -notmatch [regex]::Escape($marker)) { throw "Alpha 6 native remote-controller marker missing: $marker" }
}
if ($compositionText -match 'RegisterSingleton<TranscriptionCoordinator>') {
    throw "The native client still owns a transcription worker."
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA6-SERVER-TRANSCRIPTION-WORKERS.md"))) {
    throw "Alpha 6 server transcription acceptance guide is missing."
}
$anywhereWebText = Get-Content (Join-Path $root "TheRadioVault.Web\Services\LocalWebServer.cs") -Raw -Encoding UTF8
foreach ($marker in @('["transcription","Transcription studio"]', '--transcript: #43c7bd', 'loadTranscripts', 'data-transcription-action', 'data-transcript-export', 'data-transcribe-full', 'radio-vault-anywhere-shell-v67')) {
    if ($anywhereWebText -notmatch [regex]::Escape($marker)) { throw "Alpha 7 Anywhere transcription marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA7-ANYWHERE-TRANSCRIPTS.md"))) {
    throw "Alpha 7 Anywhere transcripts acceptance guide is missing."
}
foreach ($marker in @('THE RADIO VAULT', 'data-nav-search', 'data-nav-favourites', 'data-section="moments"', 'data-section="research"', 'data-section="settings"', 'id="sidebarShows"', 'width:224px', 'height:110px', 'id="miniSeek"', 'after(libraryTools)', 'setAttribute("aria-label"')) {
    if ($anywhereWebText -notmatch [regex]::Escape($marker)) { throw "Alpha 8 native-style Anywhere shell marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA8-ANYWHERE-NATIVE-SHELL.md"))) {
    throw "Alpha 8 Anywhere native-shell acceptance guide is missing."
}
foreach ($marker in @('nativeDashboardTop', 'Unheard broadcasts', 'id="miniFavourite"', 'id="miniMoment"', 'id="miniVolume"', 'Waiting for the existing dormant decoder preparation', 'Priming can advance or re-seek the media element')) {
    if ($anywhereWebText -notmatch [regex]::Escape($marker)) { throw "Alpha 8 Buildfix 1 Dashboard, player or handoff marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA8-BUILDFIX1-DASHBOARD-PLAYER-HANDOFF.md"))) {
    throw "Alpha 8 Buildfix 1 acceptance guide is missing."
}
foreach ($marker in @('savedVolumeValue === null ? 1', 'heartbeatInterval = audio.paused ? 5000 : 1000')) {
    if ($anywhereWebText -notmatch [regex]::Escape($marker)) { throw "Alpha 8 Buildfix 2 audible playback or paused ownership marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA8-BUILDFIX2-AUDIBLE-PAUSED-PLAYBACK.md"))) {
    throw "Alpha 8 Buildfix 2 acceptance guide is missing."
}
foreach ($marker in @('loadMoments', 'loadResearch', 'Knowledge database', 'Export complete knowledge database', 'loadSettings', 'data-edit-metadata', 'visibilitychange')) {
    if ($anywhereWebText -notmatch [regex]::Escape($marker)) { throw "Alpha 9 Anywhere parity marker missing: $marker" }
}
if ($anywhereWebText -match '\.sidebarShows,\.navDivider,\.navSoon,\.momentTab,\.researchTab,\.settingsTab') {
    throw "Alpha 9 mobile navigation still hides completed Anywhere workspaces."
}
$brokenEncodings = @(
    (-join @([char]0x00E2, [char]0x20AC, [char]0x00A2)),
    (-join @([char]0x00C2, [char]0x00B7)),
    (-join @([char]0x00E2, [char]0x20AC, [char]0x00A6))
)
foreach ($brokenEncoding in $brokenEncodings) {
    if ($anywhereWebText.Contains($brokenEncoding)) { throw "Alpha 9 Anywhere shell contains broken symbol encoding: $brokenEncoding" }
}
$loopbackClientText = Get-Content $loopbackClientPath -Raw
foreach ($marker in @('MaximumTransientAttempts', 'SendWithReconnectAsync', 'IsRetrySafe', 'GatewayTimeout')) {
    if ($loopbackClientText -notmatch [regex]::Escape($marker)) { throw "Alpha 9 reconnect marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA9-ANYWHERE-FULL-PARITY.md"))) {
    throw "Alpha 9 full-parity acceptance guide is missing."
}
$webModelsContractText = Get-Content (Join-Path $root "TheRadioVault.Web\Models\WebModels.cs") -Raw
if ($webModelsContractText -notmatch 'CapabilityGeneration \{ get; init; \} = 39') {
    throw "Connected server-client capability generation was not advanced to 39."
}
$wikiServicePath = Join-Path $root "TheRadioVault.Services\Services\WikiService.cs"
$wikiPackPath = Join-Path $root "TheRadioVault.Services\Services\WikiAuthoringPackService.cs"
$wikiViewPath = Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\WikiView.axaml"
$wikiLoopbackPath = Join-Path $root "TheRadioVault.Infrastructure\Services\LoopbackWikiServices.cs"
foreach ($requiredPath in @($wikiServicePath, $wikiPackPath, $wikiViewPath, $wikiLoopbackPath)) {
    if (-not (Test-Path $requiredPath)) { throw "0.35 Wiki foundation source missing: $requiredPath" }
}
foreach ($marker in @('LoopbackWikiService', 'LoopbackWikiPackTransferService', 'WikiViewModel')) {
    if ($compositionText -notmatch [regex]::Escape($marker)) { throw "0.35 Wiki composition marker missing: $marker" }
}
foreach ($marker in @('FederationWikiImportPreview', 'FederationWikiImportApply', 'FederationWikiExport', 'ClientWiki')) {
    if ($webRoutesText -notmatch [regex]::Escape($marker)) { throw "0.35 Wiki server route missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.35.0-ALPHA1-WIKI-FOUNDATION.md"))) {
    throw "0.35 Alpha 1 Wiki acceptance guide is missing."
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.35.0-ALPHA1-BUILDFIX1-RESEARCH-PACK-TOLERANCE.md"))) {
    throw "0.35 Alpha 1 Buildfix 1 Research-pack acceptance guide is missing."
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.35.0-ALPHA2-WIKI-AUTHORING.md"))) {
    throw "0.35 Alpha 2 Wiki authoring acceptance guide is missing."
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.35.0-ALPHA3-WIKI-DASHBOARD.md"))) {
    throw "0.35 Alpha 3 Wiki dashboard acceptance guide is missing."
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.35.0-ALPHA4-WIKI-REFINEMENT.md"))) {
    throw "0.35 Alpha 4 Wiki refinement acceptance guide is missing."
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.35.0-ALPHA5-CANONICAL-TOPICS.md"))) {
    throw "0.35 Alpha 5 canonical-topic acceptance guide is missing."
}
$wikiViewText = Get-Content $wikiViewPath -Raw
$wikiServiceText = Get-Content $wikiServicePath -Raw
foreach ($marker in @('canonical_topics', 'canonical_topic_aliases', 'topic_merge_history', 'RunAutomaticTopicCleanupAsync', 'MergeTopicsAsync', 'Canonical topic cleanup')) {
    if (($wikiServiceText + $wikiViewText + (Get-Content (Join-Path $root "TheRadioVault.Data\Database\SqliteDatabase.cs") -Raw)) -notmatch [regex]::Escape($marker)) {
        throw "0.35 Alpha 5 canonical-topic marker missing: $marker"
    }
}
$wikiViewText = Get-Content $wikiViewPath -Raw
$wikiServiceText = Get-Content $wikiServicePath -Raw
foreach ($marker in @('Build starter pages', 'ShowReadingModeCommand', 'CitationSourceTitle', 'ArchiveBroadcasts', 'ImportChanges')) {
    if (($wikiViewText + (Get-Content (Join-Path $root "TheRadioVault.Presentation\ViewModels\WikiViewModel.cs") -Raw)) -notmatch [regex]::Escape($marker)) {
        throw "0.35 Alpha 2 Wiki authoring UI marker missing: $marker"
    }
}
foreach ($marker in @('Explore the stories behind the archive', 'Featured starting points', 'Recently updated', 'Travel through the timelines', 'ShowDashboardCommand', 'ManageWikiCommand', 'LoadDashboardCollectionsAsync')) {
    if (($wikiViewText + (Get-Content (Join-Path $root "TheRadioVault.Presentation\ViewModels\WikiViewModel.cs") -Raw)) -notmatch [regex]::Escape($marker)) {
        throw "0.35 Alpha 3 Wiki dashboard marker missing: $marker"
    }
}
foreach ($marker in @('WikiMarkdownView', 'ReaderImages', 'RestoreRevisionCommand', 'AuditCitationsCommand', 'PlayTimelineLinkCommand', 'OpenEntityCommand')) {
    if (($wikiViewText + (Get-Content (Join-Path $root "TheRadioVault.Presentation\ViewModels\WikiViewModel.cs") -Raw) + (Get-Content (Join-Path $root "TheRadioVault.Presentation\ViewModels\RelatedWikiPagesViewModel.cs") -Raw) + (Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Controls\WikiMarkdownView.axaml.cs") -Raw)) -notmatch [regex]::Escape($marker)) {
        throw "0.35 Alpha 3 Wiki reader marker missing: $marker"
    }
}
foreach ($marker in @('data-section="wiki"', 'async function loadWiki', 'function renderWikiMarkdown', 'async function openWikiEntity', '[data-wiki-entity], .chip')) {
    if ($anywhereWebText -notmatch [regex]::Escape($marker)) { throw "0.35 Alpha 3 Web Wiki marker missing: $marker" }
}
foreach ($marker in @('BackCommand', 'ForwardCommand', 'IsArticleMode', 'Timeline Explorer', 'AuditQualityCommand', 'HasBacklinks', 'GetNavigationContextAsync', 'GetTimelineShowsAsync', 'AuditQualityAsync')) {
    if (($wikiViewText + (Get-Content (Join-Path $root "TheRadioVault.Presentation\ViewModels\WikiViewModel.cs") -Raw) + $wikiServiceText) -notmatch [regex]::Escape($marker)) {
        throw "0.35 Alpha 4 native Wiki refinement marker missing: $marker"
    }
}
foreach ($marker in @('function wikiNavBar', 'showWikiTimelineExplorer', 'wikiNavigation', 'INTERACTIVE HISTORY', 'dashboard-highlights', 'timeline-shows')) {
    if ($anywhereWebText -notmatch [regex]::Escape($marker)) { throw "0.35 Alpha 4 Web Wiki refinement marker missing: $marker" }
}
foreach ($marker in @('PreviewStarterPagesAsync', 'GenerateStarterPagesAsync', 'BrowseArchiveLinksAsync', 'ReadArchiveContextAsync', 'WikiPackPageChangePreview')) {
    if (($wikiServiceText + (Get-Content (Join-Path $root "TheRadioVault.Services\Models\WikiModels.cs") -Raw)) -notmatch [regex]::Escape($marker)) {
        throw "0.35 Alpha 2 Wiki service marker missing: $marker"
    }
}
$researchPackModelsText = Get-Content (Join-Path $root "TheRadioVault.Web\Models\WebRemoteAdministrationModels.cs") -Raw
$researchPackClientText = Get-Content (Join-Path $root "TheRadioVault.Infrastructure\Services\LoopbackResearchServices.cs") -Raw
$researchWorkspaceViewModelText = Get-Content (Join-Path $root "TheRadioVault.Presentation\ViewModels\ResearchWorkspaceViewModel.cs") -Raw
$researchWorkspaceViewText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\ResearchWorkspaceView.axaml") -Raw
foreach ($marker in @('MaximumPackageBytes = 512 * 1024 * 1024', 'WebResearchPackLimits.MaximumPackageBytes')) {
    if (($researchPackModelsText + $researchPackClientText) -notmatch [regex]::Escape($marker)) { throw "RC1 Buildfix 1 Research pack size marker missing: $marker" }
}
foreach ($marker in @('ImportErrorText', 'RetryImportCommand', 'RaiseImportFeedbackState')) {
    if ($researchWorkspaceViewModelText -notmatch [regex]::Escape($marker)) { throw "RC1 Buildfix 1 Research import state marker missing: $marker" }
}
foreach ($marker in @('This Knowledge import could not finish', 'No partial changes were kept', 'RetryImportCommand')) {
    if ($researchWorkspaceViewText -notmatch [regex]::Escape($marker)) { throw "RC1 Buildfix 1 Research import UI marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-RC1-BUILDFIX1-RESEARCH-PACK-IMPORT.md"))) {
    throw "RC1 Buildfix 1 Research pack acceptance guide is missing."
}
$researchLibraryText = Get-Content (Join-Path $root "TheRadioVault.Infrastructure\Services\DatabaseService.ResearchLibrary.cs") -Raw
$researchBrowserText = Get-Content (Join-Path $root "TheRadioVault.Infrastructure\Services\DatabaseService.ResearchBrowser.cs") -Raw
foreach ($text in @($researchLibraryText, $researchBrowserText)) {
    if ($text -match "research_state=CASE[\s\S]{0,800}THEN 'ambiguous'") {
        throw "Knowledge import still writes the reconciliation-only ambiguous value into research_state."
    }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.35.0-ALPHA9-BUILDFIX1-KNOWLEDGE-IMPORT.md"))) {
    throw "0.35 Alpha 9 Buildfix 1 Knowledge import acceptance guide is missing."
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.35.0-ALPHA9-BUILDFIX2-ARCHIVE-WIDE-KNOWLEDGE-EXPORT.md"))) {
    throw "0.35 Alpha 9 Buildfix 2 archive-wide Knowledge export acceptance guide is missing."
}
foreach ($marker in @('if (!id || isIosWebKit || thisPhoneOwnsSession() || phoneTransferInProgress) return;', 'if (!isIosWebKit && shared?.episodeId && !phoneTransferInProgress)', 'const preparingDormantTarget = !isIosWebKit && inactive && has &&', 'radio-vault-anywhere-shell-v67')) {
    if ($anywhereWebText -notmatch [regex]::Escape($marker)) { throw "Alpha 20 Buildfix 1 repeated-iPhone-handoff marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA20-BUILDFIX1-IPHONE-REPEATED-HANDOFF.md"))) {
    throw "Alpha 20 Buildfix 1 acceptance guide is missing."
}
foreach ($marker in @('GetOrCreatePositionedWaveSession', 'PositionedWaveSessionIdleLifetime', 'WriteRangeAsync', 'streamSession', 'radio-vault-anywhere-shell-v67')) {
    if ($anywhereWebText -notmatch [regex]::Escape($marker)) { throw "Alpha 20 Buildfix 2 iPhone range-continuity marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA20-BUILDFIX2-IPHONE-RANGE-CONTINUITY.md"))) {
    throw "Alpha 20 Buildfix 2 acceptance guide is missing."
}
$nativePlaybackEngineText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Playback\NAudioPlaybackEngine.cs") -Raw
foreach ($marker in @('VolumeSampleProvider', '_volumeProvider.Volume = (float)_volume', 'output.Init(volumeProvider.ToWaveProvider())')) {
    if ($nativePlaybackEngineText -notmatch [regex]::Escape($marker)) { throw "Alpha 20 Buildfix 3 device-local volume marker missing: $marker" }
}
if ($nativePlaybackEngineText.Contains('if (_output is not null) _output.Volume') -or
    $nativePlaybackEngineText.Contains('output.Volume = (float)_volume')) {
    throw "Native playback still overwrites the Windows audio-session volume."
}
$macPlaybackEnginePath = Join-Path $root "TheRadioVault.Desktop.Avalonia\Playback\MacAvFoundationPlaybackEngine.cs"
if (-not (Test-Path $macPlaybackEnginePath)) { throw "The native macOS playback engine is missing." }
$macPlaybackEngineText = Get-Content $macPlaybackEnginePath -Raw
foreach ($marker in @('OperatingSystem.IsMacOS()', 'AVFoundation.framework/AVFoundation', 'CMTimeMakeWithSeconds', 'PlaybackStatus.Buffering', 'MediaEnded?.Invoke')) {
    if ($macPlaybackEngineText -notmatch [regex]::Escape($marker)) { throw "Mac Client playback marker missing: $marker" }
}
$macInfoPlistPath = Join-Path $root "installer\macos\Info.plist"
$macPackagingPath = Join-Path $root "package-macos-client.ps1"
foreach ($path in @($macInfoPlistPath, $macPackagingPath, (Join-Path $root "installer\macos\finalize-macos-client.sh"), (Join-Path $root "installer\macos\RadioVault.entitlements"))) {
    if (-not (Test-Path $path)) { throw "Mac Client packaging file is missing: $path" }
}
$macInfoPlistText = Get-Content $macInfoPlistPath -Raw
foreach ($marker in @('com.theradiovault.client', 'NSLocalNetworkUsageDescription', 'NSAllowsLocalNetworking', 'LSMinimumSystemVersion')) {
    if ($macInfoPlistText -notmatch [regex]::Escape($marker)) { throw "Mac Client Info.plist marker missing: $marker" }
}
$macPackagingText = Get-Content $macPackagingPath -Raw
foreach ($marker in @('osx-arm64', '--self-contained true', 'Radio Vault.app', 'Write-Icns')) {
    if ($macPackagingText -notmatch [regex]::Escape($marker)) { throw "Mac Client packaging marker missing: $marker" }
}
$nativeCompositionText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Composition\AvaloniaApplicationHost.cs") -Raw
foreach ($marker in @('CreatePlaybackEngine()', 'new MacAvFoundationPlaybackEngine()', 'new ServerMediaProxy')) {
    if ($nativeCompositionText -notmatch [regex]::Escape($marker)) { throw "Mac Client composition marker missing: $marker" }
}
$linuxPlaybackPath = Join-Path $root "TheRadioVault.Desktop.Avalonia\Playback\LinuxMpvPlaybackEngine.cs"
if (-not (Test-Path $linuxPlaybackPath)) { throw "The native Linux playback engine is missing." }
$linuxPlaybackText = Get-Content $linuxPlaybackPath -Raw
foreach ($marker in @('OperatingSystem.IsLinux()', 'UnixDomainSocketEndPoint', '--input-ipc-server=', 'RADIOVAULT_MPV_PATH', 'PlaybackStatus.Ended')) {
    if ($linuxPlaybackText -notmatch [regex]::Escape($marker)) { throw "Linux Client playback marker missing: $marker" }
}
foreach ($marker in @('OperatingSystem.IsLinux()', 'new LinuxMpvPlaybackEngine()')) {
    if ($nativeCompositionText -notmatch [regex]::Escape($marker)) { throw "Linux Client composition marker missing: $marker" }
}
$macServerPackagingPath = Join-Path $root "package-macos-server.ps1"
$linuxPackagingPath = Join-Path $root "package-linux.sh"
foreach ($path in @($macServerPackagingPath, $linuxPackagingPath, (Join-Path $root "installer\macos\ServerInfo.plist"), (Join-Path $root "installer\macos\finalize-macos-server.sh"), (Join-Path $root "LINUX.md"))) {
    if (-not (Test-Path $path)) { throw "Mac/Linux Client-Server packaging file is missing: $path" }
}
$macServerPackagingText = Get-Content $macServerPackagingPath -Raw
foreach ($marker in @('TheRadioVault.Server\TheRadioVault.Server.csproj', 'Radio Vault Server.app', '--self-contained true', 'com.theradiovault.server')) {
    if ($macServerPackagingText -notmatch [regex]::Escape($marker)) { throw "Mac Server packaging marker missing: $marker" }
}
$linuxPackagingText = Get-Content $linuxPackagingPath -Raw
foreach ($marker in @('linux-x64', '--self-contained true', 'RadioVault.Client-$VERSION-$RID.tar.gz', 'RadioVault.Server-$VERSION-$RID.tar.gz')) {
    if ($linuxPackagingText -notmatch [regex]::Escape($marker)) { throw "Linux Client-Server packaging marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA20-BUILDFIX3-DEVICE-LOCAL-VOLUME.md"))) {
    throw "Alpha 20 Buildfix 3 device-local volume acceptance guide is missing."
}
$nativeAccessRecoveryText = Get-Content (Join-Path $root "TheRadioVault.Infrastructure\Services\NativeConnectedAccessService.cs") -Raw
foreach ($marker in @('var recovered = !_current.IsLive;', 'MarkServerLive(invalidateMemoryCache: recovered)')) {
    if ($nativeAccessRecoveryText -notmatch [regex]::Escape($marker)) { throw "RC1 connection-recovery marker missing: $marker" }
}
foreach ($marker in @('var startListener = _listener!;', 'AcceptLoopAsync(startListener', 'startCancellation.Token')) {
    if ($anywhereWebText -notmatch [regex]::Escape($marker)) { throw "RC1 restart-generation marker missing: $marker" }
}
if ($anywhereWebText.Contains('AcceptLoopAsync(_listener!')) {
    throw "RC1 server accept loop still references a mutable listener generation."
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-RC1-STABILITY.md"))) {
    throw "RC1 stability acceptance guide is missing."
}
$serverProgramText = Get-Content (Join-Path $root "TheRadioVault.Server\Program.cs") -Raw
$serverAppText = Get-Content (Join-Path $root "TheRadioVault.Server\App.axaml.cs") -Raw
$serverSettingsText = Get-Content (Join-Path $root "TheRadioVault.Server\ViewModels\ServerSettingsViewModel.cs") -Raw
$startupRegistrationText = Get-Content (Join-Path $root "TheRadioVault.Server\Services\WindowsStartupRegistrationService.cs") -Raw
$platformStartupRegistrationText = Get-Content (Join-Path $root "TheRadioVault.Server\Services\ServerStartupRegistrationService.cs") -Raw
$nativeConnectionText = Get-Content (Join-Path $root "TheRadioVault.Infrastructure\Services\NativeConnectedAccessService.cs") -Raw
$nativeSettingsText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\DesktopToolsView.axaml") -Raw
foreach ($marker in @('ServerInstanceCoordinator.Acquire', 'SignalPrimaryAsync')) {
    if ($serverProgramText -notmatch [regex]::Escape($marker)) { throw "Alpha 10 single-instance server marker missing: $marker" }
}
foreach ($marker in @('ShutdownMode.OnExplicitShutdown', 'TrayIcon', '--background', 'ShowSettings')) {
    if ($serverAppText -notmatch [regex]::Escape($marker)) { throw "Alpha 10 background server marker missing: $marker" }
}
foreach ($marker in @('GeneratePairingCodeCommand', 'RevokeAllClientsCommand', 'LanFederationEnabled')) {
    if ($serverSettingsText -notmatch [regex]::Escape($marker)) { throw "Alpha 10 server pairing UI marker missing: $marker" }
}
foreach ($marker in @('CurrentVersion\Run', '--background', 'Registry.CurrentUser')) {
    if ($startupRegistrationText -notmatch [regex]::Escape($marker)) { throw "Alpha 10 Windows startup marker missing: $marker" }
}
foreach ($marker in @('WindowsStartupRegistrationService', 'LaunchAgents', '.config', 'autostart', 'com.theradiovault.server', 'Start with macOS', 'Start with Linux')) {
    if ($platformStartupRegistrationText -notmatch [regex]::Escape($marker)) { throw "Cross-platform Server startup marker missing: $marker" }
}
foreach ($marker in @('DiscoverAsync', 'PairAsync', 'ServerCertificateCustomValidationCallback', 'FederationBootstrap')) {
    if ($nativeConnectionText -notmatch [regex]::Escape($marker)) { throw "Alpha 10 native pairing marker missing: $marker" }
}
foreach ($marker in @('Server connection', 'ConnectedAccess.DiscoverCommand', 'ConnectedAccess.PairCommand', 'ConnectedAccess.TestCommand')) {
    if ($nativeSettingsText -notmatch [regex]::Escape($marker)) { throw "Alpha 10 native connection settings marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA10-BACKGROUND-SERVER-PAIRING.md"))) {
    throw "Alpha 10 background-server pairing acceptance guide is missing."
}

$parserPath = Join-Path $root "TheRadioVault.Core\Services\FilenameParserService.cs"
$parserText = Get-Content $parserPath -Raw
foreach ($marker in @("ExtractCatalogueHeadline", "RbiCollectionRegex", "CatalogueCopySuffixRegex", "AssignedCollectionName")) {
    if ($parserText -notmatch [regex]::Escape($marker)) { throw "Catalogue parser marker missing: $marker" }
}
foreach ($marker in @("countsByShow", "CollectionIdentityResolver.Canonicalize", "CollectionIdentityResolver.Matches")) {
    if ($browseText -notmatch [regex]::Escape($marker)) { throw "Canonical show projection marker missing: $marker" }
}

$identityPath = Join-Path $root "TheRadioVault.Services\Services\CollectionIdentityResolver.cs"
if (-not (Test-Path $identityPath)) { throw "Canonical collection-family resolver is missing." }
$identityText = Get-Content $identityPath -Raw
foreach ($marker in @("ResolveFamily", "AddIdPredicate", "KnownShowCatalog.Normalize")) {
    if ($identityText -notmatch [regex]::Escape($marker)) { throw "Collection-family resolver marker missing: $marker" }
}
$researchWorkspacePath = Join-Path $root "TheRadioVault.Services\Services\ResearchWorkspaceService.cs"
$researchWorkspaceText = Get-Content $researchWorkspacePath -Raw
foreach ($marker in @("CollectionIdentityResolver.LoadFamilies", "browseCollection", "undatedCollection", "coverageResearch")) {
    if ($researchWorkspaceText -notmatch [regex]::Escape($marker)) { throw "Research show projection marker missing: $marker" }
}
$mainWindowPath = Join-Path $root "TheRadioVault.Presentation\ViewModels\MainWindowViewModel.cs"
$mainWindowText = Get-Content $mainWindowPath -Raw
if ($mainWindowText -notmatch "LibraryScanCompleted") {
    throw "Sidebar navigation is not refreshed after a completed Library scan."
}

$libraryViewModelPath = Join-Path $root "TheRadioVault.Presentation\ViewModels\LibraryViewModel.cs"
$libraryViewModelText = Get-Content $libraryViewModelPath -Raw
foreach ($marker in @("IsCatalogueCollection", "!IsCatalogueCollection", "no broadcast date")) {
    if ($libraryViewModelText -notmatch [regex]::Escape($marker)) { throw "Catalogue Library view marker missing: $marker" }
}
$scannerPath = Join-Path $root "TheRadioVault.Infrastructure\Services\LibraryScannerService.cs"
$scannerText = Get-Content $scannerPath -Raw
if ($scannerText -notmatch "AssignedCollectionName = folder.CollectionName") {
    throw "Explicit folder assignment is not reaching the filename parser."
}
$exportPath = Join-Path $root "TheRadioVault.Desktop.Avalonia\Research\AvaloniaResearchPackTransferServices.cs"
$exportText = Get-Content $exportPath -Raw
if ($exportText -notmatch "AppendUnresearchedLibraryEpisodesAsync") {
    throw "Whole-show Research export does not include unresearched Library episodes."
}

$anywhereServicePath = Join-Path $root "TheRadioVault.Desktop.Avalonia\Anywhere\DedicatedServerRadioVaultAnywhereService.cs"
$anywhereViewPath = Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\DesktopToolsView.axaml"
if (-not (Test-Path $anywhereServicePath)) { throw "Avalonia Radio Vault Anywhere service is missing." }
$anywhereServiceText = Get-Content $anywhereServicePath -Raw
foreach ($marker in @("LoopbackServerClient", "never starts a second", "Radio Vault Server owns hosting settings")) {
    if ($anywhereServiceText -notmatch [regex]::Escape($marker)) { throw "Dedicated-server Anywhere boundary missing: $marker" }
}
$serverAdapterPath = Join-Path $root "TheRadioVault.Infrastructure\Services\LocalWebServerService.cs"
$serverAdapterText = Get-Content $serverAdapterPath -Raw
foreach ($marker in @("preferences.LanFederationEnabled", "Array.Empty<WebPairedDesktopClient>()")) {
    if ($serverAdapterText -notmatch [regex]::Escape($marker)) { throw "Native desktop credential gate missing: $marker" }
}
$anywhereViewText = Get-Content $anywhereViewPath -Raw
foreach ($marker in @("Radio Vault Web", "IsAnywhereSection", "Open phone HTTPS setup", "hosted by the server")) {
    if ($anywhereViewText -notmatch [regex]::Escape($marker)) { throw "Anywhere Settings UI marker missing: $marker" }
}

foreach ($marker in @('IConnectedAccessService', 'NativeConnectedAccessService')) {
    if ($hostText -notmatch [regex]::Escape($marker)) { throw "Alpha 10 native connection composition marker missing: $marker" }
}
$mainWindowViewPath = Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\MainWindow.axaml"
$mainWindowViewText = Get-Content $mainWindowViewPath -Raw
$desktopThemePath = Join-Path $root "TheRadioVault.Desktop.Avalonia\App.axaml"
$desktopThemeText = Get-Content $desktopThemePath -Raw
if ($mainWindowViewText -notmatch "PrimaryTransportIconTemplate" -or
    $desktopThemeText -notmatch "ShowMoveToThisDevice") {
    throw "The native centre transport is missing its move-to-this-device state."
}
$dashboardViewPath = Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\DashboardView.axaml"
$dashboardViewText = Get-Content $dashboardViewPath -Raw
if ($dashboardViewText -match "FeaturedContinue.ToggleFavouriteCommand") {
    throw "Dashboard featured continuation still exposes a persistent favourite action."
}
$researchVmPath = Join-Path $root "TheRadioVault.Presentation\ViewModels\ResearchWorkspaceViewModel.cs"
$researchVmText = Get-Content $researchVmPath -Raw
if ($researchVmText -match "authoritative.server") {
    throw "Local Research UI still exposes authoritative-server wording."
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/ALPHA13-LOCAL-UX-PASS1-ACCEPTANCE.md"))) {
    throw "Alpha 13 local UX acceptance guide is missing."
}

$packModelsPath = Join-Path $root "TheRadioVault.Infrastructure\Models\Models.cs"
$packModelsText = Get-Content $packModelsPath -Raw
foreach ($marker in @("TrvPackCatalogueMetadata", "OriginalReleaseDate", "OriginalFilename", "Provenance", "ResearchNotes")) {
    if ($packModelsText -notmatch [regex]::Escape($marker)) { throw "Catalogue research-pack field missing: $marker" }
}
foreach ($marker in @('research["catalogue"]', 'catalogue["original_filename"]', 'catalogue["research_notes"]')) {
    if ($researchWorkspaceText -notmatch [regex]::Escape($marker)) { throw "Catalogue Research workspace marker missing: $marker" }
}
$catalogueResearchViewPath = Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\ResearchWorkspaceView.axaml"
$catalogueResearchViewText = Get-Content $catalogueResearchViewPath -Raw
foreach ($marker in @("EditorCatalogueProgramme", "EditorOriginalReleaseDate", "EditorProvenance", "EditorResearchNotes")) {
    if ($catalogueResearchViewText -notmatch [regex]::Escape($marker)) { throw "Catalogue Research editor binding missing: $marker" }
}
$webModelsPath = Join-Path $root "TheRadioVault.Web\Models\WebModels.cs"
$webModelsText = Get-Content $webModelsPath -Raw
if ($webModelsText -notmatch "CatalogueFields") { throw "Radio Vault Anywhere catalogue display model is missing." }
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/ALPHA13-CATALOGUE-RESEARCH-ACCEPTANCE.md"))) {
    throw "Catalogue Research acceptance guide is missing."
}


$playbackVmPath = Join-Path $root "TheRadioVault.Presentation\ViewModels\PlaybackViewModel.cs"
$playbackVmText = Get-Content $playbackVmPath -Raw
foreach ($marker in @("LastResolveDurationMs", "LastDecoderOpenDurationMs", "open-local-decoder", "Opening local audio")) {
    if ($playbackVmText -notmatch [regex]::Escape($marker)) { throw "Alpha 13 Pass 2 playback marker missing: $marker" }
}
$enginePath = Join-Path $root "TheRadioVault.Desktop.Avalonia\Playback\NAudioPlaybackEngine.cs"
$engineText = Get-Content $enginePath -Raw
foreach ($marker in @("created lazily by Play", "_output is null || _status != PlaybackStatus.Playing", "PlaybackStatus.Buffering")) {
    if ($engineText -notmatch [regex]::Escape($marker)) { throw "Lazy local-audio pipeline marker missing: $marker" }
}
$canonicalPath = Join-Path $root "TheRadioVault.Services\Services\CanonicalLibraryQueryService.cs"
$canonicalText = Get-Content $canonicalPath -Raw
if ($canonicalText -notmatch "GetLatestVerifiedTruthRunId") {
    throw "Lightweight canonical playback lookup is missing."
}
$nowPlayingViewPath = Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\NowPlayingView.axaml"
$nowPlayingViewText = Get-Content $nowPlayingViewPath -Raw
foreach ($marker in @("Playback.IsPrimaryTransportLoading", "Preparing playback", "Play first queued broadcast")) {
    if ($nowPlayingViewText -notmatch [regex]::Escape($marker)) { throw "Now Playing Pass 2 marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/ALPHA13-PLAYBACK-NOW-PLAYING-PASS2-ACCEPTANCE.md"))) {
    throw "Alpha 13 Pass 2 acceptance guide is missing."
}

$transcriptionHostPath = Join-Path $root "TheRadioVault.Desktop.Avalonia\Composition\AvaloniaApplicationHost.cs"
$transcriptionVmPath = Join-Path $root "TheRadioVault.Presentation\ViewModels\TranscriptsViewModel.cs"
$transcriptionViewPath = Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\TranscriptsView.axaml"
foreach ($path in @($transcriptionVmPath, $transcriptionViewPath)) {
    if (-not (Test-Path $path)) { throw "Alpha 2 transcription workspace file is missing: $path" }
}
$transcriptionHostText = Get-Content $transcriptionHostPath -Raw
foreach ($marker in @("ITranscriptionCoordinator", "IServerTranscriptionAdministrationService", "LoopbackTranscriptionCoordinator", "TranscriptsViewModel")) {
    if ($transcriptionHostText -notmatch [regex]::Escape($marker)) { throw "Alpha 2 transcription composition marker missing: $marker" }
}
$transcriptionViewText = Get-Content $transcriptionViewPath -Raw
foreach ($marker in @("CurrentTranscriptionActionText", "Five-minute sample", "PlaySelectedSegmentCommand", "RetrySelectedJobCommand", "CancelSelectedJobCommand")) {
    if ($transcriptionViewText -notmatch [regex]::Escape($marker)) { throw "Alpha 2 transcription workflow marker missing: $marker" }
}
$transcriptionSettingsViewText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\DesktopToolsView.axaml") -Raw
foreach ($marker in @("Server transcription", "Install on server", "Advanced server transcription settings")) {
    if ($transcriptionSettingsViewText -notmatch [regex]::Escape($marker)) { throw "Alpha 2 automatic transcription setup marker missing: $marker" }
}
$diarizationEngineText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Diarization\SherpaOnnxMultiSpeakerDiarizationEngine.cs") -Raw
foreach ($marker in @("OfflineSpeakerDiarization", "Clustering.Threshold", "SpeakerDiarizationTurn")) {
    if ($diarizationEngineText -notmatch [regex]::Escape($marker)) { throw "Alpha 3 multi-speaker engine marker missing: $marker" }
}
foreach ($marker in @("Identify and separate multiple speakers automatically", "TranscriptionMultiSpeakerDiarization")) {
    if ($transcriptionSettingsViewText -notmatch [regex]::Escape($marker)) { throw "Alpha 3 multi-speaker setup marker missing: $marker" }
}
$transcriptionDownloadPath = Join-Path $root "TheRadioVault.Transcription\Services\WhisperDownloadService.cs"
$transcriptionDownloadText = Get-Content $transcriptionDownloadPath -Raw
foreach ($marker in @("releases/latest", "whisper-bin-x64.zip", "digest", "whisper-vad", "ExtractArchiveSafely")) {
    if ($transcriptionDownloadText -notmatch [regex]::Escape($marker)) { throw "Alpha 2 safe transcription download marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.33.0-ALPHA2-LOCAL-TRANSCRIPTION.md"))) {
    throw "Alpha 2 local transcription acceptance guide is missing."
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.33.0-ALPHA2-BUILDFIX1-TRANSCRIPTION-MODEL-REPAIR.md"))) {
    throw "Alpha 2 Buildfix 1 transcription-model acceptance guide is missing."
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.33.0-ALPHA3-MULTI-SPEAKER-DIARIZATION.md"))) {
    throw "Alpha 3 multi-speaker acceptance guide is missing."
}
$startupViewText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\StartupWindow.axaml") -Raw
foreach ($marker in @("STARTING RADIO VAULT", "RvShellBrush", "RadioVault-Logo.png")) {
    if ($startupViewText -notmatch [regex]::Escape($marker)) { throw "Alpha 4 launch UI marker missing: $marker" }
}
$audioPreparerText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Transcription\NAudioTranscriptionAudioPreparer.cs") -Raw
foreach ($marker in @(".m4a", "prepared-audio.wav", "WdlResamplingSampleProvider")) {
    if ($audioPreparerText -notmatch [regex]::Escape($marker)) { throw "Alpha 4 audio preparation marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.33.0-ALPHA4-LAUNCH-TRANSCRIPTS-UI.md"))) {
    throw "Alpha 4 launch and Transcripts UI acceptance guide is missing."
}
$packServiceText = Get-Content (Join-Path $root "TheRadioVault.Infrastructure\Services\KnowledgePackService.cs") -Raw
$searchServiceText = Get-Content (Join-Path $root "TheRadioVault.Services\Services\LibraryBrowseService.cs") -Raw
$libraryViewText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\LibraryView.axaml") -Raw
$processControllerText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Transcription\WindowsTranscriptionProcessController.cs") -Raw
foreach ($marker in @("CREATE TABLE transcripts", "TranscriptCount")) {
    if ($packServiceText -notmatch [regex]::Escape($marker)) { throw "Alpha 5 Research Pack transcript marker missing: $marker" }
}
foreach ($marker in @("LoadExtendedSearchMatches", "SearchContext", "transcripts")) {
    if ($searchServiceText -notmatch [regex]::Escape($marker)) { throw "Alpha 5 Search marker missing: $marker" }
}
if ($libraryViewText -notmatch [regex]::Escape("Transcribe broadcast")) { throw "Alpha 5 broadcast transcription menu is missing." }
foreach ($marker in @("PauseSelectedJobCommand", "ResumeSelectedJobCommand")) {
    if ($transcriptionVmPath -and ((Get-Content $transcriptionVmPath -Raw) -notmatch [regex]::Escape($marker))) { throw "Alpha 5 pause workflow marker missing: $marker" }
}
foreach ($marker in @("SuspendThread", "ResumeThread")) {
    if ($processControllerText -notmatch [regex]::Escape($marker)) { throw "Alpha 5 native pause marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.33.0-ALPHA5-TRANSCRIPT-WORKFLOW.md"))) {
    throw "Alpha 5 transcript workflow acceptance guide is missing."
}
$broadcastInfoText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\FullBroadcastInfoView.axaml") -Raw
$nowPlayingTranscriptText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\NowPlayingView.axaml") -Raw
$searchViewText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\SearchView.axaml") -Raw
foreach ($marker in @("Read transcript", "Start transcription")) {
    if ($broadcastInfoText -notmatch [regex]::Escape($marker)) { throw "Alpha 6 Broadcast Information transcript marker missing: $marker" }
}
foreach ($marker in @("OpenTranscriptCommand", "StartTranscriptionCommand")) {
    if ($nowPlayingTranscriptText -notmatch [regex]::Escape($marker)) { throw "Alpha 6 Now Playing transcript marker missing: $marker" }
}
foreach ($marker in @("Narrow the results", "ScopeFilters", "StatusFilters", "ShowFilters", "YearFilters", "Suggestions", "HasTranscriptOnly")) {
    if ($searchViewText -notmatch [regex]::Escape($marker)) { throw "Alpha 6 faceted Search marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.33.0-ALPHA6-TRANSCRIPT-ACCESS-FACETED-SEARCH.md"))) {
    throw "Alpha 6 transcript access and faceted Search acceptance guide is missing."
}
$transcriptReviewText = Get-Content (Join-Path $root "TheRadioVault.Presentation\ViewModels\TranscriptsViewModel.cs") -Raw
$transcriptReviewViewText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\TranscriptsView.axaml") -Raw
$voiceEngineText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Diarization\SherpaOnnxVoiceEmbeddingEngine.cs") -Raw
foreach ($marker in @("SaveSelectedPhraseAsync", "SplitSelectedPhraseAsync", "MergeSelectedPhraseAsync", "SuggestRememberedVoicesAsync", "ExportTranscriptAsync")) {
    if ($transcriptReviewText -notmatch [regex]::Escape($marker)) { throw "Alpha 7 transcript review marker missing: $marker" }
}
foreach ($marker in @("Save wording", "Confirm voice", "Split phrase", "Merge next", "ExportSrtCommand", "ExportVttCommand")) {
    if ($transcriptReviewViewText -notmatch [regex]::Escape($marker)) { throw "Alpha 7 transcript review UI marker missing: $marker" }
}
foreach ($marker in @("SpeakerEmbeddingExtractor", "CreateEmbeddingAsync")) {
    if ($voiceEngineText -notmatch [regex]::Escape($marker)) { throw "Alpha 7 remembered voice marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.33.0-ALPHA7-TRANSCRIPT-REVIEW.md"))) {
    throw "Alpha 7 transcript review acceptance guide is missing."
}
$batchCoordinatorText = Get-Content (Join-Path $root "TheRadioVault.Transcription\Services\TranscriptionBatchCoordinator.cs") -Raw
$batchRepositoryText = Get-Content (Join-Path $root "TheRadioVault.Transcription\Services\SqliteTranscriptionBatchRepository.cs") -Raw
foreach ($marker in @("CreateAndStartAsync", "PauseAsync", "ResumeAsync", "RetryFailedAsync", "MoveItemAsync")) {
    if ($batchCoordinatorText -notmatch [regex]::Escape($marker)) { throw "Alpha 8 batch coordinator marker missing: $marker" }
}
foreach ($marker in @("transcription_batches", "transcription_batch_items", "MarkAbandonedBatchesInterrupted")) {
    if ($batchRepositoryText -notmatch [regex]::Escape($marker)) { throw "Alpha 8 durable batch marker missing: $marker" }
}
foreach ($marker in @("Start batch", "Pause batch", "Resume batch", "Retry failed", "MoveBatchItemUpCommand")) {
if ($transcriptReviewViewText -notmatch [regex]::Escape($marker)) { throw "Alpha 8 batch UI marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.33.0-ALPHA8-BATCH-TRANSCRIPTION.md"))) {
    throw "Alpha 8 batch transcription acceptance guide is missing."
}

$researchViewText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\ResearchWorkspaceView.axaml") -Raw
$researchTransferText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Research\AvaloniaResearchPackTransferServices.cs") -Raw
$researchViewModelText = Get-Content (Join-Path $root "TheRadioVault.Presentation\ViewModels\ResearchWorkspaceViewModel.cs") -Raw
$knowledgePackText = Get-Content (Join-Path $root "TheRadioVault.Infrastructure\Services\KnowledgePackService.cs") -Raw
foreach ($marker in @('Export full Knowledge Database', 'Archive Knowledge Database ready')) {
    if ($researchViewText -notmatch [regex]::Escape($marker)) { throw "Buildfix 1 Deep Research Pack UI marker missing: $marker" }
}
foreach ($marker in @('pack.Transcripts.Count', 'TranscriptCount = pack.Transcripts.Count')) {
    if ($researchTransferText -notmatch [regex]::Escape($marker)) { throw "Buildfix 1 transcript export marker missing: $marker" }
}
foreach ($marker in @('LoadCoreAsync', 'export.TranscriptCount')) {
    if ($researchViewModelText -notmatch [regex]::Escape($marker)) { throw "Buildfix 1 Research refresh marker missing: $marker" }
}
if ($knowledgePackText -notmatch [regex]::Escape('Archive Knowledge Database')) { throw "Unified knowledge-database instructions marker missing." }
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.33.0-ALPHA8-BUILDFIX1-DEEP-RESEARCH-AUDIT.md"))) {
    throw "Alpha 8 Buildfix 1 acceptance guide is missing."
}

$transcriptionSafetyText = Get-Content (Join-Path $root "TheRadioVault.Transcription\Services\TranscriptionSafety.cs") -Raw
$transcriptionCoordinatorText = Get-Content (Join-Path $root "TheRadioVault.Transcription\Services\TranscriptionCoordinator.cs") -Raw
$transcriptsViewModelText = Get-Content (Join-Path $root "TheRadioVault.Presentation\ViewModels\TranscriptsViewModel.cs") -Raw
$transcriptionSettingsText = Get-Content (Join-Path $root "TheRadioVault.Transcription\Services\WhisperCppSettingsStore.cs") -Raw
foreach ($marker in @('ShouldUseVoiceActivityDetection', 'WhisperTimestampMapper', 'IsSpeakerCountPlausible')) {
    if ($transcriptionSafetyText -notmatch [regex]::Escape($marker)) { throw "Buildfix 2 transcription safety marker missing: $marker" }
}
foreach ($marker in @('speakerAnalysisRejected', 'implausible speaker analysis was discarded')) {
    if ($transcriptionCoordinatorText -notmatch [regex]::Escape($marker)) { throw "Buildfix 2 speaker guard marker missing: $marker" }
}
foreach ($marker in @('CurrentTranscriptionActionText', 'ReplaceExistingTranscript: !sample && CurrentHasTranscript')) {
    if ($transcriptsViewModelText -notmatch [regex]::Escape($marker)) { throw "Buildfix 2 re-transcription marker missing: $marker" }
}
if ($transcriptionSettingsText -notmatch [regex]::Escape('storedThreshold <= 0.5 ? 0.9')) { throw "Buildfix 2 diarization migration marker missing." }
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.33.0-ALPHA8-BUILDFIX2-TRANSCRIPTION-CONTINUITY.md"))) {
    throw "Alpha 8 Buildfix 2 acceptance guide is missing."
}

$transcriptionWorkspaceText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\TranscriptsView.axaml") -Raw
foreach ($marker in @('Transcript review', 'Transcription activity', 'Button.transcript-action', 'Play selected phrase')) {
    if ($transcriptionWorkspaceText -notmatch [regex]::Escape($marker)) { throw "Buildfix 3 transcription workspace marker missing: $marker" }
}
if ($transcriptionWorkspaceText -match [regex]::Escape('compact-action')) {
    throw "Buildfix 3 transcription text actions must not use the fixed-width icon-button style."
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.33.0-ALPHA8-BUILDFIX3-TRANSCRIPTION-WORKSPACE.md"))) {
    throw "Alpha 8 Buildfix 3 acceptance guide is missing."
}

$roadmapText = Get-Content (Join-Path $root "docs\guides\ROADMAP.md") -Raw
foreach ($marker in @('RadioVault Server', 'Universal clients', 'Dedicated server foundation', 'Full remote native clients', 'Universal handoff hardening')) {
    if ($roadmapText -notmatch [regex]::Escape($marker)) { throw "Stable 0.33 server/client roadmap marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.33.0-STABLE.md"))) {
    throw "Radio Vault 0.33 stable acceptance guide is missing."
}

$serverProjectPath = Join-Path $root "TheRadioVault.Server\TheRadioVault.Server.csproj"
$serverRuntimePath = Join-Path $root "TheRadioVault.Infrastructure\Services\RadioVaultServerRuntime.cs"
$serverWindowPath = Join-Path $root "TheRadioVault.Server\Views\ServerSettingsWindow.axaml"
foreach ($path in @($serverProjectPath, $serverRuntimePath, $serverWindowPath)) {
    if (-not (Test-Path $path)) { throw "Alpha 1 dedicated server file is missing: $path" }
}
[xml]$serverProject = Get-Content $serverProjectPath -Raw
$serverPropertyGroup = @($serverProject.Project.PropertyGroup)[0]
if ([string]$serverPropertyGroup.Version -ne $version) { throw "RadioVault Server project version does not match VERSION.txt." }
$serverProjectText = Get-Content $serverProjectPath -Raw
if ($serverProjectText -match 'TheRadioVault.Presentation|TheRadioVault.Desktop.Avalonia.csproj') {
    throw "The dedicated server must not depend on the native client presentation shell."
}
$serverRuntimeText = Get-Content $serverRuntimePath -Raw
foreach ($marker in @('RadioVaultServerRuntime', 'HeadlessWebPlaybackController', 'LocalWebServerService')) {
    if ($serverRuntimeText -notmatch [regex]::Escape($marker)) { throw "Alpha 1 server runtime marker missing: $marker" }
}
$serverWindowText = Get-Content $serverWindowPath -Raw
foreach ($marker in @('Background archive service', 'SERVER SETTINGS', 'AUTHORITATIVE STORAGE')) {
    if ($serverWindowText -notmatch [regex]::Escape($marker)) { throw "Alpha 1 settings-only server marker missing: $marker" }
}
if ($serverWindowText -match 'Dashboard|Now Playing|Transcripts') { throw "Normal client feature UI leaked into the settings-only server." }
if ($solutionText -notmatch [regex]::Escape('TheRadioVault.Server\TheRadioVault.Server.csproj')) {
    throw "The dedicated server project is missing from the solution."
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA1-DEDICATED-SERVER-FOUNDATION.md"))) {
    throw "Radio Vault 0.34 Alpha 1 server-foundation guide is missing."
}

$nativeHostText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Composition\AvaloniaApplicationHost.cs") -Raw
$serverClientText = Get-Content (Join-Path $root "TheRadioVault.Infrastructure\Services\LoopbackServerClient.cs") -Raw
$serverPlaybackText = Get-Content (Join-Path $root "TheRadioVault.Infrastructure\Services\LoopbackUserStateServices.cs") -Raw
$mediaBridgeText = Get-Content (Join-Path $root "TheRadioVault.Infrastructure\Services\ServerMediaProxy.cs") -Raw
$nativeCacheText = Get-Content (Join-Path $root "TheRadioVault.Infrastructure\Services\NativeServerResponseCache.cs") -Raw
$serverAdministrationText = Get-Content (Join-Path $root "TheRadioVault.Infrastructure\Services\LoopbackServerAdministrationServices.cs") -Raw
foreach ($marker in @('NativeServerConnectionPreferences.Load', 'useRemoteServer: isRemoteSession', 'ServerMediaProxy', 'isRemoteSession')) {
    if ($nativeHostText -notmatch [regex]::Escape($marker)) { throw "Alpha 11 native composition marker missing: $marker" }
}
foreach ($marker in @('CreateRemoteClient', 'CertificateThumbprint', 'X-RadioVault-Token', 'OpenResponseAsync')) {
    if ($serverClientText -notmatch [regex]::Escape($marker)) { throw "Alpha 11 remote connection marker missing: $marker" }
}
$startupAppText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\App.axaml.cs") -Raw
$cacheSyncText = Get-Content (Join-Path $root "TheRadioVault.Infrastructure\Services\NativeClientCacheSyncService.cs") -Raw
$federationSyncText = Get-Content (Join-Path $root "TheRadioVault.Web\Services\LocalWebServer.FederationLibrarySync.cs") -Raw
foreach ($marker in @('InitializeStartupAsync', 'RefreshStartupCacheAfterLaunchAsync', 'ConfigureSession')) {
    if ($startupAppText -notmatch [regex]::Escape($marker)) { throw "Alpha 19 cache-first launch marker missing: $marker" }
}
foreach ($marker in @('UsePersistentCacheFirst', 'GetLiveJsonAsync')) {
    if ($serverClientText -notmatch [regex]::Escape($marker)) { throw "Alpha 19 persistent cache marker missing: $marker" }
}
foreach ($marker in @('metadataOnly=true', 'LibrarySyncSequence', 'ToHashSet')) {
    if ($cacheSyncText -notmatch [regex]::Escape($marker)) { throw "Alpha 19 delta client marker missing: $marker" }
}
if ($federationSyncText -notmatch [regex]::Escape('!noChanges && !metadataOnly')) {
    throw "Alpha 19 metadata-only server sync marker is missing."
}
$transcriptRepositoryText = Get-Content (Join-Path $root "TheRadioVault.Transcription\Services\SqliteTranscriptRepository.cs") -Raw
foreach ($marker in @('Source = "local"', 'NormalizeTranscriptSource(document.Source)')) {
    if ($transcriptionCoordinatorText -notmatch [regex]::Escape($marker) -and $transcriptRepositoryText -notmatch [regex]::Escape($marker)) {
        throw "Alpha 19 Buildfix 1 transcription compatibility marker missing: $marker"
    }
}
foreach ($marker in @('isIosWebKit', 'const dormantPositionMs = isIosWebKit ? 0 : shared.positionMs')) {
    if ($anywhereWebText -notmatch [regex]::Escape($marker)) { throw "Alpha 19 Buildfix 1 iPhone handoff marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA19-BUILDFIX1-TRANSCRIPTION-IOS-HANDOFF.md"))) {
    throw "Alpha 19 Buildfix 1 release guide is missing."
}
foreach ($marker in @('ETag: ', 'Last-Modified: ', 'Cache-Control: private, max-age=300', 'waitForIosDecoderClock')) {
    if ($anywhereWebText -notmatch [regex]::Escape($marker)) { throw "Alpha 19 Buildfix 2 iPhone range-playback marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA19-BUILDFIX2-IPHONE-RANGE-PLAYBACK.md"))) {
    throw "Alpha 19 Buildfix 2 release guide is missing."
}
foreach ($marker in @('rangeValidatorMatches', 'no-transform', 'Reuse', 'audio.duration < 1')) {
    if ($anywhereWebText -notmatch [regex]::Escape($marker)) { throw "Alpha 19 Buildfix 3 iPhone decoder-probe marker missing: $marker" }
}
if ($anywhereWebText -match [regex]::Escape('assignCanonicalPartSource(id, freshPartIndex, null)')) {
    throw "Alpha 19 Buildfix 3 must not replace the healthy dormant iPhone decoder inside the Move gesture."
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA19-BUILDFIX3-IPHONE-DECODER-PROBE.md"))) {
    throw "Alpha 19 Buildfix 3 release guide is missing."
}
$playbackTransferText = Get-Content (Join-Path $root "TheRadioVault.Web\Services\PlaybackTransferCoordinator.cs") -Raw
foreach ($marker in @('media-start?positionMs=', 'assignCanonicalGestureStartSource', 'decoderRunningAudibly', 'sourceHasNoOutput')) {
    if ($anywhereWebText -notmatch [regex]::Escape($marker) -and $playbackTransferText -notmatch [regex]::Escape($marker)) {
        throw "Alpha 19 Buildfix 4 iPhone audible-start marker missing: $marker"
    }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA19-BUILDFIX4-IPHONE-AUDIBLE-START.md"))) {
    throw "Alpha 19 Buildfix 4 release guide is missing."
}
if ($playbackTransferText -notmatch [regex]::Escape('source.EpisodeId is null or <= 0')) {
    throw "Alpha 19 Buildfix 5 unowned authority marker is missing."
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA19-BUILDFIX5-UNOWNED-AUTHORITY.md"))) {
    throw "Alpha 19 Buildfix 5 release guide is missing."
}
foreach ($marker in @('StreamPositionedWaveAsync', 'currentAudioLogicalBaseMs = gesturePrimedPositionMs', 'currentAudioIsPositioned', 'audio/wav', 'positioned=" + (isIosWebKit ? "1" : "0")', 'commitAlignmentToleranceMs = directAudiblePrime ? 2500 : 750')) {
    if ($anywhereWebText -notmatch [regex]::Escape($marker)) {
        throw "Alpha 19 Buildfix 6 positioned iPhone playback marker missing: $marker"
    }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA19-BUILDFIX6-POSITIONED-IPHONE-AUDIO.md"))) {
    throw "Alpha 19 Buildfix 6 release guide is missing."
}
foreach ($marker in @('currentAudioLogicalBaseMs = gesturePrimedPositionMs', 'return Math.max(0, Number(currentAudioLogicalBaseMs || 0) + localMs)', 'currentAudioLogicalBaseMs = Number(part.logicalStartMs || 0)')) {
    if ($anywhereWebText -notmatch [regex]::Escape($marker)) {
        throw "Alpha 19 Buildfix 7 synchronous logical-base marker missing: $marker"
    }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA19-BUILDFIX7-SYNCHRONOUS-AUDIO-BASE.md"))) {
    throw "Alpha 19 Buildfix 7 release guide is missing."
}
foreach ($marker in @('currentAudioEpisodeId', 'decoderMatchesGestureTarget', 'mustPrimeTargetSourceInGesture', 'if (directAudiblePrime || mustPrimeTargetSourceInGesture)', 'audio.muted = !directAudiblePrime', 'audioEpisodeId: Number(currentAudioEpisodeId || 0)')) {
    if ($anywhereWebText -notmatch [regex]::Escape($marker)) {
        throw "Alpha 19 Buildfix 8 consecutive iPhone broadcast-switch marker missing: $marker"
    }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA19-BUILDFIX8-CONSECUTIVE-IPHONE-SWITCHES.md"))) {
    throw "Alpha 19 Buildfix 8 release guide is missing."
}
$alpha20HostText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Composition\AvaloniaApplicationHost.cs") -Raw
$alpha20SettingsText = Get-Content (Join-Path $root "TheRadioVault.Presentation\ViewModels\DesktopToolsViewModel.cs") -Raw
$alpha20ConnectedText = Get-Content (Join-Path $root "TheRadioVault.Presentation\ViewModels\ConnectedAccessViewModel.cs") -Raw
$alpha20SettingsViewText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\DesktopToolsView.axaml") -Raw
$alpha20ResearchText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Research\AvaloniaResearchPackTransferServices.cs") -Raw
foreach ($marker in @('AppVersionService.DisplayVersion', 'AppVersion = AppVersionService.Version', 'Models and processing on the active server', 'Snapshot.CapabilityGeneration', 'Server/client compatibility', 'ConnectedAccess.CapabilityGenerationText')) {
    if ($alpha20HostText -notmatch [regex]::Escape($marker) -and
        $alpha20SettingsText -notmatch [regex]::Escape($marker) -and
        $alpha20ConnectedText -notmatch [regex]::Escape($marker) -and
        $alpha20SettingsViewText -notmatch [regex]::Escape($marker) -and
        $alpha20ResearchText -notmatch [regex]::Escape($marker)) {
        throw "Alpha 20 release-truth marker missing: $marker"
    }
}
foreach ($staleMarker in @('Alpha 19 Buildfix 2 - reliable iPhone range playback', 'Generation 24 · cache-first client', 'Anywhere compatibility', 'Local whisper.cpp configuration', 'post-1.0 roadmap')) {
    if ($alpha20HostText -match [regex]::Escape($staleMarker) -or
        $alpha20SettingsText -match [regex]::Escape($staleMarker) -or
        $alpha20SettingsViewText -match [regex]::Escape($staleMarker)) {
        throw "Alpha 20 stale release text remains: $staleMarker"
    }
}
foreach ($installerScript in @('package-client-installer.ps1', 'package-server-installer.ps1')) {
    $installerScriptText = Get-Content (Join-Path $root $installerScript) -Raw
    $packageIndex = $installerScriptText.IndexOf('& (Join-Path $root "package-')
    $payloadCheckIndex = $installerScriptText.IndexOf('if (-not (Test-Path $')
    $compilerIndex = $installerScriptText.IndexOf('$compilerCandidates')
    if ($packageIndex -lt 0 -or $payloadCheckIndex -le $packageIndex -or $compilerIndex -le $payloadCheckIndex) {
        throw "Alpha 20 installer payload is not rebuilt and validated before compilation: $installerScript"
    }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA20-RELEASE-HARDENING.md"))) {
    throw "Alpha 20 release-hardening guide is missing."
}
foreach ($marker in @('MediaManifest', 'MediaPart', '_mediaProxy.Register')) {
    if ($serverPlaybackText -notmatch [regex]::Escape($marker)) { throw "Alpha 11 server playback marker missing: $marker" }
}
foreach ($marker in @('TcpListener', 'IPAddress.Loopback', 'Range', 'private, no-store')) {
    if ($mediaBridgeText -notmatch [regex]::Escape($marker)) { throw "Alpha 11 private media bridge marker missing: $marker" }
}
foreach ($marker in @('AesGcm', 'MaximumBytes', 'TryLoad', 'DeleteForServer')) {
    if ($nativeCacheText -notmatch [regex]::Escape($marker)) { throw "Alpha 11 encrypted read-only cache marker missing: $marker" }
}
foreach ($marker in @('FederationSettings', 'FederationLibraryScan', 'LoopbackServerArchiveHealthService')) {
    if ($serverAdministrationText -notmatch [regex]::Escape($marker)) { throw "Alpha 11 server administration marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA11-NATIVE-SERVER-CLIENT.md"))) {
    throw "Radio Vault 0.34 Alpha 11 native server-client guide is missing."
}
$settingsViewModelText = Get-Content (Join-Path $root "TheRadioVault.Presentation\ViewModels\DesktopToolsViewModel.cs") -Raw
$connectedAccessViewModelText = Get-Content (Join-Path $root "TheRadioVault.Presentation\ViewModels\ConnectedAccessViewModel.cs") -Raw
foreach ($marker in @('ApplyAnywhereSnapshotOnUiAsync', '_dispatcher.InvokeAsync(() => AnywhereSnapshot = snapshot)')) {
    if ($settingsViewModelText -notmatch [regex]::Escape($marker)) { throw "Alpha 11 Buildfix 1 Settings dispatcher marker missing: $marker" }
}
foreach ($marker in @('ApplySnapshotOnUiAsync', '_dispatcher!.InvokeAsync')) {
    if ($connectedAccessViewModelText -notmatch [regex]::Escape($marker)) { throw "Alpha 11 Buildfix 1 connection dispatcher marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA11-BUILDFIX1-SETTINGS-THREAD-SAFETY.md"))) {
    throw "Radio Vault 0.34 Alpha 11 Buildfix 1 Settings guide is missing."
}
$settingsViewText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\DesktopToolsView.axaml") -Raw
foreach ($marker in @('CURRENT SERVER', 'ActiveConnectionLabel', 'Use this computer', 'It will not list the server already running on this computer', 'hosted by the server', 'Closing the client does not stop it')) {
    if ($settingsViewText -notmatch [regex]::Escape($marker)) { throw "Alpha 11 Buildfix 2 server-ownership marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA11-BUILDFIX2-SERVER-OWNERSHIP.md"))) {
    throw "Radio Vault 0.34 Alpha 11 Buildfix 2 server-ownership guide is missing."
}
$alpha12HostText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Composition\AvaloniaApplicationHost.cs") -Raw
$alpha12TranscriptionText = Get-Content (Join-Path $root "TheRadioVault.Infrastructure\Services\LoopbackTranscriptionServices.cs") -Raw
$alpha12ServerViewModelText = Get-Content (Join-Path $root "TheRadioVault.Server\ViewModels\ServerSettingsViewModel.cs") -Raw
$alpha12ServerViewText = Get-Content (Join-Path $root "TheRadioVault.Server\Views\ServerSettingsWindow.axaml") -Raw
$alpha12ClientStylesText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\App.axaml") -Raw
$alpha12TranscriptsViewText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\TranscriptsView.axaml") -Raw
foreach ($marker in @('LoopbackServerLibraryFolderService', 'LoopbackServerArchiveHealthService', 'LoopbackServerLibraryMaintenanceService', 'IServerTranscriptionAdministrationService')) {
    if ($alpha12HostText -notmatch [regex]::Escape($marker)) { throw "Alpha 12 server-owned client composition marker missing: $marker" }
}
if ($alpha12HostText -match 'new SqliteDatabase|AvaloniaArchiveBackupService') { throw "Alpha 12 client still constructs a local archive database or backup owner." }
foreach ($marker in @('ServerOwnedTranscriptionEngine', 'ServerOwnedVoiceEmbeddingEngine', 'install-recommended')) {
    if ($alpha12TranscriptionText -notmatch [regex]::Escape($marker)) { throw "Alpha 12 server-owned transcription marker missing: $marker" }
}
foreach ($marker in @('IsServerRunning', 'ServerStateBrush', 'IsTranscriptionSetupRequired', 'TranscriptionStateBrush')) {
    if ($alpha12ServerViewModelText -notmatch [regex]::Escape($marker)) { throw "Alpha 12 server status marker missing: $marker" }
}
foreach ($marker in @('IsVisible="{Binding IsServerStopped}"', 'IsVisible="{Binding IsServerRunning}"', 'IsVisible="{Binding ShowAutomaticTranscriptionSetup}"')) {
    if ($alpha12ServerViewText -notmatch [regex]::Escape($marker)) { throw "Alpha 12 server action-state marker missing: $marker" }
}
if ($alpha12ClientStylesText -notmatch [regex]::Escape('AllowAutoHide')) { throw "Alpha 12 auto-hiding scrollbar marker missing." }
if ($alpha12TranscriptsViewText -match 'RvTranscriptBrush|RvTranscriptSubtleBrush') { throw "Alpha 12 transcription page still uses the navigation-only teal signature colour." }
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA12-SERVER-OWNERSHIP-UX.md"))) {
    throw "Radio Vault 0.34 Alpha 12 guide is missing."
}

$alpha13AccessText = Get-Content (Join-Path $root "TheRadioVault.Infrastructure\Services\NativeConnectedAccessService.cs") -Raw
$alpha13SettingsText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\DesktopToolsView.axaml") -Raw
$alpha13InstallerText = Get-Content (Join-Path $root "installer\RadioVault.Server.iss") -Raw
foreach ($marker in @('MonitorRemoteConnectionAsync', '_preferences.UseRemoteOnStartup = true', 'PublishHealthyConnection', 'nextReconnectAt')) {
    if ($alpha13AccessText -notmatch [regex]::Escape($marker)) { throw "Alpha 13 remote-client monitor marker missing: $marker" }
}
foreach ($marker in @('Pair and use server', 'ConnectionStateBrush', 'ReconnectScheduleText')) {
    if ($alpha13SettingsText -notmatch [regex]::Escape($marker)) { throw "Alpha 13 connection UI marker missing: $marker" }
}
foreach ($marker in @('PrivilegesRequired=lowest', 'Start Radio Vault Server automatically', 'RadioVault.Server.exe', 'uninsdeletevalue')) {
    if ($alpha13InstallerText -notmatch [regex]::Escape($marker)) { throw "Alpha 13 server-installer marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "package-server-installer.ps1"))) { throw "Alpha 13 installer packaging script is missing." }
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA13-REMOTE-CLIENT-INSTALLER.md"))) {
    throw "Radio Vault 0.34 Alpha 13 guide is missing."
}

$alpha13Buildfix1RuntimeText = Get-Content (Join-Path $root "TheRadioVault.Infrastructure\Services\RadioVaultServerRuntime.cs") -Raw
$alpha13Buildfix1ServerViewText = Get-Content (Join-Path $root "TheRadioVault.Server\Views\ServerSettingsWindow.axaml") -Raw
$alpha13Buildfix1LibraryViewText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\LibraryView.axaml") -Raw
$alpha13Buildfix1ActionsText = Get-Content (Join-Path $root "TheRadioVault.Infrastructure\Services\LoopbackUserStateServices.cs") -Raw
$alpha13Buildfix1ClientInstallerText = Get-Content (Join-Path $root "installer\RadioVault.Client.iss") -Raw
foreach ($marker in @('AddLibraryFolderAsync', 'SetLibraryFolderEnabledAsync', 'RemoveLibraryFolderAsync', 'ScanLibraryAsync')) {
    if ($alpha13Buildfix1RuntimeText -notmatch [regex]::Escape($marker)) { throw "Alpha 13 Buildfix 1 server folder marker missing: $marker" }
}
foreach ($marker in @('LIBRARY FOLDERS', 'AddLibraryFolderCommand', 'RemoveLibraryFolderCommand', 'ScanLibraryCommand')) {
    if ($alpha13Buildfix1ServerViewText -notmatch [regex]::Escape($marker)) { throw "Alpha 13 Buildfix 1 server folder UI marker missing: $marker" }
}
foreach ($marker in @('Mark as listened', 'Mark as unlistened', 'MarkListenedCommand', 'MarkUnlistenedCommand')) {
    if ($alpha13Buildfix1LibraryViewText -notmatch [regex]::Escape($marker)) { throw "Alpha 13 Buildfix 1 listening menu marker missing: $marker" }
}
if ($alpha13Buildfix1ActionsText -notmatch [regex]::Escape('WebApiRoutes.ListeningStatus')) { throw "Alpha 13 Buildfix 1 server listening-state route is missing." }
foreach ($marker in @('Radio Vault Client', 'TheRadioVault.exe', 'PrivilegesRequired=lowest')) {
    if ($alpha13Buildfix1ClientInstallerText -notmatch [regex]::Escape($marker)) { throw "Alpha 13 Buildfix 1 client installer marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "package-client-installer.ps1"))) { throw "Alpha 13 Buildfix 1 client installer packaging script is missing." }
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA13-BUILDFIX1-FOLDERS-LISTENING-INSTALLERS.md"))) {
    throw "Radio Vault 0.34 Alpha 13 Buildfix 1 guide is missing."
}

$alpha14WebServerText = Get-Content (Join-Path $root "TheRadioVault.Web\Services\LocalWebServer.cs") -Raw
$alpha14ClientAdapterText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Anywhere\DedicatedServerRadioVaultAnywhereService.cs") -Raw
$alpha14ClientViewText = Get-Content (Join-Path $root "TheRadioVault.Desktop.Avalonia\Views\DesktopToolsView.axaml") -Raw
$alpha14ServerViewText = Get-Content (Join-Path $root "TheRadioVault.Server\Views\ServerSettingsWindow.axaml") -Raw
$alpha14QrText = Get-Content (Join-Path $root "TheRadioVault.Application\Models\PhoneQrCode.cs") -Raw
foreach ($marker in @('productName = "Radio Vault Web"', 'GetAccessUrls().FirstOrDefault()', 'GetSecureSetupUrls().FirstOrDefault()', '<title>Radio Vault Web</title>', 'radio-vault-anywhere-shell-v67')) {
    if ($alpha14WebServerText -notmatch [regex]::Escape($marker)) { throw "Alpha 14 Radio Vault Web server marker missing: $marker" }
}
foreach ($marker in @('response.Web?.AccessUrl', 'response.Web?.SecureSetupUrl')) {
    if ($alpha14ClientAdapterText -notmatch [regex]::Escape($marker)) { throw "Alpha 14 paired-client Web link marker missing: $marker" }
}
foreach ($marker in @('Connect a phone to Radio Vault Web', 'AnywhereQrCode.Rows', 'AnywhereSetupQrCode.Rows', 'Copy phone link')) {
    if ($alpha14ClientViewText -notmatch [regex]::Escape($marker)) { throw "Alpha 14 client Web controls marker missing: $marker" }
}
foreach ($marker in @('RADIO VAULT WEB', 'CopyWebLinkCommand', 'RegenerateWebLinkCommand', 'WebQrCode.Rows', 'SecureSetupQrCode.Rows')) {
    if ($alpha14ServerViewText -notmatch [regex]::Escape($marker)) { throw "Alpha 14 server Web controls marker missing: $marker" }
}
foreach ($marker in @('QRCodeGenerator.GenerateQrCode', 'ECCLevel.M', 'ModuleMatrix')) {
    if ($alpha14QrText -notmatch [regex]::Escape($marker)) { throw "Alpha 14 local QR marker missing: $marker" }
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-ALPHA14-RADIO-VAULT-WEB-PHONE-CONNECTION.md"))) {
    throw "Radio Vault 0.34 Alpha 14 guide is missing."
}

$knowledgePackText = Get-Content (Join-Path $root "TheRadioVault.Infrastructure\Services\KnowledgePackService.cs") -Raw
$knowledgeImportText = Get-Content (Join-Path $root "TheRadioVault.Infrastructure\Services\WebArchiveProvider.RemoteAdministration.cs") -Raw
$knowledgeWebImportText = Get-Content (Join-Path $root "TheRadioVault.Web\Services\LocalWebServer.RemoteAdministration.cs") -Raw
$knowledgeClientText = Get-Content (Join-Path $root "TheRadioVault.Infrastructure\Services\LoopbackServerClient.cs") -Raw
foreach ($marker in @('pack_documentation', 'pack_schema', 'pack_change_log', 'pack_validation', 'ValidateReadableDatabase', 'File.Move(temporaryPath, path, overwrite: true)', 'Read every row in pack_documentation')) {
    if ($knowledgePackText -notmatch [regex]::Escape($marker)) { throw "0.35 Alpha 9 documented Knowledge database marker missing: $marker" }
}
foreach ($marker in @('ResolveImportPageIdentitiesAsync', 'ReadPageCoreBySlugAsync')) {
    if ($wikiServiceText -notmatch [regex]::Escape($marker)) { throw "0.35 Alpha 9 page reconciliation marker missing: $marker" }
}
foreach ($marker in @('CreateKnowledgeImportBackup', 'PRAGMA quick_check')) {
    if ($knowledgeImportText -notmatch [regex]::Escape($marker)) { throw "0.35 Alpha 9 guarded server import marker missing: $marker" }
}
if ($knowledgeWebImportText -notmatch [regex]::Escape('ImportFailureMessage')) { throw "0.35 Alpha 9 actionable server import error marker is missing." }
if ($knowledgeClientText -notmatch [regex]::Escape('Timeout = TimeSpan.FromMinutes(10)')) {
    throw "0.35 Alpha 9 large Knowledge import timeout marker is missing."
}
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.35.0-ALPHA9-KNOWLEDGE-PORTABILITY.md"))) {
    throw "0.35 Alpha 9 Knowledge portability acceptance guide is missing."
}

$stableReadmeText = Get-Content (Join-Path $root "README.md") -Raw
$stableBuildingText = Get-Content (Join-Path $root "BUILDING.md") -Raw
$stableFoundationText = Get-Content (Join-Path $root "tools\Test-AvaloniaFoundation.ps1") -Raw
foreach ($marker in @('<h1 align="center">Radio Vault</h1>', 'Bring your old radio collection back to life.', 'browse your collection by show, year, month and broadcast', 'Radio Vault Server', 'Radio Vault Web', 'Your collection stays on your own hardware', 'should not be exposed directly to the public internet', '## Download the latest test builds', 'actions/workflows/ci.yml?query=branch%3Amain', 'windows-client-and-server', 'macos-client-and-server-osx-arm64-unsigned', 'linux-client-and-server-x64', 'ios-client-simulator-arm64-unsigned', '## AI disclosure', 'does not contain a generative-AI assistant', 'speech-recognition models installed and run locally')) {
    if ($stableReadmeText -notmatch [regex]::Escape($marker)) { throw "Radio Vault marketing README marker missing: $marker" }
}
if ($stableReadmeText -match [regex]::Escape('repository is currently private')) { throw 'Public Radio Vault README still describes the repository as private.' }
foreach ($marker in @('# Building Radio Vault 0.41.0', 'local-release-gate.sh', 'package-macos-local.sh', 'package-server-installer.ps1', 'package-client-installer.ps1', 'SOURCE_MANIFEST.sha256.json')) {
    if ($stableBuildingText -notmatch [regex]::Escape($marker)) { throw "0.41 build-guide marker missing: $marker" }
}
foreach ($marker in @("foundationVersion = '0.35-alpha9-knowledge-portability'", 'databaseSchema = 47', 'lanCapabilityGeneration = 40', 'remoteClientMigrated = $true', 'encryptedRemoteCache = $true', 'automaticReconnect = $true', 'remotePlaybackMigrated = $true')) {
    if ($stableFoundationText -notmatch [regex]::Escape($marker)) { throw "0.35 Alpha 1 architecture-report marker missing: $marker" }
}
$stableAdoptionText = (Get-Content (Join-Path $root "TheRadioVault.Services\Services\LibraryTruthAdoptionService.cs") -Raw) +
    (Get-Content (Join-Path $root "TheRadioVault.Services\Services\LibraryTruthAdoptionRehearsalService.cs") -Raw) +
    (Get-Content (Join-Path $root "TheRadioVault.Services\Services\LibraryTruthEngine.cs") -Raw)
foreach ($staleMarker in @('Run and complete an Alpha10', 'sealed Alpha10 plan', 'Alpha9 classified')) {
    if ($stableAdoptionText -match [regex]::Escape($staleMarker)) { throw "Stable 0.34 still exposes prerelease Library Truth wording: $staleMarker" }
}
$stableSourcePackagingText = Get-Content (Join-Path $root "tools\Package-Source.ps1") -Raw
foreach ($marker in @('$rootInstaller', 'RadioVault\.(Client|Server)')) {
    if ($stableSourcePackagingText -notmatch [regex]::Escape($marker)) { throw "Stable 0.34 source-package exclusion marker missing: $marker" }
}
$stableSourceManifest = Get-Content (Join-Path $root "SOURCE_MANIFEST.sha256.json") -Raw | ConvertFrom-Json
if ($stableSourceManifest.version -ne $version) { throw "Source manifest version '$($stableSourceManifest.version)' does not match '$version'. Run tools\Package-Source.ps1." }
foreach ($relativeProject in @(
    'TheRadioVault.Data\TheRadioVault.Data.csproj',
    'TheRadioVault.Infrastructure\TheRadioVault.Infrastructure.csproj',
    'TheRadioVault.Services\TheRadioVault.Services.csproj',
    'TheRadioVault.Transcription\TheRadioVault.Transcription.csproj')) {
    $stableProjectText = Get-Content (Join-Path $root $relativeProject) -Raw
    if ($stableProjectText -notmatch [regex]::Escape('Microsoft.Data.Sqlite" Version="8.0.29"')) { throw "Stable 0.34 SQLite security rollup is missing from $relativeProject." }
}
$stableDataProjectText = Get-Content (Join-Path $root 'TheRadioVault.Data\TheRadioVault.Data.csproj') -Raw
if ($stableDataProjectText -notmatch [regex]::Escape('SQLitePCLRaw.bundle_e_sqlite3" Version="2.1.12"')) { throw 'Stable 0.34 patched native SQLite bundle override is missing.' }
if (-not (Test-Path (Join-Path $root "docs/history/release-notes/V0.34.0-STABLE.md"))) { throw "Radio Vault 0.34 stable release contract is missing." }

Write-Host "Native server client, Research, transcription, playback and Radio Vault Web source validation passed for $version." -ForegroundColor Green
