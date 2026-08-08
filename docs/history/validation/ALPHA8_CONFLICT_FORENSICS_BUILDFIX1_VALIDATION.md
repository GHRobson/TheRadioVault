# Alpha8 Conflict Forensics buildfix1 validation

## Compiler corrections

- Corrected CS0826 in `LibraryTruthConflictForensics.cs` by explicitly declaring `MetadataFieldRules` as `new MetadataFieldRule[]`.
- Corrected CS0411 by normalising both provenance `SelectMany` branches to `IEnumerable<ProvenanceSnapshot>`.
- The three reported CS0006 errors were cascading failures caused by `TheRadioVault.Services` not producing its DLL.
- Added source-validation guards for both primary compiler-failure patterns.

## Static validation completed

- Parsed all 37 project/XAML XML files successfully.
- Confirmed `VERSION.txt`, project `Version`, `InformationalVersion` and the Library Truth parser marker agree.
- Confirmed 97 smoke tests remain registered.
- Confirmed database schema remains 44 and Library Truth export schema remains 6.
- Confirmed the buildfix patch applies cleanly to the original alpha8 source and reproduces the corrected tree.
- Confirmed no `bin`, `obj` or `.vs` directories are included.

Conflict policies, forensic rows, backup creation, disposable transaction, integrity checking and mandatory rollback are unchanged. No live adoption command or live-library mutation path was added. Full compilation and smoke-test execution still require Windows with the .NET 8 SDK.
