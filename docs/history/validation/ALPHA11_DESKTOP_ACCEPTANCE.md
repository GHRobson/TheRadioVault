# Alpha 11 desktop acceptance checklist

Build the solution in Visual Studio, launch it against a copy of the adopted
GRAHAM-PC database, and confirm the following before treating Alpha 11 as the new
baseline.

## Canonical identity

- Dashboard shows **4,330 broadcasts**, not 7,169 files or 4,363 legacy rows.
- Library contains exactly **4,330 unique broadcast listings**.
- Exactly **25** listings show `Needs attention`.
- Collection, year and month totals agree with the Library.
- Archive Health still reports **7,169 physical files** separately.
- Search returns one result per canonical broadcast.
- An old saved alias episode ID opens the correct visible broadcast.

## Multipart playback

- Select a known two- or three-part adopted broadcast; it appears once.
- Playback reaches the end of part one and continues into part two automatically.
- The progress bar and duration represent the complete broadcast.
- Back/forward seeking can cross a part boundary.
- Closing and reopening the app resumes inside a later part correctly.
- A Moment created in a later part returns to the same logical position.
- Completion is recorded only after the final part.
- A missing preferred source falls back to another available candidate for that
  segment or shows a clear unavailable-part warning.

## Compatibility and queue

- A queue entry created before Alpha 11 from an alias appears as one canonical
  broadcast.
- New queue entries store and reopen the canonical representative.
- Favourite, mark played and mark unplayed stay consistent after Library reload.
- Remote playback handoff preserves the complete logical position.

## Regression safety

- A no-change library scan leaves the visible count at 4,330.
- Retired aliases do not return as Library rows.
- The adoption remains completed and commit-verified.
- SQLite integrity remains `ok` with zero foreign-key violations.
- No audio file is renamed, moved, deleted or rewritten.
