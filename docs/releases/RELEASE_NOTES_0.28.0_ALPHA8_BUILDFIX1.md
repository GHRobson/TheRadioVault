# Radio Vault v0.28.0-alpha8 buildfix1 — Conflict Forensics

Version: `0.28.0-alpha8-conflict-forensics-buildfix1`

This buildfix corrects the two primary Visual Studio compiler errors in `LibraryTruthConflictForensics.cs`:

- CS0826 at the metadata-field rule declaration: the target-typed record constructors now sit inside an explicitly typed `MetadataFieldRule[]`.
- CS0411 in provenance expansion: both `SelectMany` branches now return `IEnumerable<ProvenanceSnapshot>`, removing the `List<T>` versus array inference ambiguity.

The three CS0006 missing-metadata-file errors were downstream consequences of `TheRadioVault.Services` failing to compile and require no separate change.

No conflict classification, selected-value policy, provenance handling, rehearsal transaction, rollback, schema or export behaviour changed. Database schema remains 44 and Library Truth export schema remains 6.
