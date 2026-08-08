# Radio Vault v0.28.0-alpha11 — Canonical Library Cutover

Version: `0.28.0-alpha11-canonical-library-cutover`

Alpha 11 makes the adopted Library Truth hierarchy the normal desktop read and
playback model. The Dashboard, Library, collection summaries, year/month
navigation and web broadcast list now receive one item per canonical broadcast
rather than one item per physical file or surviving legacy episode row.

## Added

- `CanonicalLibraryQueryService`, anchored to the exact completed,
  commit-verified Alpha 10 adoption run rather than whichever shadow analysis
  happened to run most recently.
- One canonical projection spanning adopted broadcasts and the 25 intentionally
  held Library Truth groups.
- Legacy episode ID resolution for Moments, queue entries, transcripts, search,
  archive-health links, research links and remote playback commands.
- Preferred-recording playback plans with ordered multipart segments and
  physical-file candidates.
- Seamless desktop transition between ordered parts of the preferred recording.
- Logical broadcast-timeline seeking, resume, completion, Moments and remote
  playback handoff across multipart boundaries.
- Visible recording, part and physical-file counts on grouped Library rows.
- A `Needs attention` badge and reason for review-only or blocked groups.

## Changed

- Dashboard totals now count canonical broadcasts.
- Library rows are grouped by canonical key; multipart files and alternate
  recordings no longer create duplicate top-level rows.
- Collection, year and month counts derive from the same canonical snapshot.
- Search de-duplicates held legacy rows and resolves results to their canonical
  representative.
- Favourite and played/unplayed actions route through canonical identity.
- Existing queue entries that reference retired aliases resolve to the visible
  broadcast, and new entries store its representative identity.
- Playback progress is stored against the complete logical broadcast timeline,
  not reset at each physical-file boundary.
- The web broadcast list inherits the canonical projection through the shared
  database service.

## Expected GRAHAM-PC counts

- 4,330 visible canonical broadcast groups
- 4,305 adopted broadcasts
- 25 groups needing attention: 15 review recommended and 10 blocked
- 6,736 recording variants
- 7,106 coverage rows
- 7,169 physical files

## Deliberate boundary

Alpha 11 implements preferred-recording multipart playback on the Windows
desktop. It does not yet add an alternate-recording selector, canonical
multi-file download manifests for the web/offline player, or assembled
multi-source transcript playback. Held groups whose coverage remains unresolved
continue to use their deterministic representative file as the safe compatibility
path.

Schema remains 45. No audio files are renamed, moved, deleted or rewritten.
