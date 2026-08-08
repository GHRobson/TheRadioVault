# Radio Vault v0.31.0 Alpha 5 — LAN and Shared-Session Consolidation

Version: `0.31.0-alpha5-lan-shared-session-consolidation`

Alpha 5 moves the remote Library synchronization lifecycle behind a platform-neutral application coordinator while preserving the accepted Alpha 4 experience.

## Changes

- Adds shared ownership of the remote cursor, request gate, timeout/cancellation, retry policy, connection state and diagnostics.
- Uses one synchronization lease for initial connection, cached update, routine polling and forced refresh.
- Makes cached startup, manual reconnect and shutdown part of the same session lifecycle.
- Removes WPF-local remote synchronization semaphore, cancellation source, cursor, retry and diagnostic state.
- Adds regression and architecture guards for the new boundary.

## Compatibility

Database schema 45, LAN capability generation 14, API v1, pairing, encrypted remote-client caches and all web/cache identities are unchanged. No migration, re-adoption, re-pairing or cache reset is required.
