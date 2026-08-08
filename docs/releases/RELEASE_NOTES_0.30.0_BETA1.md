# Radio Vault v0.30.0 Beta 1

Build identity: `0.30.0-beta1-multi-device-hardening`

Beta 1 promotes the accepted Alpha 9 server/remote-client feature set into the first Multi-Device Library Access beta. This build is a feature freeze: it adds hardening, performance evidence and soak diagnostics without adding a new application surface or changing the database/API compatibility boundary.

## What changed

### Smaller remote-client updates

The server synchronization journal already sends bounded changed-broadcast deltas, but the WPF client previously rebuilt its entire in-memory Library presentation whenever any change arrived. A five-second listening-progress save could therefore cause thousands of row objects, collection summaries and filters to be reconstructed repeatedly during a normal listening session.

Beta 1 now applies routine same-identity deltas to the existing row objects. Progress, duration, status, favourite state, headline, summary, people, topics, last-played time and queue/dashboard state refresh in place. Full reconstruction remains mandatory when the server session changes, the journal cannot safely continue, a broadcast is added or removed, or its show/date moves between facets.

### Synchronization diagnostics

The copied **Remote-client parity diagnostics** report now includes:

- last synchronization timestamp and duration;
- revision-check, incremental-delta, full-reset or failed mode;
- changed-row count;
- consecutive failure count and next retry time;
- the last bounded synchronization error.

Changed or unusually slow synchronizations are also written to `diagnostic.log` on both sides with bounded event/row counts, without logging every healthy six-second no-change poll.

### Shutdown and session evidence

The Alpha 9 Buildfix 2 shutdown ordering, two-second final remote progress bound and 12-second final watchdog are retained. Beta 1 extends the privacy-safe session marker to both operating modes and records the last shutdown stage reached. If the watchdog ever has to terminate a stuck process, the next launch and diagnostic export can identify whether the stall occurred during timers, LAN monitoring, server synchronization, listening-progress persistence, web-server stop, event cleanup, background jobs, playback disposal or final session completion.

### Safe local/remote window transitions

The local-library and remote-client commands replace the main WPF window inside the same process. Beta 1 now distinguishes that handoff from a genuine application exit. The outgoing window completes its own session marker but does not arm the final process watchdog, overwrite the replacement window’s new session marker or stop a local web server that the replacement window has just started. This prevents a successful mode switch from being terminated 12 seconds later and keeps local Connected Access available after returning from remote-client mode.

## Compatibility boundary

- Database schema: **45**
- LAN capability generation: **14**
- API: **v1**
- Web-shell generation: **10**
- Anywhere shell cache: **v33**
- IndexedDB: **v2**
- Downloaded audio/artwork cache identities: **v1**

No library migration, re-adoption, cache reset or re-pairing is expected.

## Validation status

Static source/package validation is included. A real Windows `Release | x64` build and the server/remote-client soak checklist remain required on the user's machines.
