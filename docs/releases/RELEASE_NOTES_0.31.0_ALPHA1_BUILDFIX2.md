# Radio Vault v0.31.0 Alpha 1 Buildfix 2

Version: `0.31.0-alpha1-architecture-baseline-buildfix2`

## Correction

The first repair qualified the new `WpfApplicationLifetime` adapter, but the new sibling namespace `TheRadioVault.Application` also changed name resolution inside the existing WPF project. The second Windows build exposed eight additional `CS0234` failures where bare `Application.Current` was interpreted as `TheRadioVault.Application.Current`.

Buildfix 2 fully qualifies all active WPF application references as `global::System.Windows.Application.Current`. The affected paths cover:

- Library Truth shutdown and failed-adoption recovery;
- switching between local-library and remote-client windows;
- remote playback and federation dispatcher selection;
- theme resource dictionaries;
- the Windows application-lifetime adapter and existing shutdown paths.

The validation suite now scans the complete WPF and Windows-adapter source trees and rejects any future unqualified `Application.Current` usage. The earlier missing-project metadata errors were downstream consequences of these compilation failures.

## Compatibility

There is no database, LAN, API, cache, pairing, library or user-interface change. Database schema remains 45 and LAN capability generation remains 14.
