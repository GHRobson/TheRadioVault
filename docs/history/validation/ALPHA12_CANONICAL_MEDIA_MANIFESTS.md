# Alpha 12 canonical media contract

Alpha 12 defines one reusable boundary for alternate recordings and multipart consumers.

`GetCanonicalRecordingOptions(canonicalKey)` returns all recordings from the commit-verified adopted truth run. `GetCanonicalPlaybackPlan(canonicalKey, recordingKey)` returns ordered logical segments for an explicitly selected safe recording. Passing no recording key preserves Alpha 11 preferred-recording behaviour. `GetCanonicalDownloadManifest` chooses one available physical source per segment and returns the complete logical timeline plus byte totals.

This is deliberately a service-layer contract. Desktop recording management, the web/offline player, assembled transcript navigation, and LAN federation can now share the same ordering and identity rules instead of reconstructing multipart structure independently.
