using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Services;

/// <summary>
/// Transitional native-client connection to the dedicated Radio Vault Server.
/// Playback ownership is coordinated over authenticated loopback HTTPS; the
/// native decoder remains local and the server remains the single authority.
/// </summary>
public sealed class LoopbackPlaybackHandoffService : IPlaybackHandoffService, IDisposable
{
    private readonly LoopbackServerClient _connection;
    private readonly bool _ownsConnection;
    private long _currentGeneration;
    private bool _disposed;

    public LoopbackPlaybackHandoffService(
        LoopbackServerClient? connection = null,
        MachineIdentityService? machineIdentity = null)
    {
        var identity = (machineIdentity ?? new MachineIdentityService()).LoadOrCreate();
        CurrentDeviceId = "native-" + identity.MachineId.Replace("-", string.Empty, StringComparison.Ordinal);
        CurrentDeviceName = $"Radio Vault on {identity.MachineName}";

        _connection = connection ?? new LoopbackServerClient();
        _ownsConnection = connection is null;
        IsAvailable = _connection.IsAvailable;
    }

    public bool IsAvailable { get; }
    public string CurrentDeviceId { get; }
    public string CurrentDeviceName { get; }
    public long CurrentGeneration => Interlocked.Read(ref _currentGeneration);

    public async Task<PlaybackHandoffSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var envelope = await SendAsync<PlayerEnvelope>(HttpMethod.Get, WebApiRoutes.Player, null, cancellationToken).ConfigureAwait(false);
        return MapSnapshot(envelope.Session, CurrentDeviceId, CurrentDeviceName, DateTimeOffset.UtcNow, UpdateGeneration);
    }

    public async Task<PlaybackHandoffSnapshot?> ClaimPlaybackAsync(
        long representativeEpisodeId,
        long positionMs,
        long durationMs,
        double speed,
        bool isPlaying,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var request = new WebClientPlaybackUpdate(
            CurrentDeviceId,
            representativeEpisodeId,
            Math.Max(0, positionMs),
            Math.Max(0, durationMs),
            isPlaying,
            Math.Clamp(speed, 0.5d, 3d),
            Force: true,
            DeviceName: CurrentDeviceName,
            DeviceKind: "DesktopClient",
            ExpectedGeneration: CurrentGeneration);
        var envelope = await SendAsync<ClientPlaybackEnvelope>(HttpMethod.Post, WebApiRoutes.PlayerWebProgress, request, cancellationToken).ConfigureAwait(false);
        ThrowIfConflict(envelope.Result.Conflict, envelope.Result.Message);
        return await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlaybackTransferPlan> BeginTransferAsync(
        long representativeEpisodeId,
        long positionMs,
        long durationMs,
        double speed,
        bool play,
        CancellationToken cancellationToken = default)
    {
        var request = new WebPlaybackTransferBeginRequest(
            CurrentDeviceId,
            representativeEpisodeId,
            Math.Max(0, positionMs),
            Math.Max(0, durationMs),
            Math.Clamp(speed, 0.5d, 3d),
            play,
            CurrentDeviceName,
            "DesktopClient");
        var result = await SendTransferAsync(WebApiRoutes.PlayerTransferBegin, request, cancellationToken).ConfigureAwait(false);
        return MapRequiredTransfer(result);
    }

    public async Task<PlaybackTransferPlan> MarkTransferReadyAsync(
        PlaybackTransferPlan transfer,
        long preparedPositionMs,
        long preparedDurationMs,
        bool decoderReady,
        bool desiredPlaying,
        bool overrideDesiredPlaying,
        double speed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        var request = new WebPlaybackTransferReadyRequest(
            CurrentDeviceId,
            transfer.TransferId,
            Math.Max(0, preparedPositionMs),
            Math.Max(0, preparedDurationMs),
            decoderReady,
            desiredPlaying,
            overrideDesiredPlaying,
            Math.Clamp(speed, 0.5d, 3d),
            CurrentDeviceName,
            "DesktopClient");
        var result = await SendTransferAsync(WebApiRoutes.PlayerTransferReady, request, cancellationToken).ConfigureAwait(false);
        return MapRequiredTransfer(result);
    }

    public async Task<PlaybackHandoffSnapshot> CommitTransferAsync(
        PlaybackTransferPlan transfer,
        long preparedPositionMs,
        bool decoderRunningMuted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        var request = new WebPlaybackTransferCommitRequest(
            CurrentDeviceId,
            transfer.TransferId,
            transfer.ReadyRevision,
            Math.Max(0, preparedPositionMs),
            decoderRunningMuted);
        var result = await SendTransferAsync(WebApiRoutes.PlayerTransferCommit, request, cancellationToken).ConfigureAwait(false);
        return MapSnapshot(result.Session, CurrentDeviceId, CurrentDeviceName, DateTimeOffset.UtcNow, UpdateGeneration);
    }

    public async Task CancelTransferAsync(
        PlaybackTransferPlan transfer,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        var request = new WebPlaybackTransferCancelRequest(CurrentDeviceId, transfer.TransferId, reason ?? string.Empty);
        await SendTransferAsync(WebApiRoutes.PlayerTransferCancel, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task AcknowledgeSourceStoppedAsync(
        Guid transferId,
        long generation,
        CancellationToken cancellationToken = default)
    {
        var request = new WebPlaybackTransferSourceStoppedRequest(CurrentDeviceId, transferId, generation);
        await SendTransferAsync(WebApiRoutes.PlayerTransferSourceStopped, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReportAsync(
        long representativeEpisodeId,
        long positionMs,
        long durationMs,
        double speed,
        bool isPlaying,
        bool completed,
        bool explicitSeek = false,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var request = new WebClientPlaybackUpdate(
            CurrentDeviceId,
            representativeEpisodeId,
            Math.Max(0, positionMs),
            Math.Max(0, durationMs),
            isPlaying,
            Math.Clamp(speed, 0.5d, 3d),
            completed,
            Force: false,
            DeviceName: CurrentDeviceName,
            DeviceKind: "DesktopClient",
            ExpectedGeneration: CurrentGeneration,
            ExplicitSeek: explicitSeek);
        var envelope = await SendAsync<ClientPlaybackEnvelope>(HttpMethod.Post, WebApiRoutes.PlayerWebProgress, request, cancellationToken).ConfigureAwait(false);
        ThrowIfConflict(envelope.Result.Conflict, envelope.Result.Message);
    }

    public static PlaybackHandoffSnapshot MapSnapshot(
        WebPlaybackSession session,
        string currentDeviceId,
        string currentDeviceName,
        DateTimeOffset refreshedAt,
        Action<long>? generationObserved = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var devices = session.Devices.Select(MapDevice).ToArray();
        var ownerId = !string.IsNullOrWhiteSpace(session.OwnerClientId)
            ? session.OwnerClientId
            : IsServerOwner(session.OwnerDevice) ? "server" : string.Empty;
        var owner = devices.FirstOrDefault(device =>
            string.Equals(device.DeviceId, ownerId, StringComparison.Ordinal));
        var ownerName = owner?.DisplayName ??
            (string.IsNullOrWhiteSpace(session.OwnerDevice) ? "No device" : session.OwnerDevice);
        var active = owner is not null && owner.RepresentativeEpisodeId.HasValue
            ? owner
            : MapState(session.Player, ownerId, ownerName, owner?.Kind ?? "Client", isOwner: true, isOnline: true);
        if (!active.RepresentativeEpisodeId.HasValue) active = null;

        generationObserved?.Invoke(session.Generation);
        return new PlaybackHandoffSnapshot(
            currentDeviceId,
            currentDeviceName,
            ownerId,
            ownerName,
            session.Generation,
            active,
            devices,
            refreshedAt)
        {
            CommittedTransfer = session.CommittedTransfer is null
                ? null
                : new PlaybackTransferCommitReceipt(
                    session.CommittedTransfer.TransferId,
                    session.CommittedTransfer.SourceClientId,
                    session.CommittedTransfer.SourceDeviceName,
                    session.CommittedTransfer.TargetClientId,
                    session.CommittedTransfer.TargetDeviceName,
                    session.CommittedTransfer.Generation,
                    session.CommittedTransfer.SourceWasPlaying,
                    session.CommittedTransfer.SourceStopAcknowledged,
                    session.CommittedTransfer.CommittedAt,
                    session.CommittedTransfer.SourceStoppedAt)
        };
    }

    private static PlaybackDeviceState MapDevice(WebPlaybackDevice device)
        => MapState(device.State, device.DeviceId, device.DisplayName, device.Kind, device.IsOwner, device.IsOnline);

    private static PlaybackDeviceState MapState(
        WebPlaybackState state,
        string deviceId,
        string displayName,
        string kind,
        bool isOwner,
        bool isOnline)
        => new(
            deviceId,
            displayName,
            kind,
            state.EpisodeId,
            state.Show,
            state.Title,
            Math.Max(0, state.PositionMs),
            Math.Max(0, state.DurationMs),
            Math.Clamp(state.Speed, 0.5d, 3d),
            state.IsPlaying,
            isOwner,
            isOnline,
            state.UpdatedAt ?? DateTimeOffset.UtcNow);

    private async Task<WebPlaybackTransferResult> SendTransferAsync<TRequest>(
        string path,
        TRequest request,
        CancellationToken cancellationToken)
    {
        EnsureAvailable();
        var envelope = await SendAsync<TransferEnvelope>(HttpMethod.Post, path, request, cancellationToken).ConfigureAwait(false);
        ThrowIfConflict(envelope.Result.Conflict, envelope.Result.Message);
        UpdateGeneration(envelope.Result.Session.Generation);
        return envelope.Result;
    }

    private async Task<TEnvelope> SendAsync<TEnvelope>(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        return await _connection.SendJsonAsync<TEnvelope>(method, path, body, allowConflict: true, cancellationToken).ConfigureAwait(false);
    }

    private PlaybackTransferPlan MapRequiredTransfer(WebPlaybackTransferResult result)
    {
        var ticket = result.Transfer ?? throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(result.Message) ? "Radio Vault Server did not issue a playback transfer." : result.Message);
        UpdateGeneration(result.Session.Generation);
        return new PlaybackTransferPlan(
            ticket.TransferId,
            ticket.TargetEpisodeId,
            ticket.ProtectedPositionMs,
            ticket.CommitPositionMs,
            ticket.DurationMs,
            ticket.Speed,
            ticket.DesiredPlaying,
            ticket.DesiredPlayingOverridden,
            ticket.SourceOwnerDevice,
            ticket.SourceOwnerClientId,
            ticket.SourceGeneration,
            ticket.ReadyRevision,
            ticket.IsReady,
            ticket.ExpiresAt);
    }

    private static bool IsServerOwner(string value)
        => string.Equals(value, "Server", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "Desktop", StringComparison.OrdinalIgnoreCase);

    private static void ThrowIfConflict(bool conflict, string message)
    {
        if (conflict)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message) ? "Playback changed on another Radio Vault client." : message);
    }

    private void UpdateGeneration(long generation)
        => Interlocked.Exchange(ref _currentGeneration, Math.Max(0, generation));

    private void EnsureAvailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsAvailable)
            throw new InvalidOperationException("The dedicated Radio Vault Server connection is not enabled.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsConnection) _connection.Dispose();
    }

    private sealed record PlayerEnvelope(WebPlaybackSession Session);
    private sealed record TransferEnvelope(WebPlaybackTransferResult Result);
    private sealed record ClientPlaybackEnvelope(WebClientPlaybackResult Result);
}
