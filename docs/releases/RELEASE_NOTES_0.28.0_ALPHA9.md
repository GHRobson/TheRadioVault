# Radio Vault v0.28.0-alpha9 — Conflict Policy Refinement

Version: `0.28.0-alpha9-policy-refinement`

## Purpose

Alpha8 proved the forensic ledger but surfaced 2,770 review rows, including 1,605 canonical STANDARD-slot false positives and large groups of generated taxonomy, asset and recording-level metadata that should not require human arbitration.

Alpha9 applies conservative field-specific policies while preserving every alternate value and all matching provenance.

## Policies

- Blank persisted `broadcast_slot` is accepted as the canonical STANDARD representation when the canonical broadcast row exists.
- Multipart/archive-capture labels are recognised as Recording/Segment evidence and cleared from broadcast-level `broadcast_variant`.
- Filename-derived part/date titles are cleared when no descriptive title exists, or replaced by the sole descriptive title.
- Generated `broadcast_era` alternatives receive a deterministic specificity-ranked winner.
- Artwork alternatives retain the provisional survivor's asset and preserve all other paths.
- Episode-wide `user_modified` is weak supporting evidence only; field-level manual/protected provenance remains authoritative.

## Safety

The validated backup, disposable clone, exact operation-count checks, foreign-key/integrity checks and mandatory rollback are unchanged. Database schema remains 44, export schema remains 6, and live adoption remains disabled.
