# Radio Vault v0.28.0-alpha14 — Library Truth Completion

Version: `0.28.0-alpha14-library-truth-completion`

Alpha 14 closes the Library Truth migration boundary without forcing unresolved groups into the canonical tables.

## Implemented

- Central playback-plan completeness validation: ordered segments must be contiguous, have valid logical ranges and retain at least one non-missing source.
- Adopted plans remain authoritative; held groups use one explicit deterministic compatibility boundary.
- Recording-selection diagnostics explain whether playback came from guarded adoption or the held-group fallback, including recording identity, role, duration, segments and physical-file count.
- A canonical Library Truth audit snapshot reports adopted and held broadcasts, multipart and incomplete recordings, review-required coverage, missing/cloud-only files, legacy fallback use and invalid preferred identities.
- Recording-specific requests fail closed when their manifest is incomplete or review-required.
- No schema change, media rewrite, automatic held-group promotion or metadata mutation.

## Completion policy

The remaining held groups are not silently "fixed" by Alpha 14. Each remains visible as a held compatibility case until direct evidence or an explicit user decision resolves it. This preserves Alpha 11–13 safety while making every fallback measurable and explainable.

Schema remains 45.
