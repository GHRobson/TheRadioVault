# Radio Vault v0.28.0-alpha15 — Unified Research Workspace

Version: `0.28.0-alpha15-unified-research-workspace`  
Schema: `45`

## Purpose

Metadata Studio and Research existed as separate destinations even though both modify or curate the same durable broadcast knowledge. Alpha 15 combines them into one **Research & Metadata** workspace so editing, importing, auditing and conflict resolution follow one mental model.

## Unified workspace

The top-level Metadata Studio navigation entry has been retired. Its metadata editor is now a section within Research & Metadata alongside:

- overview and archive coverage;
- all researched broadcasts and missing recordings;
- rapid decisions;
- research quality;
- sources and provenance;
- import history;
- research-pack import and export.

Existing editor behaviour and pack formats are preserved. This is a navigation and workflow consolidation, not a metadata migration.

## Rapid decisions

The rapid-decision surface is intentionally fixed-height and does not wrap the active choice in a scrolling record view. It shows:

- show, date, slot and part identity;
- the affected field;
- source count and confidence;
- the current library value;
- the saved research value;
- one large action under each value.

Choosing either value saves immediately and advances to the next unresolved conflict. Routine decisions do not display a confirmation dialog. `S` skips without changing data. `Z` safely reopens the last decision made during the current session, provided neither side has subsequently changed.

Errors and unsafe undo conditions still use explicit dialogs because they require attention.

## Match decisions

Broadcast-match decisions remain distinct from field-value conflicts, but are opened from the same Rapid decisions page. Routine apply and dismiss operations no longer ask for a second confirmation; the decision window itself is the confirmation surface. Historical undo remains guarded.

## Documentation hygiene

Only `README.md`, `BUILDING.md` and `CHANGELOG.md` remain as Markdown files in the source root. Stable guides, release notes, old validation reports and historical patches are retained under `docs/`. `validate-source.ps1` enforces this layout for future packages.

## Safety boundary

- No schema change.
- No automatic conflict resolution.
- No audio-file move, rename or deletion.
- Alpha 14 canonical playback completeness and audit behaviour remain intact.
- Every rapid decision continues to use the existing transactional conflict-resolution service and event propagation.
