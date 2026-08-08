# Release Notes — Radio Vault v0.31.0 Alpha 1 Buildfix 1

Build identity: `0.31.0-alpha1-architecture-baseline-buildfix1`

Buildfix 1 resolves the initial Alpha 1 compilation failure in the new Windows platform-adapter project. The WPF shutdown adapter now fully qualifies `System.Windows.Application`, preventing the new `TheRadioVault.Application` namespace from shadowing the WPF type.

No user-visible behaviour, architecture boundary, database schema, LAN capability, API contract, pairing state or cache identity changes from Alpha 1.
