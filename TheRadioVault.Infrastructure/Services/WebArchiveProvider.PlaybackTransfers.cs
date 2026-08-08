using TheRadioVault.Core.Events;
using TheRadioVault.Web.Models;
using TheRadioVault.Web.Services;

namespace TheRadioVault.Services;

internal sealed partial class WebArchiveProvider
{
    public WebPlaybackTransferResult BeginPlaybackTransfer(WebPlaybackTransferBeginRequest request)
    {
        var desktop = GetPlaybackState();
        if (!ValidPlaybackEndpointId(request.ClientId))
            return TransferFailure(false, "A valid target device identity is required.");
        // Handoff is a data-integrity boundary. Resolve only the requested broadcast
        // from the database so a large archive cannot turn this request into a full
        // library rebuild and exceed the web client's transfer deadline.
        var episode = GetEpisodeDirect(request.EpisodeId);
        if (episode is null) return TransferFailure(false, "Broadcast not found.");

        WebPlaybackTransferTicket ticket;
        try
        {
            lock (_playbackGate)
            {
                var authority = CreateTransferAuthority(desktop, DateTimeOffset.UtcNow);
                ticket = _playbackTransfers.Begin(request with
                {
                    // A stale card or cached phone shell must never start a transfer
                    // behind the server's durable position. A deliberate rewind is a
                    // separate, explicit owner-only seek after the transfer commits.
                    PositionMs = Math.Max(Math.Max(0, request.PositionMs), Math.Max(0, episode.PositionMs)),
                    DurationMs = request.DurationMs > 0 ? Math.Max(request.DurationMs, episode.DurationMs) : episode.DurationMs,
                    Speed = Math.Clamp(request.Speed, 0.5d, 3d),
                    DeviceName = NormalizeDeviceName(request.DeviceName, request.DeviceKind),
                    DeviceKind = NormalizeDeviceKind(request.DeviceKind)
                }, authority, DateTimeOffset.UtcNow);
            }
        }
        catch (PlaybackTransferConflictException exception)
        {
            return TransferFailure(true, exception.Message);
        }

        AddChange("playback-transfer", request.EpisodeId, $"begin:{ticket.TransferId:N}", DateTimeOffset.UtcNow);
        return new WebPlaybackTransferResult(true, false,
            $"Preparing playback on {ticket.TargetDeviceName}.", ticket, GetPlaybackSession());
    }

    public WebPlaybackTransferResult MarkPlaybackTransferReady(WebPlaybackTransferReadyRequest request)
    {
        var desktop = GetPlaybackState();
        WebPlaybackTransferTicket ticket;
        try
        {
            lock (_playbackGate)
            {
                var authority = CreateTransferAuthority(desktop, DateTimeOffset.UtcNow);
                ticket = _playbackTransfers.MarkReady(request, authority, DateTimeOffset.UtcNow);
            }
        }
        catch (PlaybackTransferConflictException exception)
        {
            return TransferFailure(true, exception.Message);
        }

        return new WebPlaybackTransferResult(true, false,
            "Target decoder is ready. Aligning to the final source playhead.", ticket, GetPlaybackSession());
    }

