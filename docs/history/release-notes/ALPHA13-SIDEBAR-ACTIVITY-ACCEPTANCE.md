# Alpha 13 — Sidebar Activity Status acceptance

## Build

1. Extract the source into a fresh folder.
2. Run `BUILD-AND-RUN.cmd`.
3. Confirm the Avalonia desktop application builds and opens.

## Sidebar activity panel

1. Start a full Library scan from Settings.
2. Navigate away from Settings while the scan is running.
3. Confirm a compact panel remains at the bottom of the sidebar.
4. Confirm it says **Scanning Library**, includes descriptive status text and shows a small progress bar.
5. Confirm the descriptive text changes as the scanner reports later phases.
6. Confirm the old full-width loading bar does not appear across the top of the page.

## Research

1. Preview a research pack and then apply it.
2. Confirm the sidebar describes the preview/import phase.
3. Export a whole-show pack and confirm the sidebar reports the export.
4. Confirm the small spinner inside the Research action button may still appear, but there is no page-wide top bar.

## Other operations

Check at least two of the following:

- open a broadcast and observe **Preparing playback**;
- create or restore a backup;
- run Archive Health;
- refresh the Library or Dashboard;
- perform a Search.

The sidebar panel should disappear automatically when the current operation finishes.

## Regression checks

- Local playback remains fast and progress persists.
- Radio Vault Anywhere remains reachable and can play audio.
- All six show sections and catalogue Research fields remain available.
- Native Avalonia client/server and handoff remain disabled for 1.0.
