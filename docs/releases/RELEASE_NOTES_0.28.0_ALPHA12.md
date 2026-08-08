# Radio Vault v0.28.0-alpha12 — Canonical Media Manifests

Version: `0.28.0-alpha12-canonical-media-manifests`

Alpha 12 extends the Alpha 11 canonical-library cutover below the broadcast level. The preferred recording is no longer the only recording shape the application can describe: services can enumerate every recording variant, request a safe playback plan for a specific recording, and generate a deterministic multipart download manifest.

## Added

- Canonical recording-option projection with role, duration, segment/file counts, preferred status, completeness, and review state.
- Explicit recording-key playback-plan lookup, while retaining preferred-recording behaviour as the default.
- Canonical download manifests containing ordered parts, logical offsets, chosen physical sources, storage state, and total byte size.
- Database-service APIs for desktop, web, transcript, offline, and future LAN clients to consume the same canonical media contract.

## Safety rules

- A manifest is emitted only when every ordered segment has a non-missing physical source.
- Coverage rows requiring review are not silently promoted into playable multipart plans.
- The preferred recording remains unchanged; Alpha 12 selection is non-destructive and does not rewrite Library Truth.
- The 25 held groups retain Alpha 11's deterministic fallback when no safe complete recording plan exists.
- Audio files are not renamed, moved, deleted, or rewritten.

Schema remains 45.
