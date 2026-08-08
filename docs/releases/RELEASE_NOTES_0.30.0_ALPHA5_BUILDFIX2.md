# Radio Vault v0.30.0 Alpha 5 Buildfix 2 — transcript contract compile repair

Buildfix 1 failed compilation with CS0246 because `LanFederationConnectionService.LoadTranscriptsAsync` returned `LanRemoteTranscriptSummary`, but the corresponding public client record had not been declared.

Buildfix 2 adds that missing contract with the exact fields already mapped by the server transcript endpoint and consumed by the existing Transcripts page. No LAN protocol, database, UI, playback or state-migration behaviour changes.

Preserved invariants:

- the ordinary `MainWindow` remains the only remote-client shell in both local and remote-client modes;
- database schema remains 45;
- LAN capability generation remains 11;
- web-shell generation remains 10;
- Anywhere shell/audio/artwork cache identities remain unchanged;
- Alpha 4 `.trvstate` migration remains unchanged.
