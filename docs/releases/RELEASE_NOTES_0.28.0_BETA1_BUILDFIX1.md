# Radio Vault v0.28.0-beta1-buildfix1

## Critical fix: listening progress persistence

- Prevents transient zero positions from silently erasing positive listening progress.
- Shares playback state across all legacy episode members of a canonical broadcast.
- Forces a final progress flush during normal application shutdown.
- Adds an atomic recovery journal for crash, blocked-writer and interrupted-close recovery.
- Resolves recovery entries by episode ID, canonical key or stable broadcast UID after build/rescan identity changes.
- Keeps explicit **Mark unplayed** as the only route that may intentionally reset progress to zero, applying it consistently across canonical member rows.

## Diagnostics

- Playback-inclusive Archive Health exports now include up to 100 recent playback states.
- Diagnostic format increases from 5 to 6; database schema remains 45.

## Unchanged

- Beta 1 Research decision cards and exact metadata navigation.
- Asynchronous Advanced diagnostics loading.
- Canonical media, transcript, web/offline and Library Truth formats.
- No audio files are modified.
