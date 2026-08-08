# Release Notes — Radio Vault v0.31.0 RC1

Version: `0.31.0-rc1-core-hardening-release-candidate`

RC1 promotes the successfully built, release-gated and runtime-accepted Beta 1 Core Hardening implementation without changing application behaviour.

## What is retained

- The completed application-service, platform, playback/media, remote-session and composition boundaries from the accepted v0.31 alphas.
- The executable WPF-independence proof and explicit Avalonia handoff work packages from Beta 1.
- Existing local-library and native remote-client parity, pairing, encrypted cache, playback, progress, queue, favourites, Moments, transcripts, Metadata Studio and Research behaviour.
- Safe local/remote mode transitions, ordered shutdown, final progress persistence and watchdog diagnostics.

## RC1 release engineering

- Promoted the build identity consistently through assembly metadata, architecture reports, validation, documentation and packaging.
- Integrated both PowerShell 5.1 validator corrections found during Beta 1 acceptance.
- Added a process-only `RUN-RELEASE-GATE.cmd` launcher.
- Preserved deterministic Release compilation, regression tests and compiled ProductVersion verification.
- Marked release packaging as RC and recorded the accepted Beta 1 Windows gate/runtime baseline in `BUILD_INFO.json`.
- Added the final clean-package, existing-install, local/remote, mode-switching, shutdown and daily-driver checklist.

## Compatibility

Database schema **45**, LAN capability generation **14**, API **v1**, web-shell generation **10**, shell cache **v33**, IndexedDB **v2**, pairing credentials and audio/artwork cache identities are unchanged. No migration, re-adoption, re-pairing or cache reset is required.

## Candidate rule

Only a reproduced release blocker warrants RC2. Otherwise the next build is Radio Vault v0.31.0 stable, followed by the Avalonia desktop foundation phase.
