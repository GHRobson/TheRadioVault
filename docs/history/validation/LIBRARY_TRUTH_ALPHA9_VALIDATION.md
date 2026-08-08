# Library Truth alpha9 full-corpus validation

Build and run `0.28.0-alpha9-policy-refinement-buildfix1` against the same 7,169-file GRAHAM-PC desktop library used for alpha5–alpha8.

Buildfix1 changes only local-variable initialization in the conflict-policy evaluator so Visual Studio can prove every result field is assigned. Expected corpus results and all safety invariants are unchanged.

## Required invariants

- 7,169 physical files.
- 7,091 current live episode rows.
- 4,330 proposed canonical broadcasts.
- 4,305 adoption-ready, 15 review-recommended and 10 blocked.
- 6,736 recording variants.
- 7,106 coverage rows: 7,096 direct and 10 review-only.
- 4,330 adoption previews.
- Rehearsal structural counts remain exactly:
  - 4,305 canonical writes
  - 6,685 recording writes
  - 7,035 segment writes
  - 7,035 coverage writes
  - 2,728 file reassignments
  - 2,728 alias retirements
- Zero foreign-key violations.
- SQLite integrity `ok`.
- Backup restore `ok`.
- Source and rollback fingerprints identical.

## Conflict-policy expectation

Alpha8 recorded 2,770 unresolved rows. The accepted alpha8 export shows that alpha9 should deterministically remove:

- 1,605 canonical STANDARD-slot false positives;
- 887 generated broadcast-era alternatives;
- 137 artwork asset alternatives;
- 71 recording-level broadcast-variant labels;
- 54 filename-title/decisive-title cases;
- 4 station decisions previously blocked by episode-wide `user_modified`.

The expected remainder is approximately **12 unresolved rows across 11 broadcasts**:

- five genuinely comparable headline choices;
- six genuinely comparable summaries;
- one protected empty-versus-populated summary.

Every resolved and unresolved alternate must remain present in the forensic ledger. Any materially different count must be explained by the exported field/classification distribution rather than accepted silently.

## Procedure

1. Build the complete solution and run the smoke tests.
2. Open Library Truth and run a fresh full shadow analysis.
3. Run **Run adoption rehearsal…**.
4. Confirm rollback verification and all integrity checks.
5. Export the schema-6 `.trvtruth`.
6. Compare the export against `RadioVault-Library-Truth-GRAHAM-PC-2026-07-19-2334.trvtruth`.
7. Do not introduce or run any committed/live adoption route.
