# Radio Vault v0.30.0 Alpha 9 Buildfix 1 static validation

Target: `0.30.0-alpha9-parity-audit-hardening-buildfix1`

Buildfix scope:

- Restores `LocalWebServer.WriteJsonAsync<T>`, which was referenced by the Alpha 9 parity and Research workspace partials but absent from the packaged source.
- Uses `JsonSerializer.SerializeToUtf8Bytes(..., JsonOptions)` and the established `WriteBytesResponseAsync` path.
- Preserves GET/HEAD behaviour, JSON content type and `Cache-Control: no-store`.
- Confirms that all three Alpha 9 `WriteJsonAsync` call sites now resolve within the shared partial class.
- Confirms that no database schema, LAN capability, route or contract changes were introduced.

The Windows WPF solution still requires a Visual Studio build on Windows for final compile and runtime acceptance.
