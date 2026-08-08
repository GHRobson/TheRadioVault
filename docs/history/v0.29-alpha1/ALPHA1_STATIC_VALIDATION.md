# v0.29.0 Alpha 1 static validation

The packaged source must satisfy these checks:

- `VERSION.txt`, project `Version` and `InformationalVersion` equal `0.29.0-alpha1-anywhere-foundation`.
- Assembly and file versions equal `0.29.0.0`.
- SQLite `CurrentSchemaVersion` remains 45.
- API v1 retains all v0.28 routes and adds only `server-info` and `bootstrap`.
- Server information does not serialise access tokens, certificate passwords or local paths.
- The web shell uses `radio-vault-anywhere-shell-v26` while audio/artwork caches remain v1.
- C# brace balance, raw-string boundaries, XAML parsing, handler references and source manifest verification pass.
