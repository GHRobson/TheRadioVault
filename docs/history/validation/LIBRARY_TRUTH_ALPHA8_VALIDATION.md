# Library Truth alpha8 validation plan

## Accepted baseline

The alpha7 desktop rehearsal completed all 4,305 eligible broadcasts with exact preview counts, zero foreign-key violations, SQLite integrity `ok`, backup restore `ok` and identical source/rollback fingerprints. It reported 7,165 coarse metadata/reference differences across 2,195 Ron & Fez broadcasts.


## Buildfix baseline

Run this validation with `0.28.0-alpha8-conflict-forensics-buildfix2`. Buildfix1 corrected the two conflict-service type-inference failures; buildfix2 corrects only the conflict-details UI string literal. Database schema 44, export schema 6 and all conflict policies remain unchanged.

## Alpha8 acceptance criteria

1. Preserve every accepted parser, broadcast, recording, file, coverage and adoption-preview count.
2. Preserve alpha7's exact structural and state-migration operation counts.
3. Persist one forensic row per meaningful field/reference difference.
4. Include all candidate values, source episode IDs and matching provenance.
5. Auto-resolve only deterministic, lossless or decisively evidenced cases.
6. Preserve alternate values even when a winner is selected.
7. Leave comparable contradictions unresolved.
8. Maintain zero foreign-key violations, SQLite integrity `ok`, validated backup restore and identical rollback fingerprints.
9. Keep all 15 review-recommended and 10 blocked broadcasts outside the rehearsal.
10. Keep live adoption disabled.

The full desktop schema-6 `.trvtruth` export is required before alpha8 can be promoted.