    public WebPlaybackTransferResult CommitPlaybackTransfer(WebPlaybackTransferCommitRequest request)
    {
        var desktop = GetPlaybackState();
        WebPlaybackTransferTicket? alreadyCommitted = null;
        lock (_playbackGate)
        {
            var currentOwnerClientId = IsServerPlaybackOwner(_playbackOwnerDevice)
                ? "server"
                : _playbackOwnerClientId;
            if (_lastCommittedTransfer is not null && _lastCommittedTransferTicket is not null &&
                _lastCommittedTransfer.TransferId == request.TransferId &&
                string.Equals(_lastCommittedTransfer.TargetClientId, request.ClientId, StringComparison.Ordinal) &&
                string.Equals(currentOwnerClientId, request.ClientId, StringComparison.Ordinal) &&
                _lastCommittedTransfer.Generation == _playbackSessionGeneration &&
                (IsServerPlaybackOwner(_playbackOwnerDevice)
                    ? desktop.EpisodeId == _lastCommittedTransferTicket.TargetEpisodeId
                    : _webPlayback.EpisodeId == _lastCommittedTransferTicket.TargetEpisodeId))
            {
                alreadyCommitted = _lastCommittedTransferTicket;
            }
        }

        // Commit is idempotent for the exact current transfer. A target may retry
        // after losing the HTTP response without stopping itself or creating a new
        // ownership generation.
        if (alreadyCommitted is not null)
            return new WebPlaybackTransferResult(true, false,
                $"Playback is already owned by {alreadyCommitted.TargetDeviceName}.",
                alreadyCommitted, GetPlaybackSession());

        WebPlaybackTransferTicket ticket;
        PlaybackTransferAuthority source;
        WebEpisode target;
        long targetDuration;
        long generation;
        var committedAt = DateTimeOffset.UtcNow;

        try
        {
            lock (_playbackGate)
            {
                source = CreateTransferAuthority(desktop, committedAt);
                ticket = _playbackTransfers.Commit(request, source, committedAt);
                target = GetEpisodeDirect(ticket.TargetEpisodeId)
                    ?? throw new PlaybackTransferConflictException("The target broadcast is no longer available.");
                targetDuration = ticket.DurationMs > 0 ? ticket.DurationMs : target.DurationMs;

                try
                {
                    // Preserve a different outgoing broadcast before changing the
                    // generation. Once ownership commits, the old remote endpoint is
                    // intentionally forbidden from issuing another durable write.
                    if (source.EpisodeId is > 0 && source.EpisodeId != ticket.TargetEpisodeId)
                    {
                        var sourceEpisode = GetEpisodeDirect(source.EpisodeId.Value);
                        if (sourceEpisode is not null)
                        {
                            var sourcePosition = source.ProjectedPositionMs(committedAt);
                            var sourceDuration = source.DurationMs > 0 ? source.DurationMs : sourceEpisode.DurationMs;
                            var sourceCompleted = sourceDuration > 0 &&
                                sourcePosition >= Math.Max(0, sourceDuration - 5_000) &&
                                sourceEpisode.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase);
                            _database.SavePlaybackState(sourceEpisode.Id, sourcePosition, sourceDuration,
                                sourceCompleted, source.Speed, incrementPlayCount: false);
                        }
                    }

                    // The transfer boundary is durable before it becomes visible as
                    // the new owner. A database failure therefore leaves the source
                    // running and the ticket available for a safe retry/cancel.
                    var targetCompleted = targetDuration > 0 &&
                        ticket.CommitPositionMs >= Math.Max(0, targetDuration - 5_000) &&
                        target.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase);
                    _database.SavePlaybackState(target.Id, ticket.CommitPositionMs, targetDuration,
                        targetCompleted, ticket.Speed, incrementPlayCount: false,
                        // A transfer is never a rewind instruction. This boundary may
                        // advance progress or preserve it, but only an explicit seek
                        // from the committed owner may intentionally move it backwards.
                        allowPositionReset: false);
                }
                catch
                {
                    _playbackTransfers.Restore(ticket, committedAt);
                    throw;
                }

                _playbackOwnerDevice = string.Equals(ticket.TargetClientId, "server", StringComparison.OrdinalIgnoreCase)
                    ? "Server"
                    : ticket.TargetDeviceName;
                _playbackOwnerClientId = string.Equals(ticket.TargetClientId, "server", StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : ticket.TargetClientId;
                _playbackSessionGeneration++;
                generation = _playbackSessionGeneration;

                var sourceClientId = IsServerPlaybackOwner(source.OwnerDevice)
                    ? "server"
                    : source.OwnerClientId;
                var sourceStopAcknowledged = !source.IsPlaying ||
                    string.IsNullOrWhiteSpace(sourceClientId) ||
                    string.Equals(sourceClientId, ticket.TargetClientId, StringComparison.Ordinal);
                _lastCommittedTransferTicket = ticket;
                _lastCommittedTransfer = new WebPlaybackCommittedTransfer(
                    ticket.TransferId,
                    sourceClientId,
                    source.OwnerDevice,
                    ticket.TargetClientId,
                    ticket.TargetDeviceName,
                    generation,
                    source.IsPlaying,
                    sourceStopAcknowledged,
                    committedAt,
                    sourceStopAcknowledged ? committedAt : null);

                // Ownership becomes singular immediately, but do not pretend that an
                // outgoing remote decoder has physically stopped until that source has
                // observed the new generation and acknowledged its pause. This receipt
                // lets the target remain muted through the real quiescence boundary.
                foreach (var deviceId in _remotePlaybackDevices.Keys.ToArray())
                {
                    var existingDevice = _remotePlaybackDevices[deviceId];
                    var isOutgoingSource = !sourceStopAcknowledged &&
                        string.Equals(deviceId, sourceClientId, StringComparison.Ordinal);
                    var outgoingPosition = source.ProjectedPositionMs(committedAt);
                    _remotePlaybackDevices[deviceId] = existingDevice with
                    {
                        State = existingDevice.State with
                        {
                            PositionMs = isOutgoingSource ? outgoingPosition : existingDevice.State.PositionMs,
                            DurationMs = isOutgoingSource && source.DurationMs > 0
                                ? source.DurationMs
                                : existingDevice.State.DurationMs,
                            Speed = isOutgoingSource ? source.Speed : existingDevice.State.Speed,
                            IsPlaying = isOutgoingSource,
                            Status = isOutgoingSource
                                ? "In Progress"
                                : existingDevice.State.PositionMs > 0 ? "In Progress" : "Paused",
                            UpdatedAt = committedAt,
                            Revision = Interlocked.Increment(ref _webRevision)
                        },
                        IsOwner = false
                    };
                }

                if (!string.Equals(ticket.TargetClientId, "server", StringComparison.OrdinalIgnoreCase))
                {
                    var state = new WebPlaybackState(
                        target.Id,
                        target.Show,
                        target.Title,
                        ticket.CommitPositionMs,
                        targetDuration,
                        ticket.CommitPositionMs > 0 ? "In Progress" : "Unplayed",
                        DateTime.Now,
                        ticket.DesiredPlaying,
                        committedAt,
                        ticket.TargetDeviceName,
                        ticket.Speed,
                        Interlocked.Increment(ref _webRevision),
                        ticket.TargetClientId);
                    _webPlayback = state;
                    _webPlaybackClientId = ticket.DesiredPlaying ? ticket.TargetClientId : string.Empty;
                    _webPlaybackLeaseExpiresAt = committedAt.Add(WebPlaybackLeaseDuration);
                    _remotePlaybackDevices[ticket.TargetClientId] = new WebPlaybackDevice(
                        ticket.TargetClientId,
                        ticket.TargetDeviceName,
                        ticket.TargetDeviceKind,
                        state,
                        committedAt,
                        true,
                        true);
                }
                else
                {
                    _webPlayback = _webPlayback with
                    {
                        IsPlaying = false,
                        UpdatedAt = committedAt,
                        Revision = Interlocked.Increment(ref _webRevision),
                        ControllerClientId = string.Empty
                    };
                    _webPlaybackClientId = string.Empty;
                    _webPlaybackLeaseExpiresAt = DateTimeOffset.MinValue;
                }

                _pendingPhoneOwnerClientId = string.Empty;
                _pendingPhoneOwnerExpiresAt = DateTimeOffset.MinValue;
                _remoteControlClientId = string.Empty;
                _remoteControlLeaseExpiresAt = DateTimeOffset.MinValue;
            }
        }
        catch (PlaybackTransferConflictException exception)
        {
            return TransferFailure(true, exception.Message);
        }
        catch (Exception exception)
        {
            return TransferFailure(false,
                $"Playback remained on the original device because the transfer boundary could not be saved: {exception.Message}");
        }

