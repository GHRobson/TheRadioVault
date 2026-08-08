# Radio Vault v0.31.0 Alpha 4 — Playback and Media Boundary Cleanup

Version: `0.31.0-alpha4-playback-media-boundary-cleanup`

Alpha 4 moves live playback orchestration behind a platform-neutral application session while preserving the accepted Alpha 3 behaviour.

## Changes

- Added `PlaybackSessionCoordinator` as the owner of local and remote playback-engine commands, state events and disposal.
- Added `PlaybackProgressCoordinator` so local recovery, database persistence and remote write-through share one protected progress plan.
- Added `PlaybackCompletionCoordinator` so only natural forward listening can satisfy completion-count evidence.
- Moved the ordinary WPF `MediaPlayer` backend to `TheRadioVault.Platform.Windows` as `WpfMediaPlaybackEngine`.
- Removed the concrete playback-engine field and direct playback-engine command path from `MainWindow`.
- Added an adapter capability for LAN progress synchronisation without exposing the concrete LAN playback engine to presentation code.
- Added regression tests for session command ownership, transient-zero protection and natural completion.
- Strengthened architecture validation and coupling reporting for the playback boundary.

## Compatibility

No database, library, pairing, LAN protocol, cache or UI migration is introduced. Schema remains 45 and LAN capability generation remains 14.
