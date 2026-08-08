# Radio Vault v0.28.0-alpha7 — Transactional Adoption Rehearsal

Alpha7 turns the confirmed alpha6 adoption preview into an executable but disposable database transaction.

## Added

- True pre-rehearsal online SQLite backup created before the live shadow ledger is written.
- Independent backup-open and integrity validation.
- Disposable working database copied from the validated backup.
- Strict preview preconditions: every eligible plan must contain its survivor and the exact persisted episode membership.
- Future canonical broadcast, recording, segment, coverage and episode-map tables created inside the rehearsal transaction.
- Media-file reassignment and mapped alias retirement rehearsal for adoption-ready broadcasts only.
- Exact per-broadcast checks that actual recordings, segments, coverage rows, linked files, file reassignments and aliases match alpha6's persisted preview counts.
- Lossless playback-state aggregation and migration of Moments, queue entries, tags, guests and research references.
- Explicit metadata and multiple-transcript policy-conflict reporting.
- Foreign-key and integrity checks before rollback.
- Deterministic logical fingerprint before and after rollback.
- Persisted rehearsal run and per-broadcast evidence tables.
- Rehearsal Results tab and alpha7 export schema 5.

## Full-corpus structural expectation

The accepted alpha6 export predicts 4,305 canonical writes, 6,685 recording writes, 7,035 segment writes, 7,035 direct coverage writes, 2,728 media-file reassignments and 2,728 mapped alias retirements. A packaging-time synthetic reconstruction completed all of those operations with zero mismatches and zero foreign-key violations before rollback.

## Still disabled

- No live adoption command.
- No audio move, rename, quarantine or deletion.
- Review-recommended and blocked broadcasts remain held.
- Transcript and metadata conflicts are reported rather than silently resolved.
