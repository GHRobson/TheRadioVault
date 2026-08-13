# v0.29.0 Alpha 2 static validation

Target: `0.29.0-alpha2-anywhere-web-app-cutover`

## Passed in the packaging environment

- `VERSION.txt`, project `Version` and `InformationalVersion` agree.
- Database schema remains 45.
- All project and XAML XML files parse successfully.
- Embedded web-client JavaScript and Service Worker pass Node syntax validation.
- Bootstrap exposes show and year facets, On This Day, playback and queue state.
- Episode and Broadcast Info contracts expose explicit canonical broadcast IDs.
- Server-side year/month/date/listening-status filtering and corresponding tests are present.
- Capability generation is 2.
- Navigation restoration, automatic reconnect refresh and privacy-safe Anywhere diagnostics are present in the PWA.
- Secure application shell cache is v27; audio and artwork cache identities remain v1.
- Source-root Markdown remains limited to `README.md`, `BUILDING.md` and `CHANGELOG.md`.
- The short-path archive root is `RV-029-A2`.

## Pending on Windows

- Restore and compile the complete solution with Visual Studio/.NET 8.
- Run `TheRadioVault.Tests`.
- Perform the iPhone/iPad connected, disconnect, offline-playback and automatic-reconnect acceptance pass.
