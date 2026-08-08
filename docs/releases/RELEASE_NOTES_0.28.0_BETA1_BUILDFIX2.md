# Radio Vault v0.28.0-beta1-buildfix2

## Saved Moment repair

- Removes genuine duplicate Moment rows using a conservative canonical-broadcast, title, notes and two-second position identity.
- Keeps the earliest original bookmark and leaves distinct Moments untouched.
- Repairs rows duplicated on the same episode and rows copied across retained canonical member episodes.
- Prevents future duplicate insertion from manual saves, metadata-package imports and research reconciliation.
- Resolves Moment playback through the canonical representative episode.

## Retained critical fix

- Includes all buildfix1 protections against listening-progress loss across restart, shutdown, rescan and build changes.

## Compatibility

- Database schema remains 45.
- No audio files are modified.
- Research packs, metadata packages, Library Truth exports, transcripts, web playback and offline formats are unchanged.
