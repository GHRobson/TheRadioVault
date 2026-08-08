# Radio Vault v0.30.0 Alpha 8

Build identity: `0.30.0-alpha8-cache-resilience-sync`

Alpha 8 makes the normal Radio Vault interface start quickly and remain useful when a remote client temporarily loses contact with its server.

## Persistent server Library cache

After the first complete synchronization, the remote client stores a compressed and AES-GCM-encrypted metadata cache. It contains canonical broadcast rows and display metadata, dashboard projections, queue state, Moments, transcript summaries and the last server Settings snapshot when available. It does not contain audio and it never uses the remote client's dormant local-library database.

Each cache is bound to the server instance ID, pinned certificate, API version and paired credentials. Invalid, oversized, mismatched or corrupted caches are ignored. Writes use an atomic temporary file and respect the configurable 16–256 MB limit.

## Immediate launch and live refresh

A later launch renders real cached Library content immediately, with an **Updating from server** state rather than an empty shell. The server supplies a synchronization session, retained change sequence and deterministic Library revision. The remote client receives only changed or deleted broadcasts when safe, and requests a complete reset when the journal is incomplete or server identity changes.

A six-second background check refreshes server changes without replacing the current page, filters or selected broadcast. Temporary failures use bounded retry backoff and leave the cached Library browseable and clearly marked as cached.

## Safe cached mode

Cached Library browsing, local filtering, search, Moments, transcript summaries and the last server archive/Settings snapshot remain available while the server is unreachable. Playback, progress, favourites, listened state, queue changes, Moment edits, metadata edits and research pack import/export require a live server and are never redirected to the local-library database.

## Playback recovery

When an active server stream fails, Radio Vault keeps the normal player in a buffering state and makes up to three bounded recovery attempts. It reloads the canonical media manifest and resumes from the last observed position. A playback failure is shown only after recovery is exhausted or the operation is not recoverable.

## Settings

Remote-client Settings now includes cache enablement, a size limit, stored size, last synchronization, current cache state, **Synchronize now**, and **Clear cached Library**.

## Compatibility

- Database schema: 45
- LAN capability generation: 13
- New capability: `lan.cache-sync`
- New route: `/api/v1/federation/library-sync`
- Server and remote client should both run Alpha 8.
