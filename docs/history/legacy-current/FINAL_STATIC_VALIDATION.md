# Radio Vault v0.29.0 Final Static Validation

The final package must satisfy these non-runtime gates:

- `VERSION.txt`, project `Version` and `InformationalVersion` all equal `0.29.0`.
- Assembly and file versions remain `0.29.0.0`.
- Database schema remains 45.
- Capability generation remains 7 and desktop web-shell generation remains 10.
- The secure shell cache is `radio-vault-anywhere-shell-v33`; audio and artwork cache identities remain at v1.
- IndexedDB remains version 2.
- Only `README.md`, `BUILDING.md` and `CHANGELOG.md` are present as root Markdown files.
- All XAML and project XML are well formed.
- Embedded client and service-worker JavaScript pass syntax validation.
- The source manifest covers every packaged file except the manifest itself.
- No accepted RC1 Anywhere capability is removed by the final promotion.

A Windows/.NET `release-gate.ps1` run remains authoritative for compilation and capability smoke tests.
