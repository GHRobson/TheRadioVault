# v0.27.0-alpha6 validation

## Validation completed in the source-preparation environment

- Version metadata agrees across `VERSION.txt` and the desktop project.
- Database schema is set to 38.
- Schema-38 reconciliation columns are present in both table creation and migration paths.
- The exact Research Library SELECT (23 columns), reconciliation candidate SELECT (33 columns) and overview SELECT (7 columns) were executed successfully against a synthetic SQLite schema.
- The schema creation SQL was executed successfully against SQLite and exposed the expected triage and action-ledger columns.
- All project files and all 25 XAML files parse as XML.
- All `MainWindow.xaml` and `ResearchReconciliationWindow.xaml` event-handler references resolve to code-behind methods.
- No duplicate `x:Name` values were found in `MainWindow.xaml` or `ResearchReconciliationWindow.xaml`.
- Lexical delimiter and source-structure checks passed across all 173 C# files.
- The grouped triage model, decision views and schema-38 smoke-test markers are present in source.
- Metadata-conflict resolution is wired from **Keep library value** / **Use research value** buttons to a transaction-safe database update, and the attention count combines match decisions with unresolved conflicts without double-counting records.
- Pending-match records expose a direct **Resolve broadcast match…** route into the correct grouped decision.
- A reconciliation failure after file enumeration no longer turns a successful audio scan into a failed scan; the preserved queue remains accessible from Research.
- Source validation was updated from the alpha5 schema-37 assumption to the alpha6 schema-38 contract.

## Build limitation

The preparation environment does not include the .NET 8 SDK, so the complete solution could not be compiled here. Build and runtime acceptance must be completed in Visual Studio 2022 on Windows.

## Required user acceptance

1. Build the complete solution in Release / Any CPU.
2. Open the existing alpha5 database and confirm schema migration without lost playback, research or transcript data.
3. Open Research on the desktop archive and record the before/after **Needs your decision** count.
4. Inspect automatic activity and confirm already-attached candidate noise was dismissed rather than copied or overwritten.
5. Resolve one genuine same-day ambiguity and confirm all candidate rows collapse into one completed decision.
6. Use **Leave research unlinked** and confirm the research remains in the Research Library.
7. Undo one approved automatic decision and confirm it returns as a manual decision without auto-reapplying.
8. Resolve one metadata conflict with **Keep library value**, then another with **Use research value**; confirm the selected record and attention count update immediately.
9. Rescan once and confirm resolved decisions do not return.
10. Confirm no files were moved, renamed or deleted.
