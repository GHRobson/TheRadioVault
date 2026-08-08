# Radio Vault v0.26.0 — Archivist

## Stable release

v0.26.0 promotes the accepted RC1 code to the stable Archivist release. There are no feature, playback-state or database-schema changes from RC1. The final promotion updates release metadata, documentation and the secure web shell cache identifier.

## Research Library and data safety

- Permanent research records survive missing, renamed or replaced audio.
- Transactional pack analysis and import retain durable per-field decisions and import history.
- Whole-import and per-broadcast rollback are guarded against overwriting later manual edits or research decisions.
- Broadcast Info and research views expose active provenance.
- Missing-broadcast research can be reviewed and reconciled when matching audio is later added.
- Duplicate-pack protection and responsive large-pack progress reduce risky or confusing imports.

## Archive integrity and responsiveness

- Unchanged rescans avoid unnecessary tag, artwork and fingerprint work.
- Rescans preserve researched people and other enriched metadata.
- Long-running archive and research operations report progress without freezing the WPF shell.
- Database schema 33 contains the import ledger, guarded rollback records and provenance structures.

## Radio Vault Anywhere

- Secure LAN web access with mobile Dashboard, Library, search, Broadcast Info, queue and archive information.
- Manual downloads for offline playback, seeking and resume.
- Forward-safe progress reconciliation when the phone reconnects.
- One authoritative playback session with one owner at a time.
- Stable PC↔phone transfer at the synchronized playhead, including paused ownership, speed, background playback, reconnect recovery and second-phone protection.
- Strict non-cacheable live byte-range delivery for iOS Safari.
- Failure-only playback diagnostics with Copy report and Retry controls.

## Release details

- Application version: `0.26.0`
- Database schema: `33`
- Secure offline shell cache: `radio-vault-anywhere-shell-v24`
- Upgrade path: existing schema-33 RC/preview databases open without a new migration

## Next milestone

Feature development proceeds to v0.27 Radio Vault Anywhere. Native iOS and the planned Avalonia desktop rebuild remain post-v1.0 work.
