# Research & Metadata engine

The durable research store remains available even when audio is absent. Unattached records appear to users as **Broadcasts to find**, a positive archive-discovery list rather than a missing-file warning.

## Everyday workflow

The Research & Metadata workspace contains only:

- **Needs your decision** — rapid, actionable cards with immediate save, keyboard choices and guarded undo.
- **Broadcasts to find** — researched broadcasts not currently represented in the archive.
- **Metadata editor** — the fallback for free-form or genuinely complex changes.

Common conflicts are resolved in place. **Open affected metadata** carries the exact broadcast and affected field into the editor, including records outside the normal first-page result limit.

## Quality boundary

Safe deterministic maintenance can run automatically or on explicit recheck. Corpus-level patterns such as repeated boilerplate summaries are aggregated into diagnostics rather than multiplied into thousands of fake decisions. Only issues with a clear editable destination appear in the attention count.

Heavy catalogues, histories and advanced diagnostics load lazily and asynchronously. Quality analysis uses bulk database snapshots rather than per-record queries.

## Import, provenance and rollback

Research-pack imports retain provenance and reversible history. Rollback restores a field only when its current normalized value still matches the value written by that import, protecting later manual work. Safe repairs and rapid decisions store before/after evidence and refuse undo when subsequent edits would be overwritten.
