# Radio Vault 0.32.0-alpha1-buildfix4-avalonia-default-shell

Buildfix 4 repairs the Avalonia XAML compiler failures found by the third Windows build. The Library table header and result rows now obtain spacing from `Border.Padding` rather than the unsupported `Grid.Padding` property, and the search field uses Avalonia 12's `PlaceholderText` property instead of the obsolete `Watermark` alias.

Buildfix 2's application-lifetime aliases and Buildfix 3's constructor/root-namespace corrections remain intact. Avalonia remains the default `TheRadioVault.exe`; WPF remains available as `TheRadioVault.WpfReference.exe`. No database, LAN, API, pairing or cache identity changes are included.
