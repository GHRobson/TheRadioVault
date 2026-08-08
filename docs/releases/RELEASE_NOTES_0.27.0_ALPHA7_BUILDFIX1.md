# Radio Vault v0.27.0-alpha7 buildfix 1 — Settings and Preservation Performance

Version: `0.27.0-alpha7-buildfix1-settings-and-preservation-performance`  
Database schema: **39**

## Fix

The alpha7 Settings page was performing expensive archive work as part of navigation. Opening Settings reconciled the storage state of every registered file, and opening Preservation ran repeated whole-library evidence queries synchronously on WPF's UI thread. This was barely visible on the laptop but caused multi-second apparent freezes on the 7,000-file desktop archive.

Buildfix 1 changes that behaviour:

- Settings opens without touching every audio path.
- Storage and Preservation are loaded only when selected.
- Their summaries run on background threads and show a loading state while the page stays interactive.
- Filesystem reconciliation runs only after the explicit **Refresh storage information** or **Recalculate archive information** command.
- Explicit filesystem checks also run off the UI thread.
- Preservation totals now use one aggregate table pass and one grouped duplicate-evidence query instead of repeated scans and a correlated per-row probe.
- Concurrent refresh generations prevent an older result replacing a newer one.

## Preserved behaviour

- Deep preservation scanning remains explicit, resumable and non-destructive.
- Manifest export and cross-PC comparison are unchanged.
- No audio is moved, renamed, quarantined or deleted.
- Database schema remains 39.
