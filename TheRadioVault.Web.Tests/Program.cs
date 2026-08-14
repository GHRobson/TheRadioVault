using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;
using TheRadioVault.Web.Services;
using TheRadioVault.Web.Tests;
using static TheRadioVault.Web.Tests.TestAssert;

var tests = new (string Name, Action Run)[]
{
    ("Web query filters favourites", WebQueryFiltersFavourites),
    ("Web query searches people", WebQuerySearchesPeople),
    ("Web query filters date facets and listening status", WebQueryFiltersDateFacetsAndStatus),
    ("Web query paginates canonical library", WebQueryPaginatesCanonicalLibrary),
    ("Web episode exposes canonical identity", WebEpisodeExposesCanonicalIdentity),
    ("Web progress clamps", () => Equal(100, Episode(1, "Ron & Fez", favourite: false, positionMs: 1100, durationMs: 1000).ProgressPercent)),
    ("Canonical web routes identify manifests and parts", CanonicalWebRoutesAreStable),
    ("Transactional handoff keeps source authoritative until commit", TransactionalHandoffKeepsSourceUntilCommit),
    ("Unowned playback permits a gesture-authorized audible decoder", UnownedPlaybackPermitsAudibleDecoder),
    ("Transactional handoff rejects startup zero", TransactionalHandoffRejectsStartupZero),
    ("Transactional handoff cancels without changing source", TransactionalHandoffCancellationKeepsSource),
    ("Transactional handoff invalidates when source changes", TransactionalHandoffInvalidatesChangedSource),
    ("Transactional handoff refreshes source play state before commit", TransactionalHandoffRefreshesSourcePlayState),
    ("Transactional handoff permits only one preparation", TransactionalHandoffIsSingleFlight),
    ("Transactional handoff covers all six device directions", TransactionalHandoffCoversAllDeviceDirections),
    ("Transactional handoff survives repeated device moves", TransactionalHandoffSurvivesRepeatedDeviceMoves)
}
    .Concat(WebApiRouteResolverTests.Cases)
    .Concat(WebRequestLifecycleResolverTests.Cases)
    .Concat(WebArchiveDiscoveryProjectionTests.Cases)
    .Concat(WebHttpApiTests.Cases)
    .Concat(WebHttpInfrastructureTests.Cases)
    .Concat(WebRequestSecurityTests.Cases)
    .Concat(WebPlaybackIntegrationTests.Cases)
    .Concat(WebShellContractTests.Cases)
    .Concat(WebMediaServerTests.Cases)
    .ToArray();

var selectedTests = args.Length == 0
    ? tests
    : tests.Where(test => args.Any(filter => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
if (selectedTests.Length == 0)
{
    Console.Error.WriteLine("No Web tests matched the supplied filters.");
    return 2;
}

var failures = new List<string>();
foreach (var test in selectedTests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex.Message}");
        Console.Error.WriteLine($"FAIL  {test.Name}: {ex.Message}");
    }
}

Console.WriteLine($"{selectedTests.Length - failures.Count}/{selectedTests.Length} Web tests passed.");
return failures.Count == 0 ? 0 : 1;

static void TransactionalHandoffKeepsSourceUntilCommit()
{
    var coordinator = new PlaybackTransferCoordinator();
    var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    var source = new PlaybackTransferAuthority(
        "Radio Vault server", "server", 7, 9, 120_000, 3_600_000, 1d, true, now);
    var ticket = coordinator.Begin(new WebPlaybackTransferBeginRequest(
        "laptop-01", 9, 120_000, 3_600_000, 1d, true, "Laptop", "DesktopClient"), source, now);
    Equal("server", source.OwnerClientId);
    Equal(120_000L, source.PositionMs);
    True(!ticket.IsReady);
    var readyAt = now.AddSeconds(2);
    ticket = coordinator.MarkReady(new WebPlaybackTransferReadyRequest(
        "laptop-01", ticket.TransferId, 120_000, 3_600_000, true,
        DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d), source, readyAt);
    Equal(122_000L, ticket.CommitPositionMs);
    var committed = coordinator.Commit(new WebPlaybackTransferCommitRequest(
        "laptop-01", ticket.TransferId, ticket.ReadyRevision, ticket.CommitPositionMs, true), source, readyAt);
    Equal("laptop-01", committed.TargetClientId);
    True(coordinator.Pending(readyAt) is null);
}

