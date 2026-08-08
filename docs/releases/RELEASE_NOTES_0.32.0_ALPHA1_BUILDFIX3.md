# Radio Vault 0.32.0-alpha1-buildfix3-avalonia-default-shell

Buildfix 3 repairs the four compiler errors found by the second Avalonia-default Windows build. The startup options record now disambiguates its null database path with a named argument, and framework types that could be shadowed by the `TheRadioVault.Desktop.Avalonia` project namespace are explicitly rooted through `global::Avalonia`.

Buildfix 2's application-lifetime aliases remain intact. Avalonia remains the default `TheRadioVault.exe`; WPF remains available as `TheRadioVault.WpfReference.exe`. No database, LAN, API, pairing or cache identity changes are included.
