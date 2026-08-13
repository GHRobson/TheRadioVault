# Radio Vault v0.29.0 Beta 1 — Anywhere Integration & Hardening

Version: `0.29.0-beta1-anywhere-integration-hardening`

Beta 1 freezes major feature work and hardens the accepted Alpha 4 Anywhere experience as one integrated product.

## Changes

- Structured privacy-safe diagnostics can be exported from the Anywhere diagnostics sheet as JSON.
- Reports include app/server/API/schema/capability versions, coarse connectivity state, current workspace type, active-filter count, download and pending-sync counts, playback ownership/source, reduced-motion/standalone state, bounded browser-navigation timings and the existing redacted event history.
- Reports intentionally exclude tokens, URLs, filenames, paths, broadcast titles, summaries, people, topics and archive content.
- Existing copy, clear-history and connection-check actions remain available.
- Capability generation is 5.
- Secure shell cache is `radio-vault-anywhere-shell-v30`; audio and artwork caches remain `v1`.
- Database schema remains 45.

## Beta acceptance focus

Test desktop startup, iPhone bootstrap and reconnection, compact Library filters, canonical multipart playback, offline downloads, sync-journal replay, Moments, transcript seeking, cache upgrades, accessibility and full-library responsiveness.
