# Radio Vault repository instructions

This repository is the single source of truth for the Windows Client, Windows
Server, macOS Client, iOS Client and their shared libraries.

## Source-control workflow

- Inspect `git status` before making changes and preserve unrelated work.
- Start work from an up-to-date `main` and use a short-lived topic branch.
- Never maintain or patch a second platform-specific copy of the repository.
- Do not commit `bin`, `obj`, `artifacts`, databases, logs, pairing state,
  credentials, tokens, certificates or signing identities.
- Keep commits focused and describe the user-visible result in the commit and
  pull-request text.

## Cross-platform parity

- Treat shared application, presentation, infrastructure, service, web and
  protocol changes as affecting every client and the server.
- Build and test every locally available affected target.
- Add or update automated tests for bug fixes and shared contracts.
- When one platform cannot be executed locally, rely on its required GitHub
  Actions check and do not claim that platform was manually verified.
- Keep Windows-, macOS- and iOS-only code behind explicit platform boundaries;
  do not duplicate shared business logic between clients.
- Preserve pairing, playback, cache and handoff protocol compatibility between
  the native clients and the Server.

## Toolchain

- Keep the .NET 8 SDK/runtime and the SDK selected by `global.json` installed
  side by side. The applications target `net8.0`, but Avalonia 12's generators
  require the pinned newer SDK compiler.
- Run `release-gate.ps1` on Windows before a release.
- Run the macOS Client and Server builds and focused macOS smoke tests described
  in `DEVELOPMENT.md` before distributing a Mac build.
- Require the Linux Client and Server CI build and platform smoke test before
  distributing a Linux build.
