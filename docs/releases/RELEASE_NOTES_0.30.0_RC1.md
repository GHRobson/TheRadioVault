# Release Notes — Radio Vault v0.30.0 RC1

Version: `0.30.0-rc1-multi-device-release-candidate`

RC1 promotes the accepted Beta 1 Multi-Device Library Access build without changing its application behaviour. It is the first v0.30 build intended for final upgrade, packaging and daily-driver acceptance before stable release.

## What is retained

- Complete normal-shell server/remote-client parity from Alpha 9.
- In-place routine synchronization deltas and deterministic full resets from Beta 1.
- Encrypted server-bound cache and explicit cached/read-only outage mode.
- Remote playback, progress, favourites, listening state, queue, Moments, transcripts, artwork, Metadata Studio, Research and mode-aware Settings.
- Safe local/remote mode switching.
- Bounded final progress persistence, shutdown-stage evidence and the 12-second shutdown watchdog.
- Explicit server ownership of storage, repair, reconciliation and rollback operations.

## RC1 release engineering

- Build identity is updated consistently across assembly metadata, source validation, documentation and regression fixtures.
- The release gate requests deterministic compilation and verifies the version embedded in the built application.
- The publish flow uses a clean self-contained `win-x64` artifact directory.
- A packaging script creates a distributable ZIP, `BUILD_INFO.json` and SHA-256 checksum.
- A final acceptance checklist covers clean extraction, Beta 1 upgrade, local and remote regression, outages, mode switching, repeated shutdown and extended soak use.

## Compatibility boundary

- Database schema: **45**
- LAN capability generation: **14**
- API: **v1**
- Web-shell generation: **10**
- Anywhere shell cache: **v33**
- IndexedDB: **v2**
- Audio/artwork caches: **v1**

No migration, re-adoption, re-pairing or cache reset is expected.

## Release-candidate rule

No new major feature should enter RC1. A follow-up candidate is justified only by a reproduced release blocker; otherwise the next build is Radio Vault v0.30.0 stable.
