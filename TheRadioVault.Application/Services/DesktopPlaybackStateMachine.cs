namespace TheRadioVault.Application.Services;

/// <summary>
/// Owns the state transitions shared by the desktop transport and handoff UI.
/// Presentation code projects these values, while decoder and network side effects
/// remain at the outer playback boundary.
/// </summary>
public sealed class DesktopPlaybackStateMachine
{
    public bool IsLoaded { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IsPlaybackElsewhere { get; private set; }
    public bool TransportPending { get; private set; }
    public bool DesiredPlaying { get; private set; }
    public bool TransportIntentChanged { get; private set; }

    public bool SetLoaded(bool value)
    {
        if (IsLoaded == value) return false;
        IsLoaded = value;
        return true;
    }

    public bool SetBusy(bool value)
    {
        if (IsBusy == value) return false;
        IsBusy = value;
        return true;
    }

    public bool SetPlaybackElsewhere(bool value)
    {
        if (IsPlaybackElsewhere == value) return false;
        IsPlaybackElsewhere = value;
        return true;
    }

    public bool ObserveLocalPlayback(bool value)
    {
        if (IsPlaying == value) return false;
        IsPlaying = value;
        if (!TransportPending) DesiredPlaying = value;
        return true;
    }

    public void BeginTransport(bool desiredPlaying)
    {
        TransportPending = true;
        DesiredPlaying = desiredPlaying;
        TransportIntentChanged = false;
    }

    public void AdoptObservedDesiredPlayback(bool desiredPlaying)
    {
        if (!TransportIntentChanged) DesiredPlaying = desiredPlaying;
    }

    public void AdoptTransferDesiredPlayback(bool desiredPlaying)
        => DesiredPlaying = desiredPlaying;

    public bool TogglePendingTransportIntent()
    {
        if (!TransportPending) return false;
        TransportIntentChanged = true;
        DesiredPlaying = !DesiredPlaying;
        return true;
    }

    public void SetLocalTransportIntent(bool desiredPlaying)
        => DesiredPlaying = desiredPlaying;

    public void ReleaseForRemoteHandoff()
    {
        DesiredPlaying = false;
        if (TransportPending) TransportIntentChanged = true;
    }

    public void AcknowledgeRemoteSourceStop()
    {
        DesiredPlaying = false;
        TransportIntentChanged = false;
    }

    public void CompleteTransport()
    {
        TransportPending = false;
        DesiredPlaying = IsPlaying;
        TransportIntentChanged = false;
    }
}
