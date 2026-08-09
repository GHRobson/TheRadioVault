# Radio Vault on Mac

Radio Vault now provides both the complete Avalonia client and the dedicated
Radio Vault Server for Apple Silicon Macs. A Mac can connect to another Radio
Vault Server, or it can own and serve the main archive itself.

## Current target

- macOS 13 or later
- Apple Silicon (`osx-arm64`) first
- Native Radio Vault Server `0.35.0-alpha9-buildfix3`
- Native AVFoundation playback for downloaded files and authenticated server streams
- Current-user LaunchAgent support for starting the Server at sign-in

## Build on Windows

```powershell
.\package-macos-client.ps1
.\package-macos-server.ps1
```

The script cross-publishes the self-contained client and creates:

```text
artifacts\macos\osx-arm64\Radio Vault.app
artifacts\macos\osx-arm64\RadioVault.Client-0.35.0-alpha9-buildfix3-osx-arm64-unsigned.zip
artifacts\macos-server\osx-arm64\Radio Vault Server.app
artifacts\macos-server\osx-arm64\RadioVault.Server-0.35.0-alpha9-buildfix3-osx-arm64-unsigned.zip
```

The Windows-produced ZIP is an unsigned transfer artifact containing the app,
its hash manifest, entitlements and the Mac finalizer. On a Mac, extract it and
run:

```zsh
zsh finalize-macos-client.sh
zsh finalize-macos-server.sh
open "Radio Vault.app"
open "Radio Vault Server.app"
```

For a signed distribution build, set `DEVELOPER_ID_APPLICATION` and optionally
`APPLE_NOTARY_PROFILE` before running the included finalizer. The repository
copies remain under `installer/macos/`.

## Required on-Mac acceptance

1. The Client and Server launch natively on Apple Silicon.
2. The Server can add folders, scan an archive, host Radio Vault Web and create pairing codes.
3. Start with macOS creates a current-user LaunchAgent and background launch works after sign-in.
4. Local-network permission appears with the Radio Vault explanation.
5. Pairing with a Windows, Mac or Linux Server succeeds and remains pinned.
6. Dashboard, Search, Library, Favourites, Moments, Explore, Knowledge,
   Downloads, Settings and Now Playing load from the Server.
7. Streaming and downloaded MP3/M4A broadcasts play, pause, seek, skip, change
   speed and volume, resume, advance multipart broadcasts and finish cleanly.
8. Queue, progress, favourites, Moments and playback handoff remain shared.
9. Cache-first startup works while the Server is temporarily unavailable.
10. Signed apps pass `codesign`, `spctl` and Apple notarization checks.

The AVFoundation bridge and Server cross-publish from the shared source, but
audio, background lifecycle and local-network behaviour must still be accepted
on a real Mac before a public release.
