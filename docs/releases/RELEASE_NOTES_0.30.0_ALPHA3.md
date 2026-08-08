# Radio Vault v0.30.0 Alpha 3 — Remote Playback & Write-Through

Alpha 3 turns the accepted server Library browser into the first interactive Radio Vault remote client. A remote client can now stream canonical broadcasts from the server, follow multipart timelines, seek and resume, and write shared state back to the server database.

## New

- Certificate-pinned, token-authenticated LAN playback.
- Loopback-only WPF media bridge with HTTP range forwarding.
- Canonical multipart transitions and cross-part seeking.
- Progress synchronisation that preserves intentional LAN-client rewinds.
- Favourite, listened-state and queue write-through.
- Compact accessible title-bar connection icons.

## Preserved

- Local client database and media remain untouched as fallback.
- Database schema 45.
- API v1.
- Web-shell generation 10.
- Anywhere shell cache v33.
- IndexedDB v2 and v1 audio/artwork caches.

Capability generation advances to 10. A Windows build and real two-PC acceptance pass are required before Alpha 3 is accepted.
