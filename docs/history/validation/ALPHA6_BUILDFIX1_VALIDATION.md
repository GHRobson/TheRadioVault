# v0.27.0-alpha6-buildfix1 validation

## Primary desktop test

1. Build and launch over the existing alpha6 database.
2. Run a full library scan on the desktop first so Roman `I`/`II` parts and AM/PM slot metadata are reparsed.
3. Open Research → Research decisions.
4. Allow automatic triage to finish.
5. Confirm the previous 2,653 decision count falls substantially.
6. Confirm identity-only and source-only entries are shown in automatic history rather than Needs your decision.
7. Inspect 27 February 2015:
   - `... I` is Part 1.
   - `... II` is Part 2.
   - Part 2 is not offered for Part 1 research.
   - a full/alternate Part 1 recording is treated as the same logical broadcast family.
8. Inspect 23 October 2002 Evening show:
   - PM/Afternoon candidates are treated as matching Evening.
   - AM is excluded.
   - an unspecified-slot file is excluded when explicit PM candidates exist.
9. Confirm remaining multipart decisions are limited mainly to records with timed Moments or genuinely different slots.
10. Confirm playback, transcription, portable transcript packages and library navigation still work.

## Safety checks

- No source audio path changes.
- No media deletion or quarantine action is introduced.
- Manual edits are not overwritten.
- Undo remains available for automatic approved links.
