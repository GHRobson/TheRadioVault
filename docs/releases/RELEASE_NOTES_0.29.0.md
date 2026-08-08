# Radio Vault v0.29.0 — Radio Vault Anywhere

Radio Vault Anywhere is complete. The Windows application can now act as the authoritative server for a secure, responsive web companion that browses and plays the same canonical library, continues across multipart recordings, works with manually downloaded broadcasts offline, and synchronises state safely after reconnecting.

## Highlights

- Bootstrap-driven responsive web companion for phone, tablet and desktop browsers.
- Canonical Library search, facets, compact mobile filters and bounded pagination.
- Desktop/web playback ownership transfer with shared progress, speed and queue state.
- Manual offline downloads with seeking, resume, storage auditing and repair.
- Durable offline synchronisation for progress, favourites, listening status and queue additions.
- Canonical Moments and timed transcripts with tap-to-seek navigation.
- Compact sync-state icons and detailed recovery controls.
- Privacy-safe Anywhere diagnostic export.
- Safe application-shell updates and scoped shell repair that preserve device data.
- Final mobile accessibility and reduced-motion polish.

## Final promotion

The final release preserves the accepted RC1 implementation. There is no new database migration or feature change. The application-shell cache advances to `radio-vault-anywhere-shell-v33` solely to replace RC1 HTML on installed clients.

## Compatibility

- Database schema: 45.
- Capability generation: 7.
- Desktop web-shell generation: 10.
- IndexedDB: version 2.
- Audio cache: `radio-vault-anywhere-audio-v1`.
- Artwork cache: `radio-vault-anywhere-artwork-v1`.
