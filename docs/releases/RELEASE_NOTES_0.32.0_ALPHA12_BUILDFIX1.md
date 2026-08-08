# Radio Vault v0.32.0 Alpha 12 Buildfix 1

Build identity: `0.32.0-alpha12-buildfix1-handoff-health-library-sync`

## Purpose

This buildfix repairs three pieces of Alpha 11/12 functionality that were present architecturally but not usable end to end: playback handoff, remote Archive Health and server-library refresh. It retains the successfully built Alpha 12 source and its Research/UI additions.

## Playback handoff

- The Avalonia-hosted server now accepts `claim-device` and `claim-device-paused` rather than returning an unknown-command response that disabled handoff on the client.
- The authoritative server shell uses a real `IPlaybackHandoffService`; it is no longer registered with a null handoff implementation.
- Now Playing contains an always-visible Shared playback card whenever the server session is available, even before a local broadcast is loaded.
- The card lists the server, paired desktop clients and phone/web clients, marks the active owner, and exposes Continue on this device / Move playback to server.
- Active Avalonia endpoints report logical position, duration, speed and play/pause state every two seconds.
- A desktop claim is confirmed immediately after its media opens, closing the race in which a stale ownership snapshot could pause the newly claimed output.
- The existing phone heartbeat and transfer gesture remain in place; the server session continues to reject stale non-owner progress writes.

## Archive Health

- Connected Settings now obtains Archive Health directly from the authoritative server.
- Running Analyse archive from a connected client performs the analysis against the server database and returns the same score, breakdown, actionable count and Research summary shown on the server.
- Temporary server/report failures are surfaced without preventing the rest of Settings from loading.

## Library discovery and refresh

- Settings now provides Scan Library now on both the authoritative server and connected desktop clients.
- A remote scan is executed on the server and followed by a forced client synchronization.
- The Avalonia server watches each enabled registered archive root and debounces supported media changes for 45 seconds before scanning.
- A one-hour due check remains the safety net for watcher overflow, disconnected network shares and changes made while Radio Vault was closed.
- Scan completion invalidates the canonical episode snapshot and publishes a library change without an episode ID; federation clients interpret it as a full cache reset and reload Library, Dashboard and Search data.
- The Settings status card reports whether a scan is running, the last completion time and found/added/updated/error counts.

## Compatibility

- Database schema: **45**
- LAN capability generation: **14**
- API: **v1**
- No library migration or re-adoption is required.
- The v0.31 playback-progress fallback remains available when a genuinely older server rejects the handoff contract.

## Build note

Static source validation is included with the package. The .NET SDK was unavailable in the packaging environment, so this buildfix still requires `BUILD-AND-RUN.cmd` on Windows before runtime acceptance.
