# Radio Vault v0.28.0-alpha15 — Unified Research Workspace

Alpha 15 turns metadata editing and research curation into one coherent workspace and replaces the slow field-conflict workflow with a rapid, no-scroll decision surface.

## Added

- Unified Research & Metadata workspace.
- Metadata editor within Research navigation.
- Research-pack import/export within the same workspace.
- Side-by-side rapid conflict decisions with immediate advance.
- Keyboard controls: `1` keep library, `2` use research, `S` skip and `Z` undo.
- Guarded single-step undo for the last rapid field decision.
- Consolidated decision counters and match-decision entry point.

## Changed

- Removed the separate top-level Metadata Studio destination.
- Removed routine confirmation dialogs from field conflict choices.
- Removed routine confirmation dialogs from match apply/dismiss actions.
- Organised accumulated release documentation under `docs/`.
- Added source-layout validation to keep future package roots clean.

## Preserved

- Database schema 45.
- Existing `.trvpack`, `.trvmetadata`, `.trvtruth` and `.trvdiagnostic` formats.
- Alpha 14 canonical playback, held-group and Library Truth audit behaviour.
- Confirmation for destructive or genuinely unsafe actions.
