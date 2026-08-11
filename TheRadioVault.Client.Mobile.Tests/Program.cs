using System.Text.Json;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Client.Mobile.Platform;
using TheRadioVault.Client.Mobile.Playback;
using TheRadioVault.Web.Models;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Downloaded progress stays isolated per broadcast", DownloadedProgressStaysIsolatedAsync),
    ("Completed download progress survives an app restart", CompletedProgressSurvivesRestartAsync),
    ("Reconnect preserves newer offline progress", ReconnectPreservesNewerOfflineProgressAsync),
    ("Reconnect accepts newer server progress for only its broadcast", ReconnectAcceptsNewerServerProgressAsync),
    ("Offline mutations keep one latest decision per broadcast", OfflineMutationsStayIsolatedAsync),
    ("Handoff keeps its transactional ownership boundary", HandoffKeepsTransactionalBoundaryAsync),
    ("Legacy playback ownership requires stable evidence", LegacyPlaybackOwnershipRequiresStableEvidenceAsync),
    ("Committed handoff ownership is trusted immediately", CommittedHandoffOwnershipIsTrustedImmediatelyAsync)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add(test.Name);
        Console.Error.WriteLine($"FAIL  {test.Name}\n      {exception.Message}");
    }
}

Console.WriteLine($"\n{tests.Length - failures.Count}/{tests.Length} mobile regression checks passed.");
if (failures.Count > 0)
{
    Console.Error.WriteLine("Failed: " + string.Join(", ", failures));
    return 1;
}
return 0;

static async Task DownloadedProgressStaysIsolatedAsync()
{
    await WithSeededDownloadsAsync(async service =>
    {
        var capturedAt = DateTimeOffset.UtcNow;
        Ensure(await service.UpdateProgressAsync(101, 69_000, false, capturedAt), "The first progress update was ignored.");
        var first = await service.GetAsync(101) ?? throw new InvalidOperationException("Episode 101 disappeared.");
        var second = await service.GetAsync(202) ?? throw new InvalidOperationException("Episode 202 disappeared.");
        Equal(69_000L, first.Summary.PositionMs, "Episode 101 position");
        Equal(0L, second.Summary.PositionMs, "Episode 202 position");
        Ensure(!second.Summary.InProgress && !second.Summary.Completed, "Episode 202 inherited episode 101's listening state.");
    });
}

static async Task CompletedProgressSurvivesRestartAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        await SeedDownloadsAsync(root);
        using (var server = new MobileServerClient(new MemoryConnectionStore()))
        {
            var service = new MobileDownloadService(server, root);
            Ensure(await service.UpdateProgressAsync(101, 100_000, true, DateTimeOffset.UtcNow), "Completion was not stored.");
        }

        using var reopenedServer = new MobileServerClient(new MemoryConnectionStore());
        var reopened = new MobileDownloadService(reopenedServer, root);
        var first = await reopened.GetAsync(101) ?? throw new InvalidOperationException("Episode 101 was not restored.");
        var second = await reopened.GetAsync(202) ?? throw new InvalidOperationException("Episode 202 was not restored.");
        Ensure(first.Summary.Completed, "Episode 101 lost its completed state after restart.");
        Equal(100_000L, first.Summary.PositionMs, "Episode 101 completed position");
        Equal(0L, second.Summary.PositionMs, "Episode 202 position after restart");
    }
    finally { DeleteTemporaryDirectory(root); }
}

static async Task ReconnectPreservesNewerOfflineProgressAsync()
{
    await WithSeededDownloadsAsync(async service =>
    {
        var offlineAt = DateTimeOffset.UtcNow;
        await service.UpdateProgressAsync(101, 100_000, true, offlineAt);
        var staleServer = Summary(101, "First Broadcast", 69_000, false, offlineAt.AddMinutes(-10));

        await service.ReconcileSummariesAsync([staleServer]);

        var local = await service.GetAsync(101) ?? throw new InvalidOperationException("Episode 101 disappeared.");
        Ensure(local.Summary.Completed, "A stale server response overwrote newer offline completion.");
        Equal(100_000L, local.Summary.PositionMs, "Episode 101 position after stale reconciliation");
    });
}

static async Task ReconnectAcceptsNewerServerProgressAsync()
{
    await WithSeededDownloadsAsync(async service =>
    {
        var localAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await service.UpdateProgressAsync(202, 10_000, false, localAt);
        var newerServer = Summary(202, "Second Broadcast", 42_000, false, DateTimeOffset.UtcNow);

        await service.ReconcileSummariesAsync([newerServer]);

        var first = await service.GetAsync(101) ?? throw new InvalidOperationException("Episode 101 disappeared.");
        var second = await service.GetAsync(202) ?? throw new InvalidOperationException("Episode 202 disappeared.");
        Equal(0L, first.Summary.PositionMs, "Unrelated episode 101 position");
        Equal(42_000L, second.Summary.PositionMs, "Episode 202 reconciled position");
    });
}

static async Task OfflineMutationsStayIsolatedAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        var store = new MobileOfflineMutationStore(root);
        await store.EnqueueListeningStatusAsync("server-a", 101, true);
        await store.EnqueueListeningStatusAsync("server-a", 202, false);
        await store.EnqueueListeningStatusAsync("server-a", 101, false);
        await store.EnqueueFavouriteAsync("server-a", 101, true);
        await store.EnqueueMomentAsync("server-a", 101, 35_000, "Moment", "", "mutation-101");
        await store.EnqueueListeningStatusAsync("server-b", 303, true);

        var pending = await store.GetPendingAsync("server-a");
        Equal(4, pending.Count, "Pending mutations for server A");
        var episode101Listening = pending.Single(value =>
            value.EpisodeId == 101 && value.Kind == MobileOfflineMutationKind.ListeningStatus);
        Ensure(episode101Listening.BooleanValue == false, "The latest episode 101 decision did not replace the earlier one.");
        Ensure(pending.Single(value => value.EpisodeId == 202).BooleanValue == false,
            "Episode 202 inherited another broadcast's decision.");
        Ensure(pending.All(value => value.ServerInstanceId == "server-a"), "A different server's mutations leaked into this queue.");

        var reopened = new MobileOfflineMutationStore(root);
        Equal(4, (await reopened.GetPendingAsync("server-a")).Count, "Persisted pending mutations");
    }
    finally { DeleteTemporaryDirectory(root); }
}

static Task HandoffKeepsTransactionalBoundaryAsync()
{
    var root = FindRepositoryRoot();
    var sessionSource = File.ReadAllText(Path.Combine(root, "TheRadioVault.Client.Mobile", "MobileClientSession.cs"));
    var ownershipSource = File.ReadAllText(Path.Combine(
        root,
        "TheRadioVault.Client.Mobile",
        "Playback",
        "MobilePlaybackOwnershipCoordinator.cs"));
    Contains(sessionSource, "BeginPlaybackTransferAsync", "handoff begin request");
    Contains(sessionSource, "WaitForSourceStopAsync", "handoff source-stop wait");
    Contains(sessionSource, "CommitPlaybackTransferAsync", "handoff commit request");
    Contains(sessionSource, "CancelPlaybackTransferAsync", "handoff cancellation path");
    Contains(ownershipSource, "receipt.Generation == session.Generation", "committed handoff generation guard");
    Contains(sessionSource, "PlaybackTransferAlignmentToleranceMs = 3_000", "live-source alignment tolerance");
    Contains(sessionSource, "<= PlaybackTransferAlignmentToleranceMs", "alignment tolerance use");
    Contains(sessionSource, "WasCommittedAwayFromThisDevice", "uncommitted-owner rejection");
    Contains(ownershipSource, "_foreignOwnerCandidateSamples >= 2", "stable foreign-owner evidence");
    var downloadedBranchStart = sessionSource.IndexOf("if (_offlinePlayback)", StringComparison.Ordinal);
    var streamedBranchStart = sessionSource.IndexOf("if (!IsPaired) return;", downloadedBranchStart, StringComparison.Ordinal);
    Ensure(downloadedBranchStart >= 0 && streamedBranchStart > downloadedBranchStart,
        "The downloaded-playback synchronisation branch could not be located.");
    var downloadedBranch = sessionSource[downloadedBranchStart..streamedBranchStart];
    Contains(downloadedBranch, "GetPlaybackSessionAsync", "downloaded playback ownership polling");
    Contains(downloadedBranch, "StopForCommittedTransferAsync", "downloaded playback source-stop acknowledgement");
    Contains(downloadedBranch, "ReportLivePlaybackAsync", "downloaded playback ownership publication");
    return Task.CompletedTask;
}

static Task LegacyPlaybackOwnershipRequiresStableEvidenceAsync()
{
    var ownership = new MobilePlaybackOwnershipCoordinator(() => "iphone-client");
    var first = PlaybackSession("mac-client", generation: 7, isPlaying: true);
    Ensure(!ownership.ConfirmForeignOwner(first), "The first legacy foreign-owner sample was trusted too early.");
    Ensure(ownership.ConfirmForeignOwner(first), "A stable second legacy foreign-owner sample was not trusted.");

    var newGeneration = PlaybackSession("mac-client", generation: 8, isPlaying: true);
    Ensure(!ownership.ConfirmForeignOwner(newGeneration), "A changed generation reused stale ownership evidence.");
    var paused = PlaybackSession("mac-client", generation: 8, isPlaying: false);
    Ensure(!ownership.ConfirmForeignOwner(paused), "A paused foreign snapshot was trusted.");
    Ensure(!ownership.ConfirmForeignOwner(newGeneration), "A paused snapshot did not reset ownership evidence.");
    return Task.CompletedTask;
}

