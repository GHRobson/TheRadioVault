# Radio Vault v0.32.0 Alpha 12 Buildfix 5

## Playback and connectivity baseline recovery

Buildfix 5 restores ordinary playback as a fast, dependable path and makes Connected Access failures diagnosable from inside Radio Vault.

### Ordinary playback

- A normal Play command opens the selected local file or remote stream without first waiting for the shared handoff session.
- Audio is allowed to start before ownership is published to the server.
- A failed ownership/status publication is recorded and retried by the normal session loop; it cannot cancel audio that is already working.
- The explicit **Move to this device** control continues to use the transactional prepare–verify–commit handoff workflow.
- Play-count persistence no longer holds the transport loading state open after the decoder has started.

### Connected Access isolation

- Library synchronisation, media streaming, playback/session traffic and maintenance operations use separate certificate-pinned connection pools.
- A stalled handoff poll or media request cannot consume the Library synchronisation pool.
- Library live/cached transitions, synchronisation timing and failure reasons are added to the runtime diagnostic journal.
- Remote playback ownership expires after a bounded missed-heartbeat lease, preventing an abandoned device from blocking later playback.

### Dashboard responsiveness

- Dashboard overview loading no longer waits for sequential On This Day topic/detail enrichment.
- The global loading bar clears when the usable Dashboard overview is ready; individual cards enrich in the background.

### Connected playback diagnostics

Settings → Advanced now provides Quick and Full stress tests on both the server and remote laptop.

The runner checks:

- application/device environment;
- server reachability and capability generation;
- live Library overview and candidate selection;
- shared playback-session availability;
- canonical media resolution;
- muted isolated decoder open, seek, Play and Pause;
- transactional handoff prepare, decoder-ready and safe cancellation;
- concurrent Library/session activity and repeated media opens in stress mode;
- confirmation that diagnostic operations did not alter listening position or completion state.

Reports export as `.trvdiag` ZIP archives containing a concise summary, structured report and bounded runtime event timeline. Pairing secrets, tokens, certificate material and ordinary local file paths are excluded or redacted. Enter the same session code on both machines to correlate reports.

### Protocol identity

- Database schema: **45**
- LAN capability generation: **16**
- API: **v1**
- Radio Vault Anywhere cache: **radio-vault-anywhere-shell-v36**
- Desktop web-shell generation: **11**

No Library migration or re-adoption is required.
