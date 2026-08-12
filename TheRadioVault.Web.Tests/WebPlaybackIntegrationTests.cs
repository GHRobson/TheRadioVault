using TheRadioVault.Web.Models;
using TheRadioVault.Web.Tests.Fixtures;
using static TheRadioVault.Web.Tests.TestAssert;

namespace TheRadioVault.Web.Tests;

internal static class WebPlaybackIntegrationTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases { get; } =
    [
        ("Transactional begin and commit retries are idempotent", TransactionalHandoffRetriesAreIdempotent),
        ("Non-transactional playback cannot steal an active owner", NonTransactionalPlaybackCannotStealActiveOwner),
        ("Transactional handoff preserves newer durable target progress", TransactionalHandoffPreservesNewerTargetProgress),
        ("Transactional handoff requires a physical source-stop receipt", TransactionalHandoffRequiresSourceStopReceipt),
        ("Transactional source-stop receipts reject stale acknowledgements", TransactionalHandoffRejectsStaleSourceStopReceipt),
        ("A newer handoff supersedes the prior source-stop receipt", NewerHandoffSupersedesPriorSourceStopReceipt),
        ("Live playback heartbeats do not mutate durable progress", LivePlaybackHeartbeatIsNotDurable),
        ("Failed transactional commit preserves owner and progress", FailedTransactionalCommitPreservesSource),
        ("Durable playback rejects a stale zero after handoff", DurablePlaybackRejectsStaleZeroAfterHandoff),
        ("Generation-less progress retries cannot rewind", GenerationlessProgressCannotRewind),
        ("Web playback lease rejects another client", WebPlaybackLeaseRejectsAnotherClient),
    ];

static void TransactionalHandoffRetriesAreIdempotent()
{
    var provider = new TestWebArchiveProvider();
    var request = new WebPlaybackTransferBeginRequest(
        "phone-retry-01", 9, 120_000, 3_600_000, 1d, true, "iPhone", "Phone");
    var firstBegin = provider.BeginPlaybackTransfer(request);
    var retryBegin = provider.BeginPlaybackTransfer(request);
    True(firstBegin.Changed && retryBegin.Changed);
    Equal(firstBegin.Transfer!.TransferId, retryBegin.Transfer!.TransferId);

    var ready = provider.MarkPlaybackTransferReady(new WebPlaybackTransferReadyRequest(
        "phone-retry-01", firstBegin.Transfer.TransferId, firstBegin.Transfer.ProtectedPositionMs,
        3_600_000, true, DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d));
    var commitRequest = new WebPlaybackTransferCommitRequest(
        "phone-retry-01", ready.Transfer!.TransferId, ready.Transfer.ReadyRevision,
        ready.Transfer.CommitPositionMs, DecoderRunningMuted: true);
    var firstCommit = provider.CommitPlaybackTransfer(commitRequest);
    var generation = firstCommit.Session.Generation;
    var retryCommit = provider.CommitPlaybackTransfer(commitRequest);

    True(firstCommit.Changed && retryCommit.Changed);
    Equal(firstCommit.Transfer!.TransferId, retryCommit.Transfer!.TransferId);
    Equal(generation, retryCommit.Session.Generation);
    Equal("phone-retry-01", retryCommit.Session.OwnerClientId);
}

static void NonTransactionalPlaybackCannotStealActiveOwner()
{
    var provider = new TestWebArchiveProvider();
    var begin = provider.BeginPlaybackTransfer(new WebPlaybackTransferBeginRequest(
        "phone-owner", 9, 120_000, 3_600_000, 1d, true, "Phone", "Phone"));
    var ready = provider.MarkPlaybackTransferReady(new WebPlaybackTransferReadyRequest(
        "phone-owner", begin.Transfer!.TransferId, begin.Transfer.ProtectedPositionMs,
        3_600_000, true, DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d));
    var commit = provider.CommitPlaybackTransfer(new WebPlaybackTransferCommitRequest(
        "phone-owner", ready.Transfer!.TransferId, ready.Transfer.ReadyRevision,
        ready.Transfer.CommitPositionMs, DecoderRunningMuted: true));
    True(commit.Changed);

    var staleClaim = provider.UpdateWebPlayback(new WebClientPlaybackUpdate(
        "laptop-stale", 9, commit.Transfer!.CommitPositionMs, 3_600_000, true, 1d,
        Force: true, DeviceName: "Laptop", DeviceKind: "DesktopClient",
        ExpectedGeneration: commit.Session.Generation));
    True(staleClaim.Conflict);
    Equal("phone-owner", provider.GetPlaybackSession().OwnerClientId);
}

