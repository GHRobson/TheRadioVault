# Radio Vault v0.32.0 Alpha 12 Buildfix 3

Build identity: `0.32.0-alpha12-buildfix3-contextual-handoff-tight-playhead`

## Purpose

This buildfix restores the concise WPF handoff interaction and removes the initial period in which remote playback controls could appear disabled. It also makes all endpoints render the shared playhead from the same timestamped server state, with frequent lightweight projection between authoritative heartbeats.

## Contextual transport control

- While this device owns playback, the centre control is the normal Play/Pause button.
- While another phone, laptop or server endpoint owns playback, the same control becomes the WPF arrow-into-desktop **Move to this device** icon.
- The separate Continue on this device button is removed from the Avalonia interface.
- The persistent bottom player and full Now Playing page use the same ownership state and command.
- Skip, seek, stop and speed controls remain restricted to the owner so a dormant decoder cannot alter the shared session.

## Immediate control during opening

- The centre control remains enabled while a server-hosted source resolves and buffers.
- Pressing it during opening records the desired play/pause state immediately.
- When opening completes, the decoder obeys the latest user intent rather than an older state captured at the beginning.
- A failed ownership claim restores the Move to this device state and displays the error instead of leaving controls disabled.

## Tight shared playhead

- Avalonia endpoints report authoritative logical position, duration, speed and play/pause state every second.
- Ownership snapshots are refreshed every second.
- Non-owning endpoints project the timestamped server position every 250 ms while playback is running.
- Projection is bounded by duration and disabled for stale states, preventing an abandoned heartbeat from advancing indefinitely.
- Transfers start from the projected position at the instant of the claim and preserve whether playback was playing or paused.
- The phone uses a 600 ms heartbeat threshold, one-second session polling and 250 ms inactive-output rendering.
- Durable listening-progress saves remain on their existing guarded cadence; smooth rendering does not multiply database writes.

Normal cross-device variance should be close to one second under ordinary LAN conditions, plus any unavoidable decoder-opening or network-buffer delay. The server remains authoritative and stale writers remain blocked.

## Compatibility

- Database schema: **45**
- LAN capability generation: **14**
- API: **v1**
- No database migration or Library Truth re-adoption is required.
- All Alpha 12 Research/interface features and Buildfix 1/2 repairs are retained.
