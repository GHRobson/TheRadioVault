# Radio Vault v0.28.0-alpha13 — Canonical Playback and Transcript Cutover

Version: `0.28.0-alpha13-canonical-playback-transcript-cutover`

Alpha 13 makes the Alpha 12 media contract consumable across desktop playback, transcript navigation, web streaming and offline clients.

## Implemented

- Recording-specific canonical playback plans remain selected for the active broadcast and are rejected when incomplete or review-required.
- Transcript segment timestamps are translated from their source physical file into the assembled canonical broadcast timeline.
- Versioned web APIs expose deterministic canonical media manifests and range-capable per-part streams.
- Web/offline clients can download every ordered part while retaining logical start/end offsets and one broadcast-level progress identity.
- Multipart part streams are resolved only from the manifest, preventing arbitrary media-file access.
- Moments and desktop seeks continue to use the same logical timeline already introduced by Alpha 11/12.

## Safety

- Missing or review-required parts fail closed.
- Held Library Truth groups retain deterministic legacy fallback.
- Recording selection does not rewrite Library Truth or duplicate listening state.
- No audio file is renamed, moved, deleted or rewritten.

Schema remains 45.
