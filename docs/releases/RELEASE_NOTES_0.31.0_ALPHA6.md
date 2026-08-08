# Radio Vault v0.31.0 Alpha 6 Buildfix 1

Version: `0.31.0-alpha6-di-tests-cleanup-buildfix1`

Alpha 6 Buildfix 1 retains the accepted final Core Hardening alpha and repairs remote-client Now Playing parity. The defect predated Alpha 6: Now Playing used the remote client’s isolated database for broadcast knowledge even though Broadcast Info correctly used the authoritative server API.

## Buildfix 1 changes

- Remote Now Playing branches away from local `DatabaseService.GetBroadcastKnowledge` before reading details.
- Synchronized Library fields provide immediate and cached read-only show/date/headline/summary/people/topic presentation.
- The live server broadcast-details endpoint supplies station, slot, multipart identity, role-separated people, topics and archive notes.
- Server artwork is loaded through the existing certificate-specific lazy artwork cache.
- Per-request cancellation and generation checks prevent stale details or artwork after rapid episode changes, mode switching or shutdown.

## Retained Alpha 6 changes

- Explicit singleton/transient service lifetimes.
- Lazy singleton factories and reverse-order singleton disposal.
- Duplicate-registration and dependency-cycle protection.
- Required-service startup report and frozen runtime registrations.
- Composition diagnostics written during startup.
- Platform-neutral local playback-engine factory with a Windows implementation.
- Application-owned playback-session factory.
- Explicit remote Library session disposal during ordered shutdown and mode switching.
- Expanded composition, lifecycle and playback-construction regression coverage.
- Updated release-gate and package architecture identities.

There are no database, LAN protocol, API, pairing, cache or intentional interface changes.
