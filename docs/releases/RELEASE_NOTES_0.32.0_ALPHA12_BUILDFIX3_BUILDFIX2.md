# Radio Vault v0.32.0 Alpha 12 Buildfix 3 Buildfix 2

Build identity: `0.32.0-alpha12-buildfix3-buildfix2-handoff-takeover-loading-feedback`

This build repairs the case where the laptop displayed a correct shared playhead and **Move to this device** icon, but clicking it only flashed and left playback running on the Avalonia server.

The server now releases playback through the underlying playback session rather than depending on a presentation `IsPlaying` value that can lag the audio engine. The requesting device reserves the handoff before opening remote audio, receives a 45-second pending-claim window for slow media startup, reports its first live state, and verifies that the server actually assigned ownership to it. A rejected or unconfirmed transfer is shown as an explicit error rather than silently reverting to the move icon.

The contextual transport button now displays an animated activity glyph while any broadcast is being prepared or while ownership is moving. Research pack preview and application also show indeterminate progress and working labels, with an explicit UI render turn before heavy local work starts, so long operations no longer look frozen.

Database schema remains **45**, LAN capability generation remains **14**, and API v1, pairing, certificate and cache identities remain unchanged.
