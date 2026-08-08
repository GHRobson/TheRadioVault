# Radio Vault v0.32.0 Alpha 10 Buildfix 15

Build identity: `0.32.0-alpha10-buildfix15-buildfix2-presentation-log-boundary`

Buildfix 15 is a contained server-progress compatibility and glyph-polish pass over the user-confirmed Buildfix 14 compile/launch baseline.

## Buildfix 2 compile correction

The first compile correction incorrectly assumed that namespace qualification made `DiagnosticLog` available to the Presentation assembly. The type is actually compiled only by the WPF shell project. Buildfix 2 keeps the Presentation layer independent by using `System.Diagnostics.Trace` for the non-fatal compatibility diagnostic. No playback or visual behaviour is changed by this correction.

## Connected Access progress compatibility

The remaining resume regression was isolated by comparing Avalonia with the accepted 0.31 remote client against the same 0.31 server:

- Radio Vault 0.31 successfully writes listening state through the established `offline-progress` endpoint.
- The Avalonia client first attempted the newer `claim-device` ownership command.
- A 0.31 server returns `Unknown playback command` for that 0.32-only command.
- Avalonia then attempted a desktop-client handoff heartbeat, which the old server treated as a conflicting phone/web report; that conflict returned before the proven progress write could run.

Buildfix 15 detects that older command contract once per session, disables only unsupported desktop-client handoff calls, clears stale handoff UI state, and continues through the same canonical progress endpoint used by the working 0.31 client. Three-device handoff remains enabled when connected to a supporting server. A transient handoff-report failure also no longer suppresses an otherwise valid progress mutation, while a genuine ownership conflict on a current server still blocks a stale writer.

The Buildfix 14 final shutdown save, server confirmation, encrypted-cache flush and centred **Closing RadioVault** overlay remain unchanged.

## Glyph refinements

- Glyph-only Dashboard and persistent-player actions no longer receive Fluent's pale square hover surface; only the glyph changes emphasis.
- Dashboard Up Next, On This Day, Recently Added and Unheard play actions use the unframed blue Now Playing vector.
- Library-row play/pause uses the same blue line-weight language without a yellow square.

Database schema remains **45**, LAN capability generation remains **14**, and API/pairing/cache identities remain unchanged.