static void TransactionalHandoffPreservesNewerTargetProgress()
{
    var provider = new TestWebArchiveProvider();
    var begin = provider.BeginPlaybackTransfer(new WebPlaybackTransferBeginRequest(
        "phone-01", 9, 0, 3_600_000, 1d, true, "iPhone", "Phone"));
    True(begin.Changed && begin.Transfer is not null, $"Begin failed: conflict={begin.Conflict}; message={begin.Message}");
    True(begin.Transfer!.ProtectedPositionMs >= 120_000L);
}

static void TransactionalHandoffRequiresSourceStopReceipt()
{
    var provider = new TestWebArchiveProvider();
    var begin = provider.BeginPlaybackTransfer(new WebPlaybackTransferBeginRequest(
        "phone-01", 9, 120_000, 3_600_000, 1d, true, "iPhone", "Phone"));
    var ready = provider.MarkPlaybackTransferReady(new WebPlaybackTransferReadyRequest(
        "phone-01", begin.Transfer!.TransferId, begin.Transfer.ProtectedPositionMs,
        3_600_000, true, DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d));
    var commit = provider.CommitPlaybackTransfer(new WebPlaybackTransferCommitRequest(
        "phone-01", ready.Transfer!.TransferId, ready.Transfer.ReadyRevision,
        ready.Transfer.CommitPositionMs, DecoderRunningMuted: true));

    True(commit.Changed);
    var receipt = commit.Session.CommittedTransfer;
    True(receipt is not null);
    Equal("server", receipt!.SourceClientId);
    True(receipt.SourceWasPlaying);
    True(!receipt.SourceStopAcknowledged);

    var acknowledged = provider.AcknowledgePlaybackTransferSourceStopped(
        new WebPlaybackTransferSourceStoppedRequest("server", receipt.TransferId, receipt.Generation));
    True(acknowledged.Changed);
    True(acknowledged.Session.CommittedTransfer?.SourceStopAcknowledged == true);
}

static void TransactionalHandoffRejectsStaleSourceStopReceipt()
{
    var provider = new TestWebArchiveProvider();
    var begin = provider.BeginPlaybackTransfer(new WebPlaybackTransferBeginRequest(
        "phone-01", 9, 120_000, 3_600_000, 1d, true, "iPhone", "Phone"));
    var ready = provider.MarkPlaybackTransferReady(new WebPlaybackTransferReadyRequest(
        "phone-01", begin.Transfer!.TransferId, begin.Transfer.ProtectedPositionMs,
        3_600_000, true, DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d));
    var commit = provider.CommitPlaybackTransfer(new WebPlaybackTransferCommitRequest(
        "phone-01", ready.Transfer!.TransferId, ready.Transfer.ReadyRevision,
        ready.Transfer.CommitPositionMs, DecoderRunningMuted: true));
    var receipt = commit.Session.CommittedTransfer!;

    var wrongGeneration = provider.AcknowledgePlaybackTransferSourceStopped(
        new WebPlaybackTransferSourceStoppedRequest("server", receipt.TransferId, receipt.Generation + 1));
    True(wrongGeneration.Conflict);
    True(wrongGeneration.Session.CommittedTransfer?.SourceStopAcknowledged == false);

    var wrongSource = provider.AcknowledgePlaybackTransferSourceStopped(
        new WebPlaybackTransferSourceStoppedRequest("other-device", receipt.TransferId, receipt.Generation));
    True(wrongSource.Conflict);
    True(wrongSource.Session.CommittedTransfer?.SourceStopAcknowledged == false);
}

