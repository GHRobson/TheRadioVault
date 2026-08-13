# Building Radio Vault 0.41.0

## Requirements

- .NET 8 SDK/runtime and .NET 10 SDK installed side by side
- PowerShell on Windows
- Windows x64 for Windows Client, Server and installer builds
- macOS 13 or later on Apple Silicon for native Mac validation
- A modern x64 Linux distribution for native Linux validation
- Xcode 26.6 and the .NET iOS workload for iPhone/iPad Simulator builds
- Inno Setup 6 only when producing the standard Windows installers

The applications target .NET 8, while Avalonia 12's source generators require
the newer compiler pinned in `global.json`. GitHub Actions installs both .NET
versions and independently builds the Windows, Mac and Linux Clients and
Servers and the iOS Client for every proposed change.

## Build and run from source

Close installed Radio Vault Client and Server processes, then run:

```powershell
.\BUILD-AND-RUN.cmd
```

The native executable is produced at:

```text
TheRadioVault.Desktop.Avalonia\bin\Release\net8.0\TheRadioVault.exe
```

`RUN-RADIO-VAULT.cmd` launches an existing Client build. `RUN-RADIO-VAULT-SERVER.cmd` launches an existing Server build. `run-source.ps1` and `run-server-source.ps1` provide the equivalent PowerShell entry points.

## Visual Studio

Open `TheRadioVault.sln`. The canonical desktop shell is the Avalonia project:

```text
TheRadioVault.Desktop.Avalonia\TheRadioVault.Desktop.Avalonia.csproj
```

The dedicated settings-only Server is:

```text
TheRadioVault.Server\TheRadioVault.Server.csproj
```

The retired WPF shell is not part of the solution or release payload.

## iOS Client

The iOS project is kept outside `TheRadioVault.sln` so the Windows release gate
does not attempt to load Apple's workload. Its interface uses native UIKit tabs,
navigation, tables, search and playback controls; the portable mobile project
contains the shared server/session logic without a UI framework dependency. On
an Apple Silicon Mac, install the workload once and build the unsigned Simulator
app with:

```zsh
dotnet workload install ios
dotnet build TheRadioVault.Client.iOS/TheRadioVault.Client.iOS.csproj \
  -c Debug -r iossimulator-arm64 -warnaserror
```

The app bundle is produced below:

```text
TheRadioVault.Client.iOS/bin/Debug/net10.0-ios/iossimulator-arm64/TheRadioVault.Client.iOS.app
```

Run it from Visual Studio Code with the C# Dev Kit/iOS tooling or install it in
a booted Simulator with `xcrun simctl install booted <app-path>`. A physical
iPhone build additionally requires an Apple Developer team, signing identity
and provisioning profile with Apple's multicast-networking capability. Pairing
and playback require the iPhone or Simulator to reach a running Radio Vault
Server on the same local network.

For acceptance, verify that playback progress appears on another connected
client after five seconds, pause and seek boundaries persist immediately,
moving a playing broadcast between iOS and a desktop stops the previous output,
and the iOS lock screen can play, pause, skip and seek the current broadcast.
Also verify that Library groups broadcasts by show, a completed multipart
download still plays after the Server is unavailable, the mini player opens
Now Playing without occupying a tab, and its play/pause control changes to a
handoff control while another device owns playback. Confirm that Favourites,
Continue Listening, Recently Added, Unplayed and Completed filters use the
server-owned Library; Play Next and Play Last appear in the shared Up Next
queue on another client; queue rows can be reordered, removed and cleared;
and a multipart download can be paused, resumed from its retained byte count
and cancelled without leaving staged media. Toggle Wi-Fi Only and verify a
download is rejected when the active iOS path is not Wi-Fi.

## Release validation

On Apple Silicon macOS, run the complete local Mac/iOS gate without using
GitHub Actions:

```zsh
./local-release-gate.sh
```

Add `--package` to also create local Client and Server ZIP and DMG installers.
The validation log is retained under `artifacts/local`.

On Windows, run:

```powershell
.\release-gate.ps1
```

The gate performs source and XML validation, verifies the Avalonia-only architecture, restores and deterministically builds the complete solution with warnings treated as errors, runs the complete smoke suite and checks that the built product version matches `VERSION.txt`.

## Packages and installers

Create the Apple Silicon Mac Client and Server bundles and unsigned transfer
archives with:

```zsh
./package-macos-local.sh
```

This native Mac script also creates the separate drag-to-Applications disk
images and does not require PowerShell. The equivalent scripts used by CI are:

```powershell
.\package-macos-client.ps1
.\package-macos-server.ps1
```

On macOS, turn those app bundles into separate drag-to-Applications disk images
with:

```bash
./package-macos-installers.sh
```

The disk images are ad-hoc signed for testing but are not notarized. Production
signing and notarization checks use the matching finalizers under
`installer/macos/`. See [MACOS-CLIENT.md](MACOS-CLIENT.md).

Create self-contained Linux Client and Server Debian installers and portable
archives on Linux with:

```bash
./package-linux.sh
```

The Linux Client requires mpv for audio playback. See [LINUX.md](LINUX.md).

Create the self-contained Server package and installer:

```powershell
.\package-server-installer.ps1
```

Create the self-contained Client package and installer:

```powershell
.\package-client-installer.ps1
```

Outputs are written below `artifacts\`. The Client and Server use stable Inno Setup application IDs so an update replaces binaries in place. Authoritative data and user configuration live outside the installation directories and are not included in uninstall cleanup.

GitHub Actions runs these packagers for every platform build. Its Windows
artifact contains both setup executables, its macOS artifact contains both disk
images, and its Linux artifact contains both Debian packages. Portable copies
remain available alongside each installer. The iOS artifact is a Simulator app;
a physical-device or App Store package must be signed by Apple for the intended
device or distribution account.

Create a source archive and regenerate `SOURCE_MANIFEST.sha256.json` with:

```zsh
./tools/package-source-local.sh artifacts/RadioVault-0.41.0-source.zip
```

The equivalent PowerShell command is:

```powershell
.\tools\Package-Source.ps1 -Destination .\artifacts\RadioVault-0.41.0-source.zip
```

## Alpha acceptance

Before distributing this build, verify an upgrade on the main Server PC and a paired Client, confirm schema 50 and capability generation 41, create and edit a saved playlist from both desktop and iPhone, add a disposable RSS feed to a test Library folder and confirm only a newly published enclosure downloads once, import an AI-enriched Knowledge Database, export the whole archive, open cited broadcasts from Explore, then repeat playback, handoff and transcription checks.
