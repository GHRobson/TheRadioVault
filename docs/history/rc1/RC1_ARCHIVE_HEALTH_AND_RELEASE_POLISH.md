# Radio Vault v0.28.0 RC1

## Purpose

RC1 changes the product from implementation-led settings to a task-led Archive view. It does not change the database schema or canonical archive model.

## Archive Health hub

The Archive section opens immediately and loads its health summary asynchronously. It reports broadcasts, recording files, decisions awaiting attention, unavailable media, preservation coverage and local backup recency. Expensive detailed issue enumeration is cached for five minutes and can be refreshed explicitly.

## Maintenance layout

Archive folders and scanning are the primary controls. Storage, preservation/comparison, backups/metadata packages and file-tag synchronisation remain available in collapsed sections. Library Truth internals remain under Advanced.

## Performance safeguards

- No file-system reconciliation runs on the UI thread.
- Archive summary and detailed health work use generation-safe asynchronous tasks.
- Scan history loads in parallel with detailed health analysis.
- Diagnostic performance timings are written to the application diagnostic log.

## Acceptance

1. Open Settings and confirm the Archive page appears immediately.
2. Confirm the health cards fill without freezing playback or navigation.
3. Open View details and confirm the window remains responsive while checking.
4. Confirm Open decisions reaches Research → Needs your decision.
5. Create a backup and confirm Latest backup refreshes.
6. Expand each archive-maintenance section and confirm all Beta 2 tools remain available.
7. Run normal playback, restart, Moments and web-transfer regression checks.
