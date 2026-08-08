# v0.28.0-alpha7 Library Truth validation

1. Build and launch `0.28.0-alpha7-transactional-rehearsal-buildfix1` over the accepted alpha6 database.
2. Confirm schema 43 initialization succeeds and creates a pre-upgrade backup.
3. Confirm the latest Library Truth analysis still reports:
   - 7,169 physical files
   - 7,091 live records
   - 4,330 canonical broadcasts
   - 4,305 adoption-ready
   - 15 review recommended
   - 10 blocked
   - 7,106 coverage rows, including 10 review-only rows
   - 4,330 adoption previews
4. Open Library Truth and select **Run adoption rehearsal…**.
5. Confirm a retained backup named `RadioVault-before-adoption-rehearsal-*.db` appears in the Backups directory and independently validates as SQLite integrity `ok`.
6. Confirm the rehearsal loads all 4,305 eligible previews and completes the expected structural operations:
   - 4,305 canonical broadcast writes
   - 6,685 recording writes
   - 7,035 segment writes
   - 7,035 direct coverage writes
   - 2,728 media-file reassignments
   - 2,728 mapped alias retirements
   - zero per-preview expected-versus-actual operation mismatches
7. Confirm the result reports:
   - zero foreign-key violations
   - SQLite integrity `ok`
   - rollback verified
   - identical source and rollback logical fingerprints
8. Inspect the Rehearsal Results tab. Policy conflicts may be present, but no unexpected transaction failure, skipped preview or missing state is acceptable.
9. Confirm normal library browsing, playback, favourites, Moments, research and transcripts remain unchanged after the rehearsal.
10. Export a new `.trvtruth`. It should use schema 5 and include the rehearsal summary and all 4,305 per-broadcast result rows.
