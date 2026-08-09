# Radio Vault on Linux

Radio Vault provides a portable x64 Linux Client and Server. They use the same
library, pairing, playback handoff, downloads, Explore and Radio Vault Web
contracts as the Windows and Mac builds.

## Current target

- A modern x64 Linux distribution with a graphical desktop
- The portable `linux-x64` build from GitHub Actions
- `mpv` installed for Client audio playback
- Current-user desktop autostart for the Server

## Download and run

Open the latest successful [Radio Vault parity build](https://github.com/GHRobson/TheRadioVault/actions/workflows/ci.yml?query=branch%3Amain), download `linux-client-and-server-x64` and extract it.

Run the Client with:

```bash
./run-radiovault.sh
```

Run the Server with:

```bash
./run-radiovault-server.sh
```

Install mpv with your distribution's normal software manager before using the
Client for playback. For example, on Ubuntu or Debian:

```bash
sudo apt install mpv
```

## Build from source

With the pinned .NET SDK installed:

```bash
./package-linux.sh
```

The script creates separate self-contained Client and Server archives below
`artifacts/linux/linux-x64/`.

## Current alpha notes

- The builds are unsigned portable archives rather than distribution-specific packages.
- The Client delegates audio output to a private mpv process controlled through a local Unix socket. Server credentials are never passed to mpv.
- The Server's automatic Whisper installer is currently Windows-only. A manually installed local whisper.cpp worker can still be configured.
- Server background startup uses the desktop session's current-user autostart folder, not a system-wide service.
