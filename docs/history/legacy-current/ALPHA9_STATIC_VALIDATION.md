# Radio Vault v0.30.0 Alpha 9 static validation

Target: `0.30.0-alpha9-parity-audit-hardening-buildfix2`

The package is statically checked for:

- XML parsing of every XAML and project file.
- Matching XAML event-handler references.
- Balanced C# delimiters and terminated literals/comments.
- Complete `IWebArchiveProvider` implementations, including test doubles.
- Version, schema 45 and capability-generation 14 consistency.
- Presence of the parity and Research workspace routes, DTOs, server handlers, client calls and normal-UI integrations.
- Presence and resolution of the shared `WriteJsonAsync<T>` response helper used by the new federation endpoints.
- Remote Metadata Studio and artwork-cache integration.
- Guards preventing remote Research/metadata actions from writing to the local database.
- Role-based **server** / **remote client** terminology in current user-facing surfaces.
- Source-manifest hashes and ZIP integrity.

Validated package inventory:

- **28** XAML files parsed.
- **396** XAML event references resolved.
- **233** C# files passed delimiter and literal/comment termination checks.
- **493** non-manifest source files are sealed by the source manifest.

This environment cannot compile or run the Windows WPF application because the .NET SDK and Windows desktop runtime are unavailable. Visual Studio compilation and the live two-machine checklist remain mandatory.
