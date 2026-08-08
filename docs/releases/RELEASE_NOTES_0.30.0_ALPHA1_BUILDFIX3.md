# Radio Vault v0.30.0 Alpha 1 Buildfix 3

This build fixes the post-pairing HTTP 500 seen when the remote client attempted to verify the server. Pairing itself had succeeded; verification was unnecessarily calling the full Radio Vault Anywhere dashboard bootstrap.

Paired remote clients now use a dedicated lightweight federation bootstrap containing only trusted server identity and server library counts. Full bootstrap failures also produce structured diagnostic IDs instead of opaque errors. Discovery, certificate pinning, one-time pairing codes and persistent per-client credentials are unchanged.

Database schema remains 45, capability generation remains 8, web-shell generation remains 10, Anywhere shell cache remains v33, IndexedDB remains v2, and downloaded audio/artwork caches remain v1.
