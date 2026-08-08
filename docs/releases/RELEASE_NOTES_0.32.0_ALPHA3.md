# Radio Vault v0.32.0 Alpha 3

Alpha 3 makes the Avalonia default shell locally listenable for the first time.

## Highlights

- Real canonical local playback from Dashboard and Library.
- Live persistent Now Playing bar.
- Play/pause, seek, skip, volume and 0.5×–3× speed controls.
- Resume position, play count, speed and natural completion persistence.
- Automatic multipart transitions with one logical playhead.
- Global rubber-band overscroll on Avalonia scroll surfaces.
- Reduced-motion-aware elastic animation.

## Architecture

The Avalonia shell uses the existing platform-neutral playback session, progress and completion coordinators. A new service boundary owns canonical media resolution and listening-state persistence. NAudio is confined to the Avalonia platform edge; no WPF playback type is referenced.

## Compatibility

Database schema 45, LAN capability generation 14, API v1 and all pairing/cache identities are unchanged. The complete WPF reference remains available for workflows not yet migrated.
