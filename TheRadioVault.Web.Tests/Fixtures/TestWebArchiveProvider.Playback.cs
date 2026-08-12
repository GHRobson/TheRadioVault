using TheRadioVault.Web.Models;
using TheRadioVault.Web.Services;

namespace TheRadioVault.Web.Tests.Fixtures;

internal sealed partial class TestWebArchiveProvider
{
    public WebPlaybackState GetPlaybackState() => _desktop;
    public WebPlaybackState GetWebPlaybackState() => _web;
    public WebPlaybackSession GetPlaybackSession()
    {
        if (_throwPlayback) throw new InvalidOperationException("Synthetic playback failure.");
        var remoteOwner = !_ownerDevice.Equals("Server", StringComparison.OrdinalIgnoreCase);
        var player = remoteOwner ? _web : _desktop;
        var ownerId = remoteOwner ? _ownerClientId : "server";
        var devices = new[]
        {
            new WebPlaybackDevice("server", "Radio Vault server", "Server", _desktop, DateTimeOffset.UtcNow, true, !remoteOwner),
            new WebPlaybackDevice(string.IsNullOrWhiteSpace(_ownerClientId) ? "phone-owner-01" : _ownerClientId,
                string.IsNullOrWhiteSpace(_web.Device) ? "Phone" : _web.Device,
                "Phone", _web, DateTimeOffset.UtcNow, true, remoteOwner)
        };
        return new WebPlaybackSession(player, _desktop, _web, _ownerDevice, ownerId, _generation)
        {
            Devices = devices,
            PendingTransfer = _playbackTransfers.Pending(DateTimeOffset.UtcNow),
            CommittedTransfer = _committedTransfer
        };
    }
    public WebPlaybackCommandResult ExecutePlaybackCommand(WebPlaybackCommand command)
    {
        if (command.ExpectedRevision.HasValue && command.ExpectedRevision.Value != _desktop.Revision && !command.Force)
            return new WebPlaybackCommandResult(false, true, "Stale", _desktop);
        var playing = command.Command.Equals("pause", StringComparison.OrdinalIgnoreCase) ? false : true;
        _desktop = _desktop with { IsPlaying = playing, Revision = _desktop.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
        if (playing)
        {
            _ownerDevice = "Server";
            _ownerClientId = string.Empty;
            _generation++;
        }
        AddChange("player", _desktop.EpisodeId ?? 0);
        return new WebPlaybackCommandResult(true, false, "Changed", _desktop);
    }
    public WebClientPlaybackResult UpdateWebPlayback(WebClientPlaybackUpdate update)
    {
        var owns = !_ownerDevice.Equals("Server", StringComparison.OrdinalIgnoreCase) && _ownerClientId == update.ClientId;
        if (!owns || (update.ExpectedGeneration > 0 && update.ExpectedGeneration != _generation))
            return new WebClientPlaybackResult(false, true, "Another client owns playback", _web);
        if (!update.ExplicitSeek && _web.EpisodeId == update.EpisodeId && _web.PositionMs >= 10_000 && update.PositionMs < _web.PositionMs - 3_000)
            return new WebClientPlaybackResult(false, true, "Stale decoder position", _web);
        _webClient = update.IsPlaying ? update.ClientId : string.Empty;
        _web = new WebPlaybackState(_episode.Id, _episode.Show, _episode.Title, update.PositionMs,
            update.DurationMs > 0 ? update.DurationMs : _episode.DurationMs, "In Progress", DateTime.Now,
            update.IsPlaying, DateTimeOffset.UtcNow, string.IsNullOrWhiteSpace(update.DeviceName) ? "Phone" : update.DeviceName,
            update.Speed, _web.Revision + 1, update.ClientId);
        return new WebClientPlaybackResult(true, false, "Saved", _web);
    }
    public WebPlaybackTransferResult BeginPlaybackTransfer(WebPlaybackTransferBeginRequest request)
    {
        try
        {
            var ticket = _playbackTransfers.Begin(request with
            {
                PositionMs = Math.Max(Math.Max(0, request.PositionMs), Math.Max(0, _episode.PositionMs)),
                DurationMs = Math.Max(request.DurationMs, _episode.DurationMs)
            }, TransferAuthority(), DateTimeOffset.UtcNow);
            return new WebPlaybackTransferResult(true, false, "Preparing", ticket, GetPlaybackSession());
        }
        catch (PlaybackTransferConflictException exception)
        {
            return new WebPlaybackTransferResult(false, true, exception.Message, null, GetPlaybackSession());
        }
    }
    public WebPlaybackTransferResult MarkPlaybackTransferReady(WebPlaybackTransferReadyRequest request)
    {
        try
        {
            var ticket = _playbackTransfers.MarkReady(request, TransferAuthority(), DateTimeOffset.UtcNow);
            return new WebPlaybackTransferResult(true, false, "Ready", ticket, GetPlaybackSession());
        }
        catch (PlaybackTransferConflictException exception)
        {
            return new WebPlaybackTransferResult(false, true, exception.Message, null, GetPlaybackSession());
        }
    }
    public WebPlaybackTransferResult CommitPlaybackTransfer(WebPlaybackTransferCommitRequest request)
    {
        var currentOwnerClientId = _ownerDevice.Equals("Server", StringComparison.OrdinalIgnoreCase)
            ? "server" : _ownerClientId;
        if (_committedTransfer is not null && _committedTicket is not null &&
            _committedTransfer.TransferId == request.TransferId &&
            string.Equals(_committedTransfer.TargetClientId, request.ClientId, StringComparison.Ordinal) &&
            string.Equals(currentOwnerClientId, request.ClientId, StringComparison.Ordinal) &&
            _committedTransfer.Generation == _generation &&
            (_ownerDevice.Equals("Server", StringComparison.OrdinalIgnoreCase)
                ? _desktop.EpisodeId == _committedTicket.TargetEpisodeId
                : _web.EpisodeId == _committedTicket.TargetEpisodeId))
            return new WebPlaybackTransferResult(true, false, "Already committed", _committedTicket, GetPlaybackSession());

        try
        {
            var authority = TransferAuthority();
            var ticket = _playbackTransfers.Commit(request, authority, DateTimeOffset.UtcNow);
            _ownerDevice = ticket.TargetClientId.Equals("server", StringComparison.OrdinalIgnoreCase)
                ? "Server" : ticket.TargetDeviceName;
            _ownerClientId = ticket.TargetClientId.Equals("server", StringComparison.OrdinalIgnoreCase)
                ? string.Empty : ticket.TargetClientId;
            _generation++;
            var sourceClientId = authority.OwnerClientId;
            var acknowledged = !authority.IsPlaying || string.Equals(sourceClientId, ticket.TargetClientId, StringComparison.Ordinal);
            _committedTicket = ticket;
            _committedTransfer = new WebPlaybackCommittedTransfer(
                ticket.TransferId, sourceClientId, authority.OwnerDevice,
                ticket.TargetClientId, ticket.TargetDeviceName, _generation,
                authority.IsPlaying, acknowledged, DateTimeOffset.UtcNow,
                acknowledged ? DateTimeOffset.UtcNow : null);
            _episode = _episode with
            {
                PositionMs = Math.Max(_episode.PositionMs, ticket.CommitPositionMs),
                DurationMs = ticket.DurationMs,
                Status = ticket.CommitPositionMs > 0 ? "In Progress" : "Unplayed",
                LastPlayedAt = DateTime.Now
            };
            if (!ticket.TargetClientId.Equals("server", StringComparison.OrdinalIgnoreCase))
                _web = new WebPlaybackState(ticket.TargetEpisodeId, _episode.Show, _episode.Title,
                    ticket.CommitPositionMs, ticket.DurationMs, "In Progress", DateTime.Now,
                    ticket.DesiredPlaying, DateTimeOffset.UtcNow, ticket.TargetDeviceName,
                    ticket.Speed, _web.Revision + 1, ticket.TargetClientId);
            else
                _desktop = _desktop with { PositionMs = ticket.CommitPositionMs, DurationMs = ticket.DurationMs,
                    IsPlaying = ticket.DesiredPlaying, UpdatedAt = DateTimeOffset.UtcNow, Revision = _desktop.Revision + 1 };
            return new WebPlaybackTransferResult(true, false, "Committed", ticket, GetPlaybackSession());
        }
        catch (PlaybackTransferConflictException exception)
        {
            return new WebPlaybackTransferResult(false, true, exception.Message, null, GetPlaybackSession());
        }
    }
    public WebPlaybackTransferResult CancelPlaybackTransfer(WebPlaybackTransferCancelRequest request)
    {
        var changed = _playbackTransfers.Cancel(request, DateTimeOffset.UtcNow);
        return new WebPlaybackTransferResult(changed, false, changed ? "Cancelled" : "Inactive", null, GetPlaybackSession());
    }
    public WebPlaybackTransferResult AcknowledgePlaybackTransferSourceStopped(WebPlaybackTransferSourceStoppedRequest request)
    {
        if (_committedTransfer is null || _committedTicket is null ||
            _committedTransfer.TransferId != request.TransferId ||
            _committedTransfer.Generation != request.Generation ||
            !string.Equals(_committedTransfer.SourceClientId, request.ClientId, StringComparison.Ordinal))
            return new WebPlaybackTransferResult(false, true, "Stale acknowledgement", null, GetPlaybackSession());
        _committedTransfer = _committedTransfer with
        {
            SourceStopAcknowledged = true,
            SourceStoppedAt = DateTimeOffset.UtcNow
        };
        return new WebPlaybackTransferResult(true, false, "Source stopped", _committedTicket, GetPlaybackSession());
    }
    private PlaybackTransferAuthority TransferAuthority()
    {
        var server = _ownerDevice.Equals("Server", StringComparison.OrdinalIgnoreCase);
        var state = server ? _desktop : _web;
        return new PlaybackTransferAuthority(_ownerDevice, server ? "server" : _ownerClientId, _generation,
            state.EpisodeId, state.PositionMs, state.DurationMs, state.Speed, state.IsPlaying,
            state.UpdatedAt ?? DateTimeOffset.UtcNow);
    }
    public WebOfflineProgressResult SyncOfflineProgress(WebOfflineProgressUpdate update)
    {
        if (update.EpisodeId != _episode.Id) return new WebOfflineProgressResult(false, "Not found");
        if (update.AllowRewind &&
            (_ownerDevice.Equals("Server", StringComparison.OrdinalIgnoreCase) ||
             _ownerClientId != update.ClientId ||
             (update.ExpectedGeneration > 0 && update.ExpectedGeneration != _generation)))
            return new WebOfflineProgressResult(false, "Another client owns playback", _episode, Conflict: true);
        var generationBoundOwnerWrite = update.AllowRewind && update.ExpectedGeneration > 0;
        if (generationBoundOwnerWrite && !update.ExplicitSeek &&
            _episode.PositionMs >= 10_000 && update.PositionMs < _episode.PositionMs - 3_000)
            return new WebOfflineProgressResult(false, "Stale durable progress", _episode, Conflict: true);
        var mayResetPosition = generationBoundOwnerWrite && update.ExplicitSeek;
        if (!update.Completed && !mayResetPosition && update.PositionMs <= _episode.PositionMs)
            return new WebOfflineProgressResult(false, "Newer progress exists", _episode);
        var duration = update.DurationMs > 0 ? update.DurationMs : _episode.DurationMs;
        _episode = _episode with
        {
            PositionMs = mayResetPosition ? update.PositionMs : Math.Max(_episode.PositionMs, update.PositionMs),
            DurationMs = duration,
            Status = update.Completed ? "Completed" : "In Progress",
            LastPlayedAt = DateTime.Now
        };
        return new WebOfflineProgressResult(true, "Offline progress saved", _episode);
    }
}
