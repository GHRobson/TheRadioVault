# Radio Vault v0.32.0 Alpha 6 Buildfix 1

Build identity: `0.32.0-alpha6-buildfix1-connected-access-route-import`

Buildfix 1 corrects the Windows compile failure in the Avalonia-linked encrypted remote-library cache by adding the explicit web-contract import that the shared source requires. It also resolves the two nullable warnings reported in the same build. No Connected Access behaviour, database schema, LAN contract, API route, pairing, certificate or cache identity changes.

Alpha 6 brings Connected Access and remote-library parity to the Avalonia default desktop. It adds LAN discovery and certificate-pinned pairing, live and encrypted cached server startup, automatic/manual reconnect, remote Dashboard and Library browsing, server artwork, canonical remote playback, progress/favourites/Queue/Moments write-through, normal Research read parity and supported server metadata editing.

Remote-client mode is composed before local database startup and is guarded against every local SQLite fallback. Cached sessions are read-only, advanced Research decisions remain authoritative-server operations, and pending playback progress is retried after reconnection.

The accepted Alpha 5 Research/Metadata Studio and Alpha 4 Library/playback/Queue/Moments interaction baseline remains intact. Database schema stays at 45, LAN capability generation stays at 14, and API v1, pairing, certificate and cache identities are unchanged.