static void NewerHandoffSupersedesPriorSourceStopReceipt()
{
    var provider = new TestWebArchiveProvider();

    var firstBegin = provider.BeginPlaybackTransfer(new WebPlaybackTransferBeginRequest(
        "phone-01", 9, 120_000, 3_600_000, 1d, true, "iPhone", "Phone"));
    var firstReady = provider.MarkPlaybackTransferReady(new WebPlaybackTransferReadyRequest(
        "phone-01", firstBegin.Transfer!.TransferId, firstBegin.Transfer.ProtectedPositionMs,
        3_600_000, true, DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d));
    var firstCommit = provider.CommitPlaybackTransfer(new WebPlaybackTransferCommitRequest(
        "phone-01", firstReady.Transfer!.TransferId, firstReady.Transfer.ReadyRevision,
        firstReady.Transfer.CommitPositionMs, DecoderRunningMuted: true));
    var firstReceipt = firstCommit.Session.CommittedTransfer!;

    var secondBegin = provider.BeginPlaybackTransfer(new WebPlaybackTransferBeginRequest(
        "laptop-02", 9, firstReady.Transfer.CommitPositionMs, 3_600_000, 1d, true,
        "Laptop", "DesktopClient"));
    var secondReady = provider.MarkPlaybackTransferReady(new WebPlaybackTransferReadyRequest(
        "laptop-02", secondBegin.Transfer!.TransferId, secondBegin.Transfer.ProtectedPositionMs,
        3_600_000, true, DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d));
    var secondCommit = provider.CommitPlaybackTransfer(new WebPlaybackTransferCommitRequest(
        "laptop-02", secondReady.Transfer!.TransferId, secondReady.Transfer.ReadyRevision,
        secondReady.Transfer.CommitPositionMs, DecoderRunningMuted: true));

    True(secondCommit.Changed);
    Equal("laptop-02", secondCommit.Session.OwnerClientId);
    True(secondCommit.Session.Generation > firstReceipt.Generation);

    var staleFirstAcknowledgement = provider.AcknowledgePlaybackTransferSourceStopped(
        new WebPlaybackTransferSourceStoppedRequest(
            firstReceipt.SourceClientId, firstReceipt.TransferId, firstReceipt.Generation));
    True(staleFirstAcknowledgement.Conflict);
    Equal("laptop-02", staleFirstAcknowledgement.Session.OwnerClientId);

    var currentReceipt = secondCommit.Session.CommittedTransfer!;
    var currentAcknowledgement = provider.AcknowledgePlaybackTransferSourceStopped(
        new WebPlaybackTransferSourceStoppedRequest(
            currentReceipt.SourceClientId, currentReceipt.TransferId, currentReceipt.Generation));
    True(currentAcknowledgement.Changed);
    True(currentAcknowledgement.Session.CommittedTransfer?.SourceStopAcknowledged == true);
}

static void LivePlaybackHeartbeatIsNotDurable()
{
    var provider = new TestWebArchiveProvider();
    var begin = provider.BeginPlaybackTransfer(new WebPlaybackTransferBeginRequest(
        "phone-01", 9, 120_000, 3_600_000, 1d, true, "iPhone", "Phone"));
    var ready = provider.MarkPlaybackTransferReady(new WebPlaybackTransferReadyRequest(
        "phone-01", begin.Transfer!.TransferId, begin.Transfer.ProtectedPositionMs,
        3_600_000, true, DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d));
    var commit = provider.CommitPlaybackTransfer(new WebPlaybackTransferCommitRequest(
        "phone-01", ready.Transfer!.TransferId, ready.Transfer.ReadyRevision,
        ready.Transfer.CommitPositionMs, DecoderRunningMuted: true));
    var durableAtCommit = provider.GetEpisode(9)!.PositionMs;

    var heartbeat = provider.UpdateWebPlayback(new WebClientPlaybackUpdate(
        "phone-01", 9, durableAtCommit + 30_000, 3_600_000, true, 1d,
        ExpectedGeneration: commit.Session.Generation));
    True(heartbeat.Changed);
    Equal(durableAtCommit, provider.GetEpisode(9)!.PositionMs);
}

static void FailedTransactionalCommitPreservesSource()
{
    var provider = new TestWebArchiveProvider();
    var original = provider.GetPlaybackSession();
    var originalPosition = provider.GetEpisode(9)!.PositionMs;
    var begin = provider.BeginPlaybackTransfer(new WebPlaybackTransferBeginRequest(
        "phone-01", 9, originalPosition, 3_600_000, 1d, true, "iPhone", "Phone"));
    var ready = provider.MarkPlaybackTransferReady(new WebPlaybackTransferReadyRequest(
        "phone-01", begin.Transfer!.TransferId, begin.Transfer.ProtectedPositionMs,
        3_600_000, true, DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d));
    var failed = provider.CommitPlaybackTransfer(new WebPlaybackTransferCommitRequest(
        "phone-01", ready.Transfer!.TransferId, ready.Transfer.ReadyRevision,
        PreparedPositionMs: 0, DecoderRunningMuted: true));

    True(failed.Conflict);
    Equal(original.OwnerDevice, provider.GetPlaybackSession().OwnerDevice);
    Equal(original.Generation, provider.GetPlaybackSession().Generation);
    Equal(originalPosition, provider.GetEpisode(9)!.PositionMs);
}

