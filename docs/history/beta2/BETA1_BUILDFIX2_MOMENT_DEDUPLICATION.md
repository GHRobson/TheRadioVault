# Beta 1 buildfix 2 — saved Moment integrity

Version: `0.28.0-beta1-buildfix2`  
Database schema: `45`

## Reported failure

The Moments page displayed exact or near-exact copies of manually created bookmarks. The supplied metadata export confirms that the duplication is stored in the database rather than being only a rendering problem. It contains 20 Moment rows, including six redundant copies across four conservative duplicate clusters; the repair should leave 14 distinct Moments. Examples include:

- **Ron grateful for Chris** on 17 June 2026 at positions differing by 87 ms;
- **The old slave hanging tree** on 22 June 2026 stored four times within a 945 ms window;
- an imported zero-time research Moment stored twice;
- the same research Moment attached to two retained physical episode members of one canonical broadcast.

## Repair contract

On the first shared Moment query in a process, Radio Vault performs one guarded, transactional repair. Rows are considered duplicates only when all of the following are true:

1. they resolve to the same canonical broadcast identity;
2. their titles match after case and whitespace normalisation;
3. their notes match after case and whitespace normalisation;
4. their positions are no more than two seconds apart.

The earliest-created row is retained as the original. Other rows in that conservative cluster are deleted atomically. Different titles, different notes and Moments more than two seconds apart are always preserved.

## Future prevention

- New manual saves first search all retained members of the canonical broadcast.
- The canonical representative episode receives the new row.
- Repeating a save within the duplicate boundary returns the existing Moment rather than inserting another row.
- Metadata-package imports and research reconciliation use the same idempotent path.
- Moment queries return the canonical representative episode ID so bookmarks remain playable after Library Truth remapping.

## Safety boundary

No audio files are touched. No database migration is required. The repair does not merge free-form notes or semantically similar but differently worded Moments.
