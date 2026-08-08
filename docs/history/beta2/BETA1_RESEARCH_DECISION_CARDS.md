# v0.28.0 Beta 1 — Research decision cards

Beta 1 begins stabilization of the v0.28 Library Truth line. It does not change database schema or canonical media behaviour.

## Fast metadata decisions

`Needs your decision` now resolves common quality findings in place instead of routing every issue through the full Metadata editor.

- A person assigned to several roles can be kept in all roles or reduced to one role with one click.
- Broad topic labels can be kept or removed directly.
- Intentionally generic summaries can be acknowledged so the same issue does not return.
- Number keys choose an option, `S` moves on, `E` opens the exact field, and `Z` undoes the latest choice.
- Decisions are stored immediately, advance the queue without confirmation prompts, and remain guarded by snapshot-based undo.

Ordinary research-versus-library conflicts retain the existing side-by-side cards.

## Exact metadata navigation

`Open affected metadata` carries the episode identity, field and affected value into the Metadata editor. The target broadcast is inserted into the visible list even when it falls outside the normal first 1,000 results, selected, scrolled into view, and the relevant field is focused with the affected text highlighted.

## Responsive diagnostics

Opening `Advanced diagnostics` no longer launches a full archive audit. The panel appears first, then its short action history loads on a worker thread. The expensive audit runs only after `Run diagnostics` or an explicit recheck. Safe-repair and undo re-audits also run away from the UI thread.

## Safety boundary

- Database schema remains 45.
- No audio files are modified.
- Library Truth adoption, canonical playback, transcript, web and offline formats are unchanged.
- Every direct metadata mutation records before/after snapshots and an auditable action.