static void UnownedPlaybackPermitsAudibleDecoder()
{
    var coordinator = new PlaybackTransferCoordinator();
    var now = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
    // Reproduce the provider's raw state: its internal Server sentinel can
    // remain even though the public playback session correctly reports None.
    var unowned = new PlaybackTransferAuthority(
        "Server", "server", 0, null, 0, 0, 1d, false, now);
    var ticket = coordinator.Begin(new WebPlaybackTransferBeginRequest(
        "iphone-01", 9, 18_000, 3_600_000, 1d, true, "iPhone", "Phone"), unowned, now);
    ticket = coordinator.MarkReady(new WebPlaybackTransferReadyRequest(
        "iphone-01", ticket.TransferId, 19_500, 3_600_000, true,
        DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d), unowned, now);
    var committed = coordinator.Commit(new WebPlaybackTransferCommitRequest(
        "iphone-01", ticket.TransferId, ticket.ReadyRevision, 19_500,
        DecoderRunningMuted: false, DecoderRunningAudibly: true), unowned, now);
    Equal("iphone-01", committed.TargetClientId);
    Equal(19_500L, committed.CommitPositionMs);

    var activeCoordinator = new PlaybackTransferCoordinator();
    var active = new PlaybackTransferAuthority(
        "Laptop", "laptop-01", 1, 9, 10_000, 3_600_000, 1d, true, now);
    var activeTicket = activeCoordinator.Begin(new WebPlaybackTransferBeginRequest(
        "iphone-01", 9, 10_000, 3_600_000, 1d, true, "iPhone", "Phone"), active, now);
    activeTicket = activeCoordinator.MarkReady(new WebPlaybackTransferReadyRequest(
        "iphone-01", activeTicket.TransferId, 10_000, 3_600_000, true,
        DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d), active, now);
    Throws<PlaybackTransferConflictException>(() => activeCoordinator.Commit(
        new WebPlaybackTransferCommitRequest(
            "iphone-01", activeTicket.TransferId, activeTicket.ReadyRevision, 10_000,
            DecoderRunningMuted: false, DecoderRunningAudibly: true), active, now));
}

static void TransactionalHandoffRejectsStartupZero()
{
    var coordinator = new PlaybackTransferCoordinator();
    var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    var source = new PlaybackTransferAuthority(
        "Radio Vault server", "server", 3, 9, 321_000, 3_600_000, 1d, true, now);
    var ticket = coordinator.Begin(new WebPlaybackTransferBeginRequest(
        "phone-01", 9, 321_000, 3_600_000, 1d, true, "iPhone", "Phone"), source, now);
    ticket = coordinator.MarkReady(new WebPlaybackTransferReadyRequest(
        "phone-01", ticket.TransferId, 321_000, 3_600_000, true,
        DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d), source, now);
    Throws<PlaybackTransferConflictException>(() => coordinator.Commit(
        new WebPlaybackTransferCommitRequest("phone-01", ticket.TransferId,
            ticket.ReadyRevision, 0, true), source, now));
    True(coordinator.Pending(now) is not null);
}

static void TransactionalHandoffCancellationKeepsSource()
{
    var coordinator = new PlaybackTransferCoordinator();
    var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    var source = new PlaybackTransferAuthority(
        "Laptop", "laptop-01", 11, 9, 480_000, 3_600_000, 1.25d, true, now);
    var ticket = coordinator.Begin(new WebPlaybackTransferBeginRequest(
        "phone-01", 9, 480_000, 3_600_000, 1.25d, true, "iPhone", "Phone"), source, now);
    True(coordinator.Cancel(new WebPlaybackTransferCancelRequest(
        "phone-01", ticket.TransferId, "decoder failed"), now));
    True(coordinator.Pending(now) is null);
    Equal("laptop-01", source.OwnerClientId);
    Equal(480_000L, source.PositionMs);
}

static void TransactionalHandoffInvalidatesChangedSource()
{
    var coordinator = new PlaybackTransferCoordinator();
    var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    var source = new PlaybackTransferAuthority(
        "Radio Vault server", "server", 5, 9, 100_000, 3_600_000, 1d, true, now);
    var ticket = coordinator.Begin(new WebPlaybackTransferBeginRequest(
        "laptop-01", 9, 100_000, 3_600_000, 1d, true, "Laptop", "DesktopClient"), source, now);
    var changedSource = source with { EpisodeId = 10 };
    Throws<PlaybackTransferConflictException>(() => coordinator.MarkReady(
        new WebPlaybackTransferReadyRequest("laptop-01", ticket.TransferId,
            100_000, 3_600_000, true, DesiredPlaying: true,
            OverrideDesiredPlaying: false, Speed: 1d), changedSource, now));
}

static void TransactionalHandoffRefreshesSourcePlayState()
{
    var coordinator = new PlaybackTransferCoordinator();
    var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    var playingSource = new PlaybackTransferAuthority(
        "Radio Vault server", "server", 5, 9, 100_000, 3_600_000, 1.25d, true, now);
    var ticket = coordinator.Begin(new WebPlaybackTransferBeginRequest(
        "phone-01", 9, 100_000, 3_600_000, 1.25d, true, "iPhone", "Phone"), playingSource, now);
    var pausedSource = playingSource with { IsPlaying = false, PositionMs = 101_000, UpdatedAt = now.AddSeconds(1) };
    ticket = coordinator.MarkReady(new WebPlaybackTransferReadyRequest(
        "phone-01", ticket.TransferId, 101_000, 3_600_000, true,
        DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1.25d), pausedSource, now.AddSeconds(1));
    True(!ticket.DesiredPlaying);
    Equal(101_000L, ticket.CommitPositionMs);
}

