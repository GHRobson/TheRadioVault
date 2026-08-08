# Alpha6 adoption-preview static validation

Target version: `0.28.0-alpha6-adoption-preview`  
Database schema: **42**  
Library Truth export schema: **4**

## Checks completed in the packaging environment

- Version markers agree across `VERSION.txt`, the desktop project and Parser V3.
- All **192 C# files** pass lexical delimiter/string/comment balance checks.
- All **37 XAML/project XML files** parse successfully.
- The Library Truth window has no duplicate control names or missing event handlers.
- All **95 registered smoke-test names** are unique and every method-group registration resolves to a test method.
- Schema 42 SQL creates `library_truth_coverages` and `library_truth_adoption_previews` successfully.
- Representative coverage/adoption insert, filter, summary and cascade-cleanup SQL was executed successfully with SQLite.
- The new engine code contains no `INSERT`, `UPDATE` or `DELETE` operation against live `episodes` or `media_files` rows.
- New global filters are applied through the existing canonical-broadcast filter contract for recordings, coverage rows and adoption previews.
- `ALPHA6_CHANGESET.patch` applies cleanly to the packaged alpha5 baseline with `patch --dry-run -p1`.

## Expected full-desktop shape

The confirmed alpha5 export implies **7,096 direct recording-segment coverage rows** before inferred relationships. Alpha6 should add approximately **10 review-only same-date coverage rows** and exactly **4,330 adoption-preview rows**. The complete Windows run remains the authority for the final totals.

## Environment limitation

The packaging environment has no .NET 8 SDK or PowerShell runtime. Compilation, `validate-source.ps1`, the 95 smoke tests and the full 7,169-file corpus run must therefore be completed on the user's Windows development machine before alpha6 is promoted.