static Task CommittedHandoffOwnershipIsTrustedImmediatelyAsync()
{
    var ownership = new MobilePlaybackOwnershipCoordinator(() => "iphone-client");
    var receipt = new WebPlaybackCommittedTransfer(
        Guid.NewGuid(),
        "iphone-client",
        "Graham's iPhone",
        "mac-client",
        "Graham's Mac",
        12,
        SourceWasPlaying: true,
        SourceStopAcknowledged: false,
        DateTimeOffset.UtcNow,
        SourceStoppedAt: null);
    var committed = PlaybackSession("mac-client", generation: 12, isPlaying: true, receipt);

    Ensure(ownership.WasCommittedAwayFromThisDevice(committed), "The committed move away was not recognised.");
    Ensure(ownership.ConfirmForeignOwner(committed), "A committed target was not trusted immediately.");
    Ensure(ownership.NeedsSourceStopAcknowledgement(committed), "The playing source did not require a stop acknowledgement.");
    Ensure(!ownership.IsOwnedByThisDevice(committed), "The old iPhone was still treated as owner after handoff.");
    Equal("Graham's Mac", ownership.OwnerName(committed), "Committed owner name");

    var acknowledged = committed with
    {
        CommittedTransfer = receipt with { SourceStopAcknowledged = true }
    };
    Ensure(!ownership.NeedsSourceStopAcknowledgement(acknowledged), "An acknowledged source stop remained pending.");
    return Task.CompletedTask;
}

static WebPlaybackSession PlaybackSession(
    string ownerClientId,
    long generation,
    bool isPlaying,
    WebPlaybackCommittedTransfer? receipt = null)
{
    var player = new WebPlaybackState(
        101,
        "Regression Show",
        "Ownership Test",
        10_000,
        100_000,
        isPlaying ? "Playing" : "Paused",
        LastPlayedAt: null,
        IsPlaying: isPlaying,
        UpdatedAt: DateTimeOffset.UtcNow,
        Device: ownerClientId == "mac-client" ? "Graham's Mac" : "Graham's iPhone");
    return new WebPlaybackSession(
        player,
        player,
        player,
        player.Device,
        ownerClientId,
        generation)
    {
        CommittedTransfer = receipt
    };
}

static async Task WithSeededDownloadsAsync(Func<MobileDownloadService, Task> action)
{
    var root = CreateTemporaryDirectory();
    try
    {
        await SeedDownloadsAsync(root);
        using var server = new MobileServerClient(new MemoryConnectionStore());
        await action(new MobileDownloadService(server, root));
    }
    finally { DeleteTemporaryDirectory(root); }
}

static async Task SeedDownloadsAsync(string root)
{
    Directory.CreateDirectory(root);
    var records = new[]
    {
        await CreateRecordAsync(root, 101, "First Broadcast"),
        await CreateRecordAsync(root, 202, "Second Broadcast")
    };
    await using var stream = File.Create(Path.Combine(root, "downloads.json"));
    await JsonSerializer.SerializeAsync(
        stream,
        new MobileDownloadIndex { Downloads = records.ToList() },
        MobileJsonContext.Default.MobileDownloadIndex);
}

static async Task<MobileDownloadRecord> CreateRecordAsync(string root, long episodeId, string title)
{
    var relativeDirectory = Path.Combine("media", episodeId.ToString(), "seed");
    var absoluteDirectory = Path.Combine(root, relativeDirectory);
    Directory.CreateDirectory(absoluteDirectory);
    var fileName = $"part-001-{episodeId}.mp3";
    var content = new byte[] { 82, 86, 65, 85, 76, 84 };
    await File.WriteAllBytesAsync(Path.Combine(absoluteDirectory, fileName), content);
    return new MobileDownloadRecord(
        Summary(episodeId, title, 0, false, null),
        $"canonical-{episodeId}",
        $"broadcast-{episodeId}",
        100_000,
        DateTimeOffset.UtcNow,
        relativeDirectory,
        [new MobileDownloadPart(1, 1, 0, 100_000, episodeId, content.Length, fileName, "audio/mpeg")]);
}

static WebClientLibraryBroadcastSummary Summary(
    long episodeId,
    string title,
    long positionMs,
    bool completed,
    DateTimeOffset? playedAt)
    => new(
        $"canonical-{episodeId}",
        episodeId,
        $"broadcast-{episodeId}",
        1,
        "Regression Show",
        new DateOnly(2026, 8, 10),
        new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero),
        "08:00",
        title,
        "Regression-test broadcast",
        false,
        completed,
        !completed && positionMs > 0,
        positionMs,
        100_000,
        playedAt,
        null,
        1,
        1,
        1,
        false,
        string.Empty,
        string.Empty,
        0);

static string CreateTemporaryDirectory()
{
    var path = Path.Combine(Path.GetTempPath(), "radiovault-mobile-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static void DeleteTemporaryDirectory(string path)
{
    try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(Environment.CurrentDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "TheRadioVault.sln"))) return directory.FullName;
        directory = directory.Parent;
    }
    throw new InvalidOperationException("Could not find the Radio Vault repository root.");
}

static void Ensure(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string label) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{label}: expected {expected}, got {actual}.");
}

static void Contains(string source, string expected, string label)
{
    if (!source.Contains(expected, StringComparison.Ordinal))
        throw new InvalidOperationException($"Missing {label}: {expected}");
}

file sealed class MemoryConnectionStore : IMobileConnectionStore
{
    public RadioVaultMobileConnection? Load() => null;
    public void Save(RadioVaultMobileConnection connection) { }
    public void Delete() { }
}
