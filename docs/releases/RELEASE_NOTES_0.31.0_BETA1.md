# Radio Vault v0.31.0 Beta 1 — WPF-Independence Proof

Version: `0.31.0-beta1-wpf-independence-proof`

Beta 1 freezes the accepted Alpha 6 Buildfix 1 application behavior and adds the formal proof and handoff artifacts required before the Avalonia rebuild.

## Changes

- Added `tools/Test-WpfIndependence.ps1` with a machine-readable report and hard failure on architecture regressions.
- Classified current WPF coupling into explicit Avalonia presentation work packages.
- Added the definitive Avalonia handoff document.
- Updated source validation, release gating and package metadata for the beta channel.
- Retained all Alpha 6 service composition, playback/media, remote-session and remote Now Playing parity behavior.

## Compatibility

Database schema 45, LAN capability generation 14, API v1, pairing, encrypted client caches and all web/cache identities are unchanged. No migration, re-adoption, re-pairing or cache reset is required.
