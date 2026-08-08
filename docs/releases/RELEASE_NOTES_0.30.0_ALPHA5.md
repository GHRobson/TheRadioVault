# Radio Vault v0.30.0 Alpha 5 — Server / Remote-Client Shell

> **Superseded implementation:** The separate remote-client shell described below was rejected after runtime review. Alpha 5 Buildfix 1 replaces it with the normal `MainWindow` and the same local-library GUI.


Alpha 5 is the first build intended to let the remote client operate primarily as a client of the server's Radio Vault server library.

## New

- Optional remote-client startup on a remote client.
- Native server Dashboard, Library, favourites and queue workspaces.
- Server-side Library search and show/year/listening-state filters.
- Native broadcast information with people, topics, notes and research sources.
- Remote Moment creation/deletion and timed transcript reading.
- Session-only and persistent local-library recovery controls.
- `--local-library` emergency launch override.
- `lan.full-shell` capability; LAN capability generation 11.

## Preserved

- Alpha 3 certificate-pinned canonical multipart playback and write-through.
- Alpha 4 guarded `.trvstate` personal-state migration.
- The remote client's local database and audio archive as an untouched fallback.
- Database schema 45, Anywhere API v1, web-shell generation 10, shell cache v33, IndexedDB v2 and v1 audio/artwork caches.

## Important testing note

Upgrade the server first. Test the saved remote-client connection before enabling primary-shell startup. The client deliberately does not silently fall back to its local library when the server is offline; use the visible recovery control or `--local-library` so two writable libraries are never confused.
