# Radio Vault v0.30.0 Alpha 5 Buildfix 3 — launch-time speed selector repair

Buildfix 2 compiled, but the application crashed while `MainWindow.InitializeComponent()` was loading. The 1.0× item in `SpeedCombo` is selected in XAML, so WPF raises `SelectionChanged` before the constructor has created `_playback` or the database-backed player state. The handler immediately attempted `_playback.Speed = speed`, causing the reported `NullReferenceException`.

Buildfix 3 makes the existing handler ignore selection changes until the window is fully loaded. Normal user speed changes after launch continue to update the playback engine, player state and persisted progress exactly as before.

Preserved invariants:

- the ordinary `MainWindow` remains the only remote-client shell in both local and remote-client modes;
- Buildfix 2's `LanRemoteTranscriptSummary` contract remains present;
- database schema remains 45;
- LAN capability generation remains 11;
- web-shell generation remains 10;
- Anywhere shell/audio/artwork cache identities remain unchanged;
- Alpha 4 `.trvstate` migration remains unchanged.
