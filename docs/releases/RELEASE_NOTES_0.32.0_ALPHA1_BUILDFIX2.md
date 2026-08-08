# Radio Vault 0.32.0-alpha1-buildfix2-avalonia-default-shell

Buildfix 2 repairs the first Avalonia-default Windows compilation failure. The Avalonia and Radio Vault application-lifetime contracts now have explicit aliases in the adapter and composition root, eliminating CS0104 without changing runtime behaviour. Validators now guard against the ambiguous import pattern returning.

Avalonia remains the default `TheRadioVault.exe`; WPF remains available as `TheRadioVault.WpfReference.exe`. No database, LAN, API, pairing or cache identity changes are included.
