# Radio Vault v0.30.0 Alpha 7 Buildfix 1

Alpha 7 compiled and loaded the server library, but the first attempt to open a remote broadcast could show a WPF thread-affinity error even though the media stream itself had opened. Pressing Play afterwards could still start the audio because the underlying failure was a false UI-state failure rather than a server or media error.

## Correction

`LanFederationPlaybackEngine.OpenCoreAsync` continued after the asynchronous server-manifest request on a worker thread. Its state notification constructed a playback snapshot immediately, which read `MediaPlayer.Position` and `MediaPlayer.Volume`. WPF owns those objects on the UI dispatcher and rejected the cross-thread access.

Buildfix 1 captures the player-owning dispatcher and routes every WPF media operation and playback snapshot through it. Server requests continue asynchronously, but completion can no longer touch the media object directly. The false **Playback failed** dialog is removed while the Alpha 7 spinner, buffering monitor and play/pause reconciliation remain unchanged.

## Terminology

Current user-facing language now uses two role names consistently:

- **Server** — the Radio Vault installation that owns the library, database and archive files.
- **Remote client** — a trusted Radio Vault installation connected to that server.

Device-specific role wording has been removed from the current interface, Settings, errors, diagnostics and v0.30 documentation. Internal compatibility identifiers are unchanged.

Database schema remains **45** and LAN capability generation remains **12**.
