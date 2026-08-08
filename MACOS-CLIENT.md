# Radio Vault Mac Client

This port reuses the complete Avalonia client and connects to the existing
Radio Vault Server. It does not build or install a macOS Server.

## Current target

- macOS 13 or later
- Apple Silicon (`osx-arm64`) first
- Existing Radio Vault Server `0.35.0-alpha9-buildfix3`
- Native AVFoundation playback for downloaded files and authenticated server streams

## Build on Windows

```powershell
.\package-macos-client.ps1
```

The script cross-publishes the self-contained client and creates:

```text
artifacts\macos\osx-arm64\Radio Vault.app
artifacts\macos\osx-arm64\RadioVault.Client-0.35.0-alpha9-buildfix3-osx-arm64-unsigned.zip
```

The Windows-produced ZIP is an unsigned transfer artifact containing the app,
its hash manifest, entitlements and the Mac finalizer. On a Mac, extract it and
run:

```zsh
zsh finalize-macos-client.sh
open "Radio Vault.app"
```

For a signed distribution build, set `DEVELOPER_ID_APPLICATION` and optionally
`APPLE_NOTARY_PROFILE` before running the included finalizer. The repository
copy remains at `installer/macos/finalize-macos-client.sh`.

## Required on-Mac acceptance

1. The application launches natively on Apple Silicon.
2. Local-network permission appears with the Radio Vault explanation.
3. Pairing with the existing Windows Server succeeds and remains pinned.
4. Dashboard, Search, Library, Favourites, Moments, Explore, Knowledge,
   Downloads, Settings and Now Playing load from the Server.
5. Streaming and downloaded MP3/M4A broadcasts play, pause, seek, skip, change
   speed and volume, resume, advance multipart broadcasts and finish cleanly.
6. Queue, progress, favourites, Moments and playback handoff remain shared.
7. Cache-first startup works while the Server is temporarily unavailable.
8. The signed app passes `codesign`, `spctl` and Apple notarization checks.

The AVFoundation bridge compiles and cross-publishes on Windows, but audio and
macOS lifecycle behaviour cannot be declared release-ready until these checks
run on macOS.
