# Radio Vault v0.29.0 RC1 Static Validation

Validated package version: `0.29.0-rc1-anywhere-release-candidate`

## Passed in the packaging environment

- `VERSION.txt`, project `Version`, and `InformationalVersion` match.
- 36 XAML and project XML files parsed successfully.
- The embedded Anywhere client JavaScript passed `node --check`.
- The embedded service-worker JavaScript passed `node --check`.
- Required RC1 recovery, update, accessibility and capability markers are present.
- Capability generation is 7 and desktop web-shell generation is 10.
- The app-shell repair path deletes only `radio-vault-anywhere-shell-*` caches; it does not target the v1 audio or artwork caches.
- No build-output directories were included in the source tree.
- Source-manifest hashes and ZIP integrity were verified after packaging.

## Pending on Windows

The packaging environment does not contain the .NET SDK or PowerShell, so the full solution build, `validate-source.ps1`, release-gate smoke tests and real iPhone upgrade/recovery pass remain to be run on the user's Windows development machine.