        // Prepare-first/commit-last: only after ownership and its durable boundary
        // have committed is the previous server output paused. Remote sources stop
        // when they observe the new generation; their protected boundary is already
        // safe in the database.
        if (IsServerPlaybackOwner(source.OwnerDevice) &&
            !string.Equals(ticket.TargetClientId, "server", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                WebPlaybackCommandResult pauseResult;
                lock (_desktopCommandGate)
                {
                    pauseResult = _playbackController.ExecutePlaybackCommand(new WebPlaybackCommand(
                        "pause-for-handoff-commit",
                        ticket.TargetClientId,
                        source.EpisodeId,
                        source.ProjectedPositionMs(committedAt),
                        Force: true,
                        DeviceName: ticket.TargetDeviceName,
                        DeviceKind: ticket.TargetDeviceKind));
                }

                if (!pauseResult.Conflict)
                {
                    lock (_playbackGate)
                        MarkCommittedSourceStoppedCore(ticket.TransferId, "server", generation, DateTimeOffset.UtcNow);
                }
            }
            catch (Exception exception)
            {
                // Commit has already crossed its durable ownership boundary. Never
                // turn a source-pause/UI failure into a false HTTP failure that makes
                // the target stop itself. The unacknowledged receipt keeps the target
                // muted until its bounded quiescence wait expires.
                System.Diagnostics.Trace.WriteLine(
                    $"[Transactional handoff] Server source-stop command will rely on the safety timeout: {exception}");
            }
        }

