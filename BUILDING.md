# Building Radio Vault 0.35.0 Alpha 9 Buildfix 3

## Requirements

- .NET 8 SDK/runtime and .NET 10 SDK installed side by side
- PowerShell on Windows
- Windows x64 for Windows Client, Server and installer builds
- macOS 13 or later on Apple Silicon for native Mac validation
- Xcode 26.6 and the .NET iOS workload for iPhone/iPad Simulator builds
- Inno Setup 6 only when producing the standard Windows installers

The applications target .NET 8, while Avalonia 12's source generators require
the newer compiler pinned in `global.json`. GitHub Actions installs both .NET
versions and independently builds the Windows Client, Windows Server and Apple
Silicon Mac and iOS Clients for every proposed change.

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

## Release validation

Run:

```powershell
.\release-gate.ps1
```

The gate performs source and XML validation, verifies the Avalonia-only architecture, restores and deterministically builds the complete solution with warnings treated as errors, runs the complete smoke suite and checks that the built product version matches `VERSION.txt`.

## Packages and installers

Create the Apple Silicon Mac Client bundle and unsigned transfer archive on
Windows with:

```powershell
.\package-macos-client.ps1
```

This builds only the Client and its shared dependencies. Final permission,
signing and notarization checks run on macOS using
`installer/macos/finalize-macos-client.sh`. See [MACOS-CLIENT.md](MACOS-CLIENT.md).

Create the self-contained Server package and installer:

```powershell
.\package-server-installer.ps1
```

Create the self-contained Client package and installer:

```powershell
.\package-client-installer.ps1
```

Outputs are written below `artifacts\`. The Client and Server use stable Inno Setup application IDs so an update replaces binaries in place. Authoritative data and user configuration live outside the installation directories and are not included in uninstall cleanup.

Create a source archive and regenerate `SOURCE_MANIFEST.sha256.json` with:

```powershell
.\tools\Package-Source.ps1 -Destination .\artifacts\RadioVault-0.35.0-alpha9-buildfix3-source.zip
```

## Alpha acceptance

Before distributing this alpha, verify an upgrade on the main Server PC and a paired Client, confirm schema 47 and capability generation 40, import an AI-enriched Knowledge Database, export the whole archive, open cited broadcasts from Explore, then repeat playback, handoff and transcription checks.
