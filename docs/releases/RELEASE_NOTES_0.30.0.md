# Release Notes — Radio Vault v0.30.0

Version: `0.30.0`

Radio Vault v0.30.0 completes **Multi-Device Library Access**. One authoritative Radio Vault installation can securely share its library and application services with trusted Windows clients across a private LAN, while preserving one canonical database and one source of truth.

## Highlights

- Secure LAN discovery, pairing, per-client credentials and certificate-pinned HTTPS.
- The normal Windows application can operate against either its local library or a paired Radio Vault server.
- Full remote browsing and read parity across Dashboard, Library, Explore, search, Broadcast Info, artwork, transcripts, Moments and Research.
- Remote playback with shared progress, speed, favourites, listening status and queue state.
- Server-backed Metadata Studio editing with explicit guards around server-owned maintenance operations.
- Encrypted server-specific cache and a clear read-only mode during outages.
- Efficient incremental synchronization with safe full resets for structural library changes.
- Detailed Connected Access parity and synchronization diagnostics.
- Safe local/remote mode switching and hardened shutdown with bounded final progress persistence.

## Stable promotion

The stable release preserves the successfully built and user-accepted RC1 implementation. There is no new database migration, API route, LAN capability or cache reset. Changes are limited to final version identity, release documentation and packaging metadata.

## Compatibility

- Database schema: **45**.
- LAN capability generation: **14**.
- API: **v1**.
- Web-shell generation: **10**.
- Anywhere shell cache: **v33**.
- IndexedDB: **v2**.
- Audio/artwork caches: **v1**.

Existing RC1 and Beta 1 installations should open directly without re-adoption, re-pairing or cache clearing.
