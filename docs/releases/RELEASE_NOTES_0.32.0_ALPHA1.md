# Radio Vault v0.32.0 Alpha 1

## Highlights

- Starts the Avalonia desktop rebuild without discarding or destabilising the accepted WPF application.
- Adds a functional Dashboard and Library vertical slice driven by the real Radio Vault service/database stack.
- Introduces a toolkit-neutral Presentation assembly and a canonical library-browse contract.
- Adds the initial Avalonia platform adapter set and a validated/frozen application composition root.
- Adds dual-shell build, run, release-gate and packaging workflows.

## Not yet migrated

Playback, queue/favourite mutations, editing, Research, transcripts/Moments, library administration, Connected Access and specialised windows remain in WPF for this alpha.

## Compatibility

No database migration, Library Truth re-adoption, re-pairing or cache reset is expected. Schema 45, LAN capability generation 14 and API v1 remain unchanged.
## Buildfix 1 — default executable

Avalonia is now the canonical `TheRadioVault.exe`; the complete WPF shell is explicitly `TheRadioVault.WpfReference.exe`. A shared Visual Studio launch profile and double-click CMD launchers remove the need to type PowerShell commands for normal use.

