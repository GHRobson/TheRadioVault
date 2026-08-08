# v0.28.0-alpha6 Library Truth validation

## Required Windows validation

1. Build the full solution with the .NET 8 SDK.
2. Run all smoke tests.
3. Start Radio Vault and confirm schema 42 initialization succeeds.
4. Build the full desktop shadow library.
5. Confirm the established alpha5 corpus totals remain stable.
6. Open Coverage Evidence and verify direct segment rows and review-only same-date links.
7. Open Adoption Preview and verify one row per canonical broadcast.
8. Export a `.trvtruth` report and upload it for corpus comparison.
9. Confirm the live episode count, playback state, favourites, research and transcripts are unchanged.

## Expected full-corpus shape

Based on the confirmed alpha5 export, alpha6 should produce approximately 7,096 direct segment rows plus 10 review-only inferred coverage rows, and 4,330 adoption-preview rows. Exact totals must be verified by the application export rather than assumed.
