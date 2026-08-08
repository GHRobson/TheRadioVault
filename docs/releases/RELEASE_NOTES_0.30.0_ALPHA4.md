# Radio Vault v0.30.0 Alpha 4 — Personal State Migration

Alpha 4 adds the one-time consolidation workflow needed to make the server the sole server library without abandoning listening state accumulated on the remote client.

Export a `.trvstate` package from the remote client, preview it on the server, and apply the guarded import. Radio Vault backs up the server database first, matches state to canonical broadcasts, protects newer/completed server progress, merges listening history conservatively, adds favourites, deduplicates Moments and appends only missing queue items.

No audio files, archive paths, transcripts or research metadata are copied. Database schema remains 45 and all accepted Alpha 3 LAN playback/write-through behaviour remains intact.