        // Ownership is already committed. Presentation/event subscribers are
        // best-effort notifications and must not make the target believe the commit
        // failed after the source has been released.
        try
        {
            _events.Publish(new PlaybackOwnershipChangedEvent(
                ticket.TargetEpisodeId,
                ticket.CommitPositionMs,
                targetDuration,
                ticket.Speed,
                ticket.DesiredPlaying,
                string.Equals(ticket.TargetClientId, "server", StringComparison.OrdinalIgnoreCase)
                    ? "Server"
                    : ticket.TargetDeviceName,
                committedAt));
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"[Transactional handoff] Ownership notification failed after commit: {exception}");
        }
        try
        {
            _events.Publish(new PlaybackChangedEvent(
                ticket.TargetEpisodeId,
                ticket.CommitPositionMs,
                targetDuration,
                ticket.DesiredPlaying,
                committedAt));
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"[Transactional handoff] Progress notification failed after commit: {exception}");
        }
        AddChange("playback-owner", ticket.TargetEpisodeId,
            $"transaction:{ticket.TransferId:N}:{generation}", committedAt);

        return new WebPlaybackTransferResult(true, false,
            $"Playback moved to {ticket.TargetDeviceName}.", ticket, GetPlaybackSession());
    }

    public WebPlaybackTransferResult AcknowledgePlaybackTransferSourceStopped(
        WebPlaybackTransferSourceStoppedRequest request)
    {
        if (!ValidPlaybackEndpointId(request.ClientId))
            return TransferFailure(false, "A valid source device identity is required.");

        bool changed;
        WebPlaybackTransferTicket ticket;
        lock (_playbackGate)
        {
            if (_lastCommittedTransfer is null || _lastCommittedTransferTicket is null ||
                _lastCommittedTransfer.TransferId != request.TransferId ||
                _lastCommittedTransfer.Generation != request.Generation ||
                !string.Equals(_lastCommittedTransfer.SourceClientId, request.ClientId, StringComparison.Ordinal))
            {
                return TransferFailure(true,
                    "This source-stop acknowledgement does not belong to the current committed playback move.");
            }

            ticket = _lastCommittedTransferTicket;
            changed = MarkCommittedSourceStoppedCore(
                request.TransferId,
                request.ClientId,
                request.Generation,
                DateTimeOffset.UtcNow);
        }

        if (changed)
            AddChange("playback-transfer", ticket.TargetEpisodeId,
                $"source-stopped:{request.TransferId:N}:{request.ClientId}", DateTimeOffset.UtcNow);
        return new WebPlaybackTransferResult(true, false,
            changed ? "The previous playback output confirmed that it stopped." : "The previous output was already stopped.",
            ticket, GetPlaybackSession());
    }

    private bool MarkCommittedSourceStoppedCore(
        Guid transferId,
        string sourceClientId,
        long generation,
        DateTimeOffset stoppedAt)
    {
        var committed = _lastCommittedTransfer;
        if (committed is null || committed.TransferId != transferId ||
            committed.Generation != generation ||
            !string.Equals(committed.SourceClientId, sourceClientId, StringComparison.Ordinal))
            return false;
        if (committed.SourceStopAcknowledged) return false;

        _lastCommittedTransfer = committed with
        {
            SourceStopAcknowledged = true,
            SourceStoppedAt = stoppedAt
        };
        if (_remotePlaybackDevices.TryGetValue(sourceClientId, out var sourceDevice))
        {
            _remotePlaybackDevices[sourceClientId] = sourceDevice with
            {
                State = sourceDevice.State with
                {
                    IsPlaying = false,
                    Status = sourceDevice.State.PositionMs > 0 ? "In Progress" : "Paused",
                    UpdatedAt = stoppedAt,
                    Revision = Interlocked.Increment(ref _webRevision)
                },
                IsOwner = false
            };
        }
        return true;
    }

    public WebPlaybackTransferResult CancelPlaybackTransfer(WebPlaybackTransferCancelRequest request)
    {
        bool changed;
        lock (_playbackGate)
            changed = _playbackTransfers.Cancel(request, DateTimeOffset.UtcNow);
        return new WebPlaybackTransferResult(changed, false,
            changed ? "Playback move cancelled; the original device was left unchanged." : "Playback move was already inactive.",
            null, GetPlaybackSession());
    }

    private WebPlaybackTransferResult TransferFailure(bool conflict, string message)
        => new(false, conflict, message, null, GetPlaybackSession());

    private PlaybackTransferAuthority CreateTransferAuthority(
        WebPlaybackState desktop,
        DateTimeOffset now)
    {
        var ownerDevice = _playbackOwnerDevice;
        var ownerClientId = IsServerPlaybackOwner(ownerDevice) ? "server" : _playbackOwnerClientId;
        WebPlaybackState state;
        if (IsServerPlaybackOwner(ownerDevice)) state = desktop;
        else if (!string.IsNullOrWhiteSpace(_playbackOwnerClientId)) state = _webPlayback;
        else if (_webPlayback.EpisodeId.HasValue) state = _webPlayback;
        else state = desktop;

        return new PlaybackTransferAuthority(
            ownerDevice,
            ownerClientId,
            _playbackSessionGeneration,
            state.EpisodeId,
            state.PositionMs,
            state.DurationMs,
            state.Speed,
            state.IsPlaying,
            state.UpdatedAt ?? now);
    }
}
