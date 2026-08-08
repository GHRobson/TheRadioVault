# Radio Vault v0.28.0-alpha8 — Conflict Forensics

## Added

- Field-level rehearsal-conflict rows keyed by canonical broadcast and metadata field.
- Full candidate-value and provenance evidence for every recorded difference.
- Deterministic classifications for canonical identity, equivalent formatting, empty versus populated, mergeable lists, specific over placeholder, protected provenance, manual edits and quality-ranked winners.
- Explicit unresolved state for genuinely comparable contradictory values.
- Preserved-alternate counts and selected-value evidence.
- Headline-review comparison and remaining transcript/voice alias-reference forensics.
- Conflict Forensics tab with value, provenance and policy detail.
- Schema-6 `.trvtruth` export containing the complete forensic ledger.
- Database schema 44 and focused regression coverage.

## Safety

- The policies run only inside the disposable adoption-rehearsal transaction.
- The transaction is always rolled back and compared with the source logical fingerprint.
- Provenance is credited only when its stored value matches the candidate value.
- Every alternate remains in the forensic ledger even when a deterministic winner exists.
- No live adoption command exists.
