# Alpha 10 Build Fix 1 — Static Validation

Target version: `0.28.0-alpha10-guarded-adoption-buildfix1`

## Build failure corrected

The Alpha 10 source declared two unrelated records named `LibraryTruthAdoptionSummary` in the same namespace:

- the established Library Truth readiness summary in `LibraryTruthModels.cs`; and
- the new permanent live-adoption run/audit summary in `LibraryTruthAdoptionModels.cs`.

This produced `CS0101` and `CS8863`, preventing `TheRadioVault.Services.dll` from being generated. The three reported `CS0006` errors were downstream consequences.

The new live-adoption result record is now named `LibraryTruthAdoptionRunSummary`. Its service and WPF consumers were updated. The existing readiness-summary model remains unchanged.

## Static checks performed

- XML, XAML and project files parse as XML.
- C# source files parse with the tree-sitter C# grammar, apart from the pre-existing embedded-JavaScript limitation in `LocalWebServer.cs`.
- No duplicate non-partial C# type declarations were found within a namespace.
- Exactly one `LibraryTruthAdoptionSummary` declaration remains.
- Exactly one `LibraryTruthAdoptionRunSummary` declaration exists.
- Version strings are aligned to the buildfix1 version.
- ZIP integrity was verified after packaging.

A Windows/.NET 8 WPF build is still required on the normal Visual Studio machine.
