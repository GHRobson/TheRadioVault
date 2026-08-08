# Radio Vault 0.32.0-alpha1-buildfix5-avalonia-startup-hardening

Buildfix 5 repairs the silent Avalonia startup behaviour found after Buildfix 4 compiled successfully. `TheRadioVault.exe` now shows a lightweight startup window immediately, initialises the existing local database and application service graph in the background, and then replaces that window with the normal Avalonia shell.

If startup cannot complete, Radio Vault keeps a visible error window open with the full exception and a shortcut to the detailed startup log. Failures that occur before Avalonia can create a window produce a native Windows error dialog. The log is stored at `%APPDATA%\TheRadioVault\avalonia-startup-failure.log`.

The earlier lifetime, namespace and Avalonia XAML build fixes remain included. Avalonia remains the default `TheRadioVault.exe`; WPF remains `TheRadioVault.WpfReference.exe`. Database schema 45, LAN capability generation 14, API v1 and all pairing/cache identities are unchanged.
