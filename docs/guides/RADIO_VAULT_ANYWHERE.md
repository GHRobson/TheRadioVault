# Radio Vault Anywhere architecture

Radio Vault Anywhere makes the mature archive available beyond the local WPF shell while preserving one authoritative database and media library.

## Client contract

Every client first uses `/api/v1/server-info` or the embedded server object in `/api/v1/bootstrap`. Clients inspect the capability list rather than assuming optional features exist. Bootstrap is a bounded startup snapshot; normal operation then uses resource, mutation and change-feed endpoints.

## Authority rules

- The desktop-hosted Radio Vault instance owns the database and physical media.
- Clients receive canonical broadcast identities and canonical media manifests, never arbitrary filesystem paths.
- Playback, queue, favourites and progress mutations remain explicit application-service operations.
- Stale playback revisions and competing phone leases fail closed.
- Offline progress can advance or complete a broadcast but cannot silently rewind newer server progress.
- Device-local downloads are visibly distinct from the complete authoritative library.

## Web application state

The PWA stores only client identity, navigation/filter state, privacy-safe diagnostics, download metadata and offline progress. Connected Dashboard data comes from the bootstrap contract. A temporary server loss must preserve navigation and playback context rather than resetting the application.

## Alpha sequence

1. **Alpha 1 — Anywhere foundation:** server identity, capability discovery and bootstrap contract.
2. **Alpha 2 — web app cutover:** bootstrap-driven shell, canonical ID use, server-side date/status facets, responsive navigation and reconnect-safe state.
3. **Alpha 3 — offline storage hardening:** storage inspection, damaged-download verification/repair, safe cache migration and sync conflict presentation.
4. **Alpha 4 — web feature parity:** transcript reading/search, Moment creation/editing and remaining desktop-to-web workflows.
5. **Beta — real-device regression:** iPhone/iPad/desktop-browser soak, performance and failure recovery.

The database schema remains 45 until persisted federation state genuinely requires a migration.

## Beta 2 resilience rules

- Canonical Library queries are paged and report total counts. Clients should never assume one response contains every match.
- Offline mutations retain one stable mutation ID across retries. A successful mutation may be acknowledged again as a duplicate, but must not be applied twice.
- Retryable failures back off; validation/conflict failures stay blocked until the user retries or discards them.
- Device downloads are healthy only when their IndexedDB source blob exists and the matching Cache Storage response can be restored.
