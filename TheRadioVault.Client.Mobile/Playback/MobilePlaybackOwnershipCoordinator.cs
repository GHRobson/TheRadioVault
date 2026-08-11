using TheRadioVault.Web.Models;

namespace TheRadioVault.Client.Mobile.Playback;

/// <summary>
/// Owns the client-side rules for deciding whether a shared playback snapshot
/// represents this device, a stable legacy foreign owner, or a committed move
/// away from this device. Network and decoder side effects stay in the session.
/// </summary>
internal sealed class MobilePlaybackOwnershipCoordinator
{
    private readonly Func<string> _currentClientId;
    private string _foreignOwnerCandidate = string.Empty;
    private long _foreignOwnerCandidateGeneration = -1;
    private int _foreignOwnerCandidateSamples;

    public MobilePlaybackOwnershipCoordinator(Func<string> currentClientId)
    {
        _currentClientId = currentClientId ?? throw new ArgumentNullException(nameof(currentClientId));
    }

    public bool HasActivePlayback(WebPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Player.EpisodeId is > 0;
    }

    public bool IsOwnedByThisDevice(WebPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return IsThisClient(session.OwnerClientId);
    }

    public string OwnerName(WebPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return string.IsNullOrWhiteSpace(session.Player.Device)
            ? session.OwnerDevice
            : session.Player.Device;
    }

    public bool WasCommittedAwayFromThisDevice(WebPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var receipt = session.CommittedTransfer;
        return receipt is not null &&
               receipt.Generation == session.Generation &&
               IsThisClient(receipt.SourceClientId) &&
               !IsThisClient(receipt.TargetClientId);
    }

    public bool NeedsSourceStopAcknowledgement(WebPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var receipt = session.CommittedTransfer;
        return receipt is not null &&
               receipt.SourceWasPlaying &&
               !receipt.SourceStopAcknowledged &&
               IsThisClient(receipt.SourceClientId) &&
               !IsThisClient(receipt.TargetClientId);
    }

    public bool ConfirmForeignOwner(WebPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (WasCommittedAwayFromThisDevice(session)) return true;

        // A paused foreign snapshot can briefly appear when the server expires
        // an old browser-style lease. Only a running legacy owner that remains
        // stable across consecutive polls is trusted without a transfer receipt.
        if (!session.Player.IsPlaying)
        {
            Reset();
            return false;
        }

        if (session.Generation != _foreignOwnerCandidateGeneration ||
            !string.Equals(session.OwnerClientId, _foreignOwnerCandidate, StringComparison.Ordinal))
        {
            _foreignOwnerCandidate = session.OwnerClientId;
            _foreignOwnerCandidateGeneration = session.Generation;
            _foreignOwnerCandidateSamples = 1;
            return false;
        }

        _foreignOwnerCandidateSamples++;
        return _foreignOwnerCandidateSamples >= 2;
    }

    public void Reset()
    {
        _foreignOwnerCandidate = string.Empty;
        _foreignOwnerCandidateGeneration = -1;
        _foreignOwnerCandidateSamples = 0;
    }

    private bool IsThisClient(string? clientId)
        => string.Equals(clientId, _currentClientId(), StringComparison.Ordinal);
}
