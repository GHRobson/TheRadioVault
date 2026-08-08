# Radio Vault v0.29 architecture

## Canonical archive model

Radio Vault separates three identities:

1. **Broadcast** — the programme event users browse, research, favourite and track progress against.
2. **Recording** — one capture or assembled multipart representation of that broadcast.
3. **Physical File** — an actual media file, including exact copies and alternate encodes.

This prevents duplicate library entries while preserving every recording and file.

## Library Truth boundary

The evidence-bearing parser and guarded adoption pipeline identify shows, dates, slots, multipart families, variants, duplicates and conflicts. Adoption is sealed, backed up, transactional, auditable and fail-closed. Held/review-required groups remain explicit instead of being silently promoted.

## Media boundary

Playback consumers resolve a canonical media manifest. Multipart broadcasts share one logical timeline for playback, progress, transcripts, Moments, web transfer and offline manifests. Incomplete or review-required coverage fails closed.

## Data-safety boundary

Listening progress is broadcast-level, written across canonical members, flushed at shutdown and mirrored to an atomic recovery journal. Session evidence distinguishes clean and unexpected exits. Moment creation/import is idempotent and legacy duplicate repair is conservative.

## User-experience boundary

Normal navigation exposes tasks and outcomes: listening, Research decisions, Broadcasts to find and Archive Health. Library Truth, preservation evidence and technical diagnostics remain available under advanced views without driving the everyday information architecture.


## Radio Vault Anywhere boundary

The desktop-hosted instance remains authoritative for SQLite, media paths and application services. Remote clients begin with a stable server identity and capability contract, then consume bounded snapshots and explicit read/write routes. No remote response exposes arbitrary filesystem paths or direct database access.
