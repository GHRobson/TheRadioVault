# Radio Vault v0.29.0 Beta 2 — Soak-Test Hardening & Performance

Version: `0.29.0-beta2-soak-hardening-performance`

Beta 2 takes the accepted Beta 1 integration build and hardens the failure paths most likely to appear during multi-day iPhone/desktop use.

## Changes

- The server returns deterministic Library pages with `offset`, `limit`, `total` and `hasMore`; the iPhone initially renders 80 cards and appends more only when requested.
- Offline mutations carry a stable mutation identifier through the first connected attempt and every journal replay. The server keeps a bounded in-memory duplicate ledger, while queue additions are also semantically deduplicated in persistent application state.
- Retryable network/server failures use exponential backoff. Non-retryable HTTP failures remain visible as blocked changes instead of being marked synced.
- The compact sync sheet exposes **Retry failed** and **Discard failed** only when intervention is required. Discarding blocked progress preserves its local resume position.
- Download storage is audited without hashing entire audio files: missing or mismatched Cache Storage entries are rebuilt from the IndexedDB source blob, while missing source audio is marked **Needs repair** and can be re-downloaded.
- Diagnostics now report downloaded repair count, pending/blocked sync counts and bounded Library-page state without archive content.
- Capability generation is 6. The secure shell cache is `radio-vault-anywhere-shell-v31`; audio and artwork caches remain `v1`.
- Database schema remains 45 and IndexedDB remains version 2 because no new object store is required.

## Beta 2 acceptance focus

Run the web client against the complete library, load several Library pages, switch filters repeatedly, interrupt network access during favourite/status/queue/progress changes, reconnect, and verify each mutation applies no more than once. Remove a cached offline-audio response through browser storage tools and confirm **Check downloads** repairs it from the retained download record. A record whose source blob is absent must remain visible as **Needs repair** rather than disappearing or pretending to be healthy.
