# Beta 2 — data safety and performance hardening

Version: `0.28.0-beta2`

## Changes

- Radio Vault writes a tiny session marker at startup and marks it clean only after orderly shutdown.
- A later launch can identify a crash or forced close and explicitly check the playback recovery journal.
- Diagnostic exports now include privacy-safe session and recovery summaries.
- Moment browsing is read-only. Conservative duplicate repair runs once during startup maintenance instead of beginning a write transaction every time a Moment list is opened.
- Existing canonical progress recovery, zero-overwrite protection, shutdown flush, canonical-member progress persistence, and idempotent Moment creation remain unchanged.

## Acceptance checks

1. Normal close and relaunch preserves progress and records a clean prior session.
2. Force-close during playback, relaunch, and confirm the last positive position is restored.
3. Open Moments repeatedly and confirm it remains responsive and no duplicates return.
4. Export diagnostics with playback enabled and confirm `playbackRecovery` and `sessionGuard` are present.
5. Exercise Research, Advanced diagnostics, Archive Settings, web transfer, multipart playback, backup and restore.
6. Confirm schema remains 45 and no migration is offered.
