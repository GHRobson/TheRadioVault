# Alpha 13 Pass 3 — Library, Dashboard and Catalogue Dates

## Library mini panel

1. Select a normal Ron & Fez or Bennington broadcast.
2. Select an Unmasked item, an RBI item, and a Ron & Ron item.
3. Confirm the compact panel uses the same everyday sections for every show:
   summary, people and topics.
4. Confirm provenance, original filename, catalogue number, research notes and
   other deep catalogue fields are absent from the compact panel.
5. Open Full Broadcast Information and confirm the deep catalogue fields remain.

## Catalogue dates

1. Rescan the RBI, Unmasked and Ron & Ron folders.
2. Confirm filenames containing a real full date use that exact date.
3. Confirm a filename containing only `2015` remains a partial `2015` clue and
   is not assigned 1 January or any other invented day.
4. Confirm `October 1996` remains a month/year clue.
5. Export an All years research pack and confirm partial clues are present in
   `research.catalogue.originalReleaseDate` while `broadcastDate` remains empty.
6. Import a pack with an exact original release date such as `2015-04-17` and
   confirm the matched Library item becomes dated after refresh/restart.

## Hide Completed

1. Use Hide Completed in List view.
2. Switch to Grid view and confirm the toggle remains visible.
3. Confirm year and month cards exclude completed broadcasts while enabled.
4. Enter a month and confirm completed rows remain hidden.
5. Disable it and confirm the original totals return.

## Regression

- Local playback starts, pauses, seeks and persists.
- Playing rows and progress update live.
- Dashboard Continue Listening updates during playback.
- Radio Vault Anywhere still starts and streams.
- Research import/export remains schema-4 compatible.
- Native desktop federation and handoff remain disabled.
