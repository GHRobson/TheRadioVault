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
dotnet build TheRadioVault.Tests/TheRadioVault.Tests.csproj \
  -c Release --no-restore -warnaserror
dotnet run --project TheRadioVault.Tests/TheRadioVault.Tests.csproj \
  -c Release --no-build -- \
  "Mac client remains usable before server pairing" \
  "Mac Client uses native AVFoundation and existing server contracts"
dotnet workload install ios
dotnet build TheRadioVault.Client.iOS/TheRadioVault.Client.iOS.csproj \
  -c Release -r iossimulator-arm64 -warnaserror
dotnet run --project TheRadioVault.Tests/TheRadioVault.Tests.csproj \
  -c Release --no-build -- \
  "iOS Client preserves native platform and server boundaries"
```

Local validation is helpful, but GitHub's Windows, macOS and iOS checks are the
merge authority.

## Publish the change

```text
git status
git add --all
git commit -m "Describe the completed change"
git push -u origin fix/short-description
```

Open a pull request into `main`. Merge only after all required checks pass:

- `Windows client and server`
- `macOS client`
- `iOS client`

After merging, delete the topic branch and update each computer with
`git switch main` followed by `git pull --ff-only`.

## Releases

Create releases from a version tag on a tested `main` commit, for example
`v0.35.0-alpha9-buildfix3`. The Windows Client, Windows Server, Mac Client and iOS Client
for a release must all identify the same commit and `VERSION.txt` value.

Unsigned CI artifacts are for testing. Public Mac distribution still requires
Developer ID signing and notarization; public Windows distribution should use
the project's signing certificate. Signing credentials must be stored as
GitHub secrets or in a secure local certificate store, never in this repository.
