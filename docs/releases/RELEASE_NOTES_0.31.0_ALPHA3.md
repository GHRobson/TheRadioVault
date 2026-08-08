# Radio Vault v0.31.0 Alpha 3 — Windows/Platform Abstraction

Version: `0.31.0-alpha3-windows-platform-abstraction`

Alpha 3 moves routine Windows integration behind the platform-neutral Application boundary while preserving the accepted Alpha 2 application behaviour.

## Changes

- Added application contracts and neutral request models for external launching, system appearance and screen bounds.
- Added Windows/WPF implementations in `TheRadioVault.Platform.Windows` and registered them in the composition root.
- Removed direct process launching, WPF clipboard, Windows Registry and WPF virtual-screen calls from the WPF project.
- Routed every current clipboard action through `IClipboardService`.
- Routed link opening, file reveal, data/model folder opening, crash-log opening and database restart through `IExternalLauncherService`.
- Routed common archive-folder, backup, metadata and transcription file selection through `IFileSelectionService`.
- Routed System theme detection and saved-window visibility checks through platform services.
- Strengthened architecture validation to reject regression to those direct platform calls.

## Compatibility

No database, library, pairing, LAN protocol, cache or UI migration is introduced. Schema remains 45 and LAN capability generation remains 14.