static void GenerationlessProgressCannotRewind()
{
    var provider = new TestWebArchiveProvider();
    var begin = provider.BeginPlaybackTransfer(new WebPlaybackTransferBeginRequest(
        "phone-01", 9, 120_000, 3_600_000, 1d, true, "iPhone", "Phone"));
    var ready = provider.MarkPlaybackTransferReady(new WebPlaybackTransferReadyRequest(
        "phone-01", begin.Transfer!.TransferId, begin.Transfer.ProtectedPositionMs,
        3_600_000, true, DesiredPlaying: true, OverrideDesiredPlaying: false, Speed: 1d));
    var commit = provider.CommitPlaybackTransfer(new WebPlaybackTransferCommitRequest(
        "phone-01", ready.Transfer!.TransferId, ready.Transfer.ReadyRevision,
        ready.Transfer.CommitPositionMs, DecoderRunningMuted: true));
    True(commit.Changed);

    var retry = provider.SyncOfflineProgress(new WebOfflineProgressUpdate(
        "phone-01", 9, 0, 3_600_000, Completed: false, Speed: 1d,
        AllowRewind: true, ExpectedGeneration: 0));
    True(!retry.Changed);
    Equal(commit.Transfer!.CommitPositionMs, provider.GetEpisode(9)!.PositionMs);
}

static void DurablePlaybackRejectsStaleZeroAfterHandoff()
{
    var provider = new TestWebArchiveProvider();
    var begin = provider.BeginPlaybackTransfer(new WebPlaybackTransferBeginRequest(
        "phone-01", 9, 120_000, 3_600_000, 1d, true, "iPhone", "Phone"));
    True(begin.Changed && begin.Transfer is not null);
    var ready = provider.MarkPlaybackTransferReady(new WebPlaybackTransferReadyRequest(
        "phone-01", begin.Transfer!.TransferId, begin.Transfer.ProtectedPositionMs,
        3_600_000, true, DesiredPlaying: true, OverrideDesiredPlaying: true, Speed: 1d));
    True(ready.Changed && ready.Transfer is not null, $"Ready failed: conflict={ready.Conflict}; message={ready.Message}");
    var commit = provider.CommitPlaybackTransfer(new WebPlaybackTransferCommitRequest(
        "phone-01", ready.Transfer!.TransferId, ready.Transfer.ReadyRevision,
        ready.Transfer.CommitPositionMs, true));
    True(commit.Changed, $"Commit failed: conflict={commit.Conflict}; message={commit.Message}");

    var stale = provider.SyncOfflineProgress(new WebOfflineProgressUpdate(
        "phone-01", 9, 0, 3_600_000, Completed: false, Speed: 1d,
        AllowRewind: true, ExpectedGeneration: commit.Session.Generation));
    True(stale.Conflict);
    Equal(commit.Transfer!.CommitPositionMs, provider.GetEpisode(9)!.PositionMs);
}

static void WebPlaybackLeaseRejectsAnotherClient()
{
    var provider = new TestWebArchiveProvider();
    var begin = provider.BeginPlaybackTransfer(new WebPlaybackTransferBeginRequest(
        "first-client-01", 9, 120_000, 3_600_000, 1d, true, "Laptop", "DesktopClient"));
    True(begin.Changed && begin.Transfer is not null, $"Begin failed: conflict={begin.Conflict}; message={begin.Message}");
    var ready = provider.MarkPlaybackTransferReady(new WebPlaybackTransferReadyRequest(
        "first-client-01", begin.Transfer!.TransferId, begin.Transfer.ProtectedPositionMs,
        3_600_000, true, DesiredPlaying: true, OverrideDesiredPlaying: true, Speed: 1d));
    True(ready.Changed && ready.Transfer is not null, $"Ready failed: conflict={ready.Conflict}; message={ready.Message}");
    var commit = provider.CommitPlaybackTransfer(new WebPlaybackTransferCommitRequest(
        "first-client-01", ready.Transfer!.TransferId, ready.Transfer.ReadyRevision,
        ready.Transfer.CommitPositionMs, true));
    True(commit.Changed, $"Commit failed: conflict={commit.Conflict}; message={commit.Message}");
    var first = provider.UpdateWebPlayback(new WebClientPlaybackUpdate(
        "first-client-01", 9, 130_000, 3_600_000, true,
        ExpectedGeneration: commit.Session.Generation));
    True(first.Changed, $"First client update failed: conflict={first.Conflict}; message={first.Message}; generation={commit.Session.Generation}");
    var second = provider.UpdateWebPlayback(new WebClientPlaybackUpdate(
        "second-client-02", 9, 140_000, 3_600_000, true,
        ExpectedGeneration: commit.Session.Generation));
    True(second.Conflict, $"Second client unexpectedly accepted: changed={second.Changed}; message={second.Message}; generation={commit.Session.Generation}");
}

}

