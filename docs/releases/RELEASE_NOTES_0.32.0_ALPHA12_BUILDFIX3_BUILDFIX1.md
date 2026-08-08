# Radio Vault v0.32.0 Alpha 12 Buildfix 3 Buildfix 1

Build identity: `0.32.0-alpha12-buildfix3-buildfix1-avalonia-handoff-icon-xaml`

This is a compile-only repair for Alpha 12 Buildfix 3. The new contextual handoff icon used WPF's `StrokeLineJoin` property in Avalonia markup, causing `AVLN2000` at `MainWindow.axaml` line 205. The property is now `StrokeJoin`, which is the Avalonia spelling already used elsewhere in Radio Vault.

The four nullable Research provenance warnings shown by the same build are also removed by normalizing the values before they are persisted. No playback, handoff, database, pairing, cache or library behaviour has otherwise changed.

Database schema remains **45**, LAN capability generation remains **14**, and API v1 identities remain unchanged.
