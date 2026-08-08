# Radio Vault v0.28.0 — Library Truth

v0.28.0 completes Radio Vault's foundational archive rebuild and promotes the accepted RC1 code to stable release. No database migration is introduced beyond the already-adopted schema 45, and no new behaviour is added after RC1 other than refreshing the secure web shell cache.

## Highlights

- Canonical Broadcast → Recording → Physical File library model.
- Safe handling of multipart shows, alternate encodes, exact duplicates and held/review-required groups.
- Continuous canonical playback, resume state, transcripts and Moments across multipart boundaries.
- Crash-safe listening-progress recovery and shutdown persistence.
- Duplicate-Moment repair and prevention.
- Fast, actionable Research & Metadata decisions with exact affected-record navigation.
- Broadcasts to find discovery list for researched shows not currently in the archive.
- Unified Archive Health, storage, preservation and backup experience.
- Desktop and secure local-web playback with ownership transfer and manual offline downloads.

## Compatibility

- Database schema: 45.
- Existing RC1/Beta 2 schema-45 databases open directly.
- Audio files are not renamed, moved or modified.
- The retained playback journal and session guard continue protecting progress across restarts and unexpected exits.
