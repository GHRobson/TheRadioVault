# Radio Vault v0.32.0 Alpha 12 Buildfix 4 Buildfix 2

## Playback start and Connected Access stability

Buildfix 4 established the transactional prepare–verify–commit handoff protocol. Real-device testing of Buildfix 4 Buildfix 1 exposed two regressions around that protocol rather than a failure of its durable-progress safeguards:

1. An explicit Play gesture could inherit the shared session's previous paused state. The decoder and ownership transaction could therefore complete correctly while the target remained paused, presenting as an indefinitely loading player with no audio.
2. The Avalonia remote client created a new certificate-pinned HTTP handler for each rapid playback-session request. Handoff polling, library synchronisation and media preparation could churn TLS connections and sockets, causing the Connected Access indicator to fall back to orange/cached mode even while some stale session state remained visible.

This buildfix keeps the transactional handoff design and repairs those surrounding playback/transport paths.

### Desktop and laptop

- An explicit Play gesture remains authoritative. A matching shared playhead and speed are reused, but an old paused snapshot no longer cancels the user's request to play.
- A transactional move is created only when another active device genuinely owns playback. Starting a fresh session, or resuming on the current owner, no longer takes the muted transfer path.
- Handoff snapshot refreshes are single-flight and run once per second rather than allowing overlapping half-second requests.
- Certificate-pinned LAN clients now share a bounded `SocketsHttpHandler` pool per server certificate and address. Existing per-request `HttpClient` disposal no longer destroys the underlying TLS connection pool.
- The committed target restores the volume captured at the beginning of the operation and repeatedly verifies that its playback engine actually enters Playing before the move is presented as audible.
- The transfer's source-stop, generation and durable-progress protections remain unchanged.

### Radio Vault Anywhere

- Tapping Play or Move to this device explicitly requests playing output instead of inheriting a stale paused shared snapshot.
- After ownership commits, the phone unmutes and verifies that Safari's decoder is genuinely running, with a bounded retry window.
- Global player polling is serialised: one player refresh per second and one change-stream refresh every three seconds. Explicit transfer stages retain their tighter bounded polling.
- Page-exit playback reporting now uses the canonical logical multipart position and duration together with the current ownership generation, rather than the physical time of one media part.
- The application-shell cache advances from v34 to `radio-vault-anywhere-shell-v35` so installed phones receive the corrected JavaScript.

### Preserved identities

- Database schema: **45**
- LAN capability generation: **15**
- API version: **v1**
- Desktop web-shell generation: **11**
- IndexedDB and downloaded audio/artwork cache identities are unchanged.
