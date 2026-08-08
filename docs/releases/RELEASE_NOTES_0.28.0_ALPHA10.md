# Radio Vault v0.28.0-alpha10 — Guarded Library Truth Adoption

Version: `0.28.0-alpha10-guarded-adoption-buildfix1`

## Added

- First guarded command capable of adopting a rollback-verified Library Truth plan into the live desktop database.
- Schema 45 permanent canonical broadcasts, recordings, segments, coverage, episode mappings and adoption audit tables.
- Typed confirmation showing exact plan and conflict totals.
- Retained pre-adoption backup with foreign-key, integrity and fingerprint validation.
- SHA-256 sealing of the exact Library Truth shadow plan, per-broadcast operation ledger and field-policy ledger.
- Exact truth-plan, per-broadcast operation-signature and field-policy-signature comparison with the persisted rehearsal before staging, before commit and after commit.
- Fail-closed handling for interrupted `running` or `validating` adoption records, with the retained backup path surfaced for inspection/restoration.
- Safer desktop quiescence: web ingress is stopped before the background-job check, playback is paused rather than reset, and a safe pre-commit failure restores playback position, timer state and web-server state.
- Permanent structure/audit row-count verification before and after commit.
- Independent post-commit fingerprint, foreign-key and SQLite integrity verification.
- Fail-closed handling for commit-boundary uncertainty, including immediate shutdown and explicit backup-restoration instructions.
- End-to-end smoke coverage for schema upgrade and guarded commit behavior.

## Unchanged

- Alpha9 parser, canonical grouping, recording/coverage structure and conflict-policy outcomes.
- Library Truth export schema 6.
- The 15 review-recommended and 10 blocked broadcasts are not adopted.
- Unresolved metadata values remain preserved for later review.
- Audio files are not renamed, moved or deleted.

## Required first-run sequence

1. Back up the desktop metadata/database.
2. Build and run Alpha10.
3. Run one fresh Alpha10 adoption rehearsal. Existing Alpha9 rehearsals are readable but intentionally unsealed and cannot enable the live button.
4. Confirm the rehearsal completes with three 64-character seals, zero foreign-key violations, SQLite integrity `ok`, backup validation `ok`, and rollback verified.
5. Only then use **Adopt verified plan…**.
