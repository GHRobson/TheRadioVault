# Shared Radio Vault development workflow

GitHub is the remote source of truth. Each Windows or Mac development computer
uses its own clone of this same repository; source folders are never copied
between computers.

## Start a change

Before asking Codex to make a change:

```text
git switch main
git pull --ff-only
git switch -c fix/short-description
```

Ask Codex to work only inside that checkout and topic branch. Codex should
inspect the existing changes first, implement the change, add a regression
test where appropriate and report which targets were actually verified.

## Validate locally

On Apple Silicon macOS, the normal local gate is:

```zsh
./local-release-gate.sh
```

This validates the Mac Client, Mac Server, shared handoff checks, mobile
offline/sync checks and the iOS simulator without using GitHub Actions. Add
`--package` to create local Client and Server ZIP/DMG installers as well. Logs
are written beneath `artifacts/local`. The script reads the SDK pinned in
`global.json`, selects the full Xcode installation for iOS, and fails early if
product version values have drifted.

Use `./package-macos-local.sh` when only refreshed Mac installers are needed.
The existing PowerShell packaging scripts remain the cross-platform/CI
implementation.

On Windows, run the complete release gate:

```powershell
./release-gate.ps1
```

On Apple Silicon macOS, build the native desktop and UIKit iOS clients and test
their platform boundaries:

```zsh
dotnet restore TheRadioVault.sln
dotnet build TheRadioVault.Desktop.Avalonia/TheRadioVault.Desktop.Avalonia.csproj \
  -c Release --no-restore -warnaserror
dotnet build TheRadioVault.Server/TheRadioVault.Server.csproj \
  -c Release --no-restore -warnaserror
dotnet build TheRadioVault.Tests/TheRadioVault.Tests.csproj \
  -c Release --no-restore -warnaserror
dotnet build TheRadioVault.Data.Tests/TheRadioVault.Data.Tests.csproj \
  -c Release --no-restore -warnaserror
dotnet build TheRadioVault.Web.Tests/TheRadioVault.Web.Tests.csproj \
  -c Release --no-restore -warnaserror
dotnet build TheRadioVault.SourceChecks/TheRadioVault.SourceChecks.csproj \
  -c Release --no-restore -warnaserror
dotnet run --project TheRadioVault.SourceChecks/TheRadioVault.SourceChecks.csproj \
  -c Release --no-build
dotnet run --project TheRadioVault.Data.Tests/TheRadioVault.Data.Tests.csproj \
  -c Release --no-build
dotnet run --project TheRadioVault.Web.Tests/TheRadioVault.Web.Tests.csproj \
  -c Release --no-build
dotnet run --project TheRadioVault.Tests/TheRadioVault.Tests.csproj \
  -c Release --no-build -- \
  "Mac client remains usable before server pairing" \
  "Mac Client uses native AVFoundation and existing server contracts" \
  "macOS and Linux packages preserve the shared client-server boundary"
dotnet workload install ios
dotnet build TheRadioVault.Client.iOS/TheRadioVault.Client.iOS.csproj \
  -c Release -r iossimulator-arm64 -warnaserror
dotnet run --project TheRadioVault.Tests/TheRadioVault.Tests.csproj \
  -c Release --no-build -- \
  "iOS Client preserves native platform and server boundaries"
```

On x64 Linux, build and package both desktop applications:

```bash
dotnet restore TheRadioVault.sln
dotnet build TheRadioVault.Desktop.Avalonia/TheRadioVault.Desktop.Avalonia.csproj \
  -c Release --no-restore -warnaserror
dotnet build TheRadioVault.Server/TheRadioVault.Server.csproj \
  -c Release --no-restore -warnaserror
dotnet build TheRadioVault.Tests/TheRadioVault.Tests.csproj \
  -c Release --no-restore -warnaserror
dotnet build TheRadioVault.Data.Tests/TheRadioVault.Data.Tests.csproj \
  -c Release --no-restore -warnaserror
dotnet build TheRadioVault.Web.Tests/TheRadioVault.Web.Tests.csproj \
  -c Release --no-restore -warnaserror
dotnet build TheRadioVault.SourceChecks/TheRadioVault.SourceChecks.csproj \
  -c Release --no-restore -warnaserror
dotnet run --project TheRadioVault.SourceChecks/TheRadioVault.SourceChecks.csproj \
  -c Release --no-build
dotnet run --project TheRadioVault.Data.Tests/TheRadioVault.Data.Tests.csproj \
  -c Release --no-build
dotnet run --project TheRadioVault.Web.Tests/TheRadioVault.Web.Tests.csproj \
  -c Release --no-build
dotnet run --project TheRadioVault.Tests/TheRadioVault.Tests.csproj \
  -c Release --no-build -- \
  "macOS and Linux packages preserve the shared client-server boundary"
./package-linux.sh
```

Local validation is the normal development loop. GitHub's Windows, macOS,
Linux and iOS checks remain the independent merge authority. Radio Vault's
standard GitHub-hosted runners are available without metered build minutes
while the repository is public. Every pushed branch is checked, but feature
work must still remain off `main` until the complete platform workflow passes
and the affected apps have had proportionate hands-on testing.

## Publish the change

```text
git status
git add --all
git commit -m "Describe the completed change"
git push -u origin fix/short-description
```

Open a pull request into `main`. Merge only after all required checks pass:

- `Windows client and server`
- `macOS client and server`
- `Linux client and server`
- `iOS client`

After merging, delete the topic branch and update each computer with
`git switch main` followed by `git pull --ff-only`.

## Releases

Create releases from a version tag on a tested `main` commit, for example
`v0.41.0`. The Windows, Mac and Linux Clients and Servers and the iOS Client
for a release must all identify the same commit and `VERSION.txt` value.

The current engineering roadmap and cross-platform audit are maintained in
[`docs/roadmap/RADIOVAULT_ROADMAP.md`](docs/roadmap/RADIOVAULT_ROADMAP.md) and
[`docs/architecture/SOURCE_AUDIT_2026-08-11.md`](docs/architecture/SOURCE_AUDIT_2026-08-11.md).

Unsigned CI artifacts are for testing. Public Mac distribution still requires
Developer ID signing and notarization; public Windows distribution should use
the project's signing certificate. Signing credentials must be stored as
GitHub secrets or in a secure local certificate store, never in this repository.
