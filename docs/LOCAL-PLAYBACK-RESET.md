# Local Playback Reset Architecture

The Avalonia desktop composition is intentionally local-only in this milestone.

## Active runtime

- `SqliteDatabase`
- `LibraryBrowseService`
- `LibraryActionService`
- `LocalPlaybackLibraryService`
- `NAudioPlaybackEngine`
- local queue, Moments, Research and metadata services
- local archive health, backup and direct Library scanning

## Detached runtime

- LAN federation preferences and bootstrap
- remote Library store/cache
- pairing and certificate management
- web/server hosting
- remote media proxy and streaming
- playback leases, device heartbeats and transactional handoff
- connected-playback stress diagnostics

## Compatibility placeholders

A few presentation interfaces still receive local-only placeholder services so the mature Settings and shell view models do not need a risky broad rewrite. Those placeholders do not open sockets, discover servers, load pairing credentials, create remote caches or publish device state.

## Frozen reference source

`TheRadioVault.Web` and older federation source remain in the repository as historical reference only. The active Avalonia project has no project reference to `TheRadioVault.Web` and no linked federation/server source files. The local-only solution and build scripts do not compile those projects.

Future networking must return as independent stages: health-only connection, read-only paginated Library, media streaming, progress write-through, cache/reconnect, web client, then handoff.
