# Radio Vault v0.30.0 Alpha 1 Buildfix 2

This build repairs the HTTP 400 pairing failure found after Buildfix 1 made two-PC discovery operational.

The client now sends the pairing JSON with an explicit content length. The server additionally accepts HTTP/1.1 chunked JSON request bodies, making the transport robust for .NET and future remote clients. Pairing failures return actionable structured messages.

Buildfix 1's adapter-aware discovery, directed broadcasts and Connected Access preference persistence are preserved. Database schema remains 45, capability generation remains 8, web-shell generation remains 10, Anywhere shell cache remains v33, IndexedDB remains v2, and downloaded audio/artwork caches remain v1.