static void TransactionalHandoffIsSingleFlight()
{
    var coordinator = new PlaybackTransferCoordinator();
    var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    var source = new PlaybackTransferAuthority(
        "Radio Vault server", "server", 5, 9, 100_000, 3_600_000, 1d, true, now);
    coordinator.Begin(new WebPlaybackTransferBeginRequest(
        "laptop-01", 9, 100_000, 3_600_000, 1d, true, "Laptop", "DesktopClient"), source, now);
    Throws<PlaybackTransferConflictException>(() => coordinator.Begin(
        new WebPlaybackTransferBeginRequest(
            "phone-01", 9, 100_000, 3_600_000, 1d, true, "iPhone", "Phone"), source, now));
}


static void TransactionalHandoffCoversAllDeviceDirections()
{
    var directions = new[]
    {
        (SourceDevice: "Server", SourceClient: "server", TargetDevice: "Laptop", TargetClient: "laptop-01", TargetKind: "DesktopClient"),
        (SourceDevice: "Laptop", SourceClient: "laptop-01", TargetDevice: "Radio Vault server", TargetClient: "server", TargetKind: "Server"),
        (SourceDevice: "Server", SourceClient: "server", TargetDevice: "iPhone", TargetClient: "phone-01", TargetKind: "Phone"),
        (SourceDevice: "iPhone", SourceClient: "phone-01", TargetDevice: "Radio Vault server", TargetClient: "server", TargetKind: "Server"),
        (SourceDevice: "Laptop", SourceClient: "laptop-01", TargetDevice: "iPhone", TargetClient: "phone-01", TargetKind: "Phone"),
        (SourceDevice: "iPhone", SourceClient: "phone-01", TargetDevice: "Laptop", TargetClient: "laptop-01", TargetKind: "DesktopClient")
    };

    foreach (var direction in directions)
    {
        var coordinator = new PlaybackTransferCoordinator();
        var now = new DateTimeOffset(2026, 7, 29, 13, 0, 0, TimeSpan.Zero);
        var source = new PlaybackTransferAuthority(
            direction.SourceDevice, direction.SourceClient, 21, 9, 600_000, 3_600_000, 1.25d, true, now);
        var ticket = coordinator.Begin(new WebPlaybackTransferBeginRequest(
            direction.TargetClient, 9, 600_000, 3_600_000, 1.25d, true,
            direction.TargetDevice, direction.TargetKind), source, now);
        ticket = coordinator.MarkReady(new WebPlaybackTransferReadyRequest(
            direction.TargetClient, ticket.TransferId, 600_000, 3_600_000, true,
            DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1.25d), source, now);
        var committed = coordinator.Commit(new WebPlaybackTransferCommitRequest(
            direction.TargetClient, ticket.TransferId, ticket.ReadyRevision,
            ticket.CommitPositionMs, DecoderRunningMuted: true), source, now);
        Equal(direction.TargetClient, committed.TargetClientId);
        Equal(direction.SourceClient, committed.SourceOwnerClientId);
        True(coordinator.Pending(now) is null);
    }
}

static void TransactionalHandoffSurvivesRepeatedDeviceMoves()
{
    var coordinator = new PlaybackTransferCoordinator();
    var now = new DateTimeOffset(2026, 8, 2, 13, 0, 0, TimeSpan.Zero);
    var authority = new PlaybackTransferAuthority(
        "Radio Vault server", "server", 0, 9, 600_000, 3_600_000, 1d, true, now);
    var devices = new[]
    {
        (Id: "phone-stress", Name: "Phone", Kind: "Phone"),
        (Id: "laptop-stress", Name: "Laptop", Kind: "DesktopClient"),
        (Id: "firefox-stress", Name: "Firefox", Kind: "Browser"),
        (Id: "server", Name: "Radio Vault server", Kind: "Server")
    };

    var transferIds = new HashSet<Guid>();
    for (var index = 0; index < 100; index++)
    {
        var target = devices[index % devices.Length];
        var ticket = coordinator.Begin(new WebPlaybackTransferBeginRequest(
            target.Id, 9, authority.PositionMs, authority.DurationMs, authority.Speed,
            true, target.Name, target.Kind), authority, now.AddMilliseconds(index * 10));
        True(transferIds.Add(ticket.TransferId));
        ticket = coordinator.MarkReady(new WebPlaybackTransferReadyRequest(
            target.Id, ticket.TransferId, ticket.ProtectedPositionMs, ticket.DurationMs,
            true, DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: ticket.Speed,
            target.Name, target.Kind), authority, now.AddMilliseconds(index * 10 + 2));
        ticket = coordinator.Commit(new WebPlaybackTransferCommitRequest(
            target.Id, ticket.TransferId, ticket.ReadyRevision, ticket.CommitPositionMs,
            DecoderRunningMuted: true), authority, now.AddMilliseconds(index * 10 + 4));

        authority = new PlaybackTransferAuthority(
            target.Name, target.Id, authority.Generation + 1, ticket.TargetEpisodeId,
            ticket.CommitPositionMs + 250, ticket.DurationMs, ticket.Speed,
            ticket.DesiredPlaying, now.AddMilliseconds(index * 10 + 5));
        True(coordinator.Pending(now.AddMilliseconds(index * 10 + 6)) is null);
    }

    Equal(100, transferIds.Count);
    Equal("server", authority.OwnerClientId);
    Equal(100L, authority.Generation);
}

