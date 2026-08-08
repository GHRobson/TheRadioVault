# Radio Vault v0.28.0-beta1-buildfix3

Buildfix 3 corrects the two CS0173 compiler errors in `DatabaseService.GetPlaybackState` by explicitly using nullable `DateTime?` playback timestamps. It otherwise preserves buildfix1's listening-progress protections and buildfix2's Moment deduplication and idempotent import safeguards. Database schema remains 45.
