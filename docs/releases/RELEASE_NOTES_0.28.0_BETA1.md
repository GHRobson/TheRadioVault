# Radio Vault v0.28.0 Beta 1

Beta 1 is the first stabilization build after the Library Truth alpha series.

## Research workflow

- Adds direct decision cards for common metadata-quality issues, including people in multiple roles and broad topic labels.
- Saves choices immediately, advances automatically and supports keyboard shortcuts and guarded undo.
- Keeps the full Metadata editor as a fallback rather than the routine path.
- Fixes `Open affected metadata` so it selects the exact broadcast and focuses the affected field.

## Performance

- Advanced diagnostics opens immediately and loads recent action history asynchronously.
- A full archive audit runs only when explicitly requested.
- Safe-repair and undo rechecks no longer block the UI thread.

## Safety

- Restores metadata and `user_modified` state on undo.
- Rejects stale direct choices when the underlying role or topic has changed.
- Database schema remains 45 and no media format changes are introduced.