static void WebQueryFiltersFavourites()
{
    var episodes = new[] { Episode(1, "Ron & Fez", true), Episode(2, "Bennington", false) };
    var result = WebEpisodeQuery.Apply(episodes, "favorites", "", "", 100, new DateTime(2026, 7, 16));
    Equal(1, result.Count);
    Equal(1L, result[0].Id);
}

static void WebQuerySearchesPeople()
{
    var episodes = new[]
    {
        Episode(1, "Ron & Fez", false, people: "Patrice O'Neal"),
        Episode(2, "Bennington", false, people: "Dave Hill")
    };
    var result = WebEpisodeQuery.Apply(episodes, "recent", "Patrice", "", 100, new DateTime(2026, 7, 16));
    Equal(1, result.Count);
    Equal(1L, result[0].Id);
}

static void WebQueryFiltersDateFacetsAndStatus()
{
    var episodes = new[]
    {
        new WebEpisode(1, "Ron & Fez", "January show", new DateTime(2020, 1, 1), "Summary", "", "", 1000, 0, "Unplayed", false, null, DateTime.UtcNow, @"C:\Audio\1.mp3", ""),
        new WebEpisode(2, "Ron & Fez", "February show", new DateTime(2020, 2, 2), "Summary", "", "", 1000, 500, "In Progress", false, DateTime.UtcNow, DateTime.UtcNow, @"C:\Audio\2.mp3", ""),
        new WebEpisode(3, "Bennington", "Completed show", new DateTime(2021, 2, 2), "Summary", "", "", 1000, 1000, "Completed", false, DateTime.UtcNow, DateTime.UtcNow, @"C:\Audio\3.mp3", "")
    };

    var result = WebEpisodeQuery.Apply(episodes, "library", "", "Ron & Fez", 2020, 2, new DateTime(2020, 2, 2), "inprogress", 100, new DateTime(2026, 7, 16));
    Equal(1, result.Count);
    Equal(2L, result[0].CanonicalBroadcastId);
    Equal(2, WebEpisodeQuery.GetYears(episodes).Count);
}

static void WebQueryPaginatesCanonicalLibrary()
{
    var episodes = Enumerable.Range(1, 6).Select(id => Episode(id, "Ron & Fez", false)).ToArray();
    var page = WebEpisodeQuery.ApplyPage(episodes, "library", "", "", null, null, null, "", 2, 2, new DateTime(2026, 7, 16));
    Equal(6, page.Total);
    Equal(2, page.Episodes.Count);
    Equal(4L, page.Episodes[0].Id);
    Equal(3L, page.Episodes[1].Id);
    True(page.HasMore);
}

static void WebEpisodeExposesCanonicalIdentity()
{
    var episode = Episode(7, "Ron & Fez", false);
    Equal(7L, episode.CanonicalBroadcastId);
    Equal("canonical-broadcast", episode.IdentityKind);
}

static WebEpisode Episode(long id, string show, bool favourite, string people = "", long positionMs = 0, long durationMs = 3_600_000)
    => new(id, show, $"Episode {id}", new DateTime(2020, 1, (int)id), "Specific summary", people, "Comedy", durationMs, positionMs,
        positionMs > 0 ? "In Progress" : "Unplayed", favourite, null, new DateTime(2026, 7, 16).AddMinutes(-id), $"C:\\Audio\\{id}.mp3", "");

static void CanonicalWebRoutesAreStable()
{
    Equal("/api/v1/broadcasts/42/media-manifest", WebApiRoutes.MediaManifest(42));
    Equal("/api/v1/broadcasts/42/media/99", WebApiRoutes.MediaPart(42,99));
    Equal("/api/v1/broadcasts/42/metadata", WebApiRoutes.BroadcastMetadata(42));
    Equal("/api/v1/transcripts", WebApiRoutes.Transcripts);
}
