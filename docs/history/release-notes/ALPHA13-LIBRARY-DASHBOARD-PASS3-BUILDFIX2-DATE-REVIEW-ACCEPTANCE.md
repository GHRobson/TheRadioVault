# Alpha 13 Pass 3 Buildfix 2 — Catalogue date review acceptance

## Purpose

This build fixes the gap between researched RBI/Unmasked/Ron & Ron dates and the Library. Dates that are not safe to adopt automatically now appear as explicit human decisions in Research.

## Build

1. Extract into a fresh folder.
2. Run `BUILD-AND-RUN.cmd`.
3. Confirm the Avalonia application opens normally.

## Date review

1. Open **Research** and choose **Date review**.
2. Confirm dated or partially dated RBI, Unmasked and Ron & Ron records appear.
3. Confirm each selected item shows:
   - proposed date or partial date clue;
   - proposed date type;
   - confidence and source count;
   - basis/provenance;
   - the current Library date, or **Undated**;
   - release and recording dates separately;
   - a same-day collision warning when appropriate.
4. Approve one exact date as the Library date and confirm it appears in the correct Library year/month/day.
5. Choose **Keep as recording date** on another item and confirm it remains undated in the Library.
6. Choose **Keep as release/archive date** on another item and confirm it remains undated in the Library.
7. Choose **Leave Library item undated** and confirm the decision disappears from the pending queue.
8. Enable **Show resolved decisions**, reopen a decision, and confirm it returns to the pending queue.
9. For a date that an earlier Pass 3 build automatically adopted, reject it as recording/release-only and confirm the Library date is removed.
10. Confirm older catalogue research that only contains a top-level date or a date clue in the original filename also appears in the review queue.

## Research packs

1. Export the affected show after making decisions.
2. Re-import the pack into a disposable copy of the database.
3. Confirm schema 5 preserves date-review status, selected date, prior Library state and review timestamp.
4. Confirm an unapproved exact date is offered for review rather than silently changing Library chronology.

## Regression

- Local playback starts normally.
- Radio Vault Anywhere starts and streams normally.
- Library mini panels remain simplified for RBI, Unmasked and Ron & Ron.
- Hide Completed still works in List, Year and Month views.
- Sidebar activity status remains available for long operations.
