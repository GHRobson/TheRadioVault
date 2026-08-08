# Radio Vault v0.32.0 Alpha 10

Build identity: `0.32.0-alpha10-buildfix15-buildfix2-presentation-log-boundary`

Buildfix 13 preserves the accepted visual work while correcting the remaining playback and shell issues. Manual playhead dragging now has an explicit gesture lifecycle and confirmed engine seek, shutdown freezes the last trustworthy logical position before persistence, and completed broadcasts can become resumable replays without losing completion history. The duplicate top chrome strip is removed, Connected Access is represented by a compact status dot, and Favourite/Moments icons now share one consistent vector treatment across the app.

Alpha 10 is a visual-identity and workflow-parity recovery release. It restores Radio Vault yellow, selectable appearance modes, live listening presentation, denser Research editing, guarded Research pack transfer, and explicit multi-device playback ownership.

## Highlights

- Reliable drag-to-seek with tunnel event handling, live target capture, pointer-capture fallback and post-seek confirmation.
- Trusted shutdown progress freeze so reopening resumes from the latest observed position rather than the session's original start position.
- Safe replay of completed broadcasts as new in-progress resumable sessions while historical completion counts remain intact.
- A top-reaching sidebar and content-first custom chrome with no reserved full-width title strip.
- Compact green/yellow/red Connected Access status dot beside the integrated window controls.
- Shared 22-pixel Favourite and Moments vector icons across Library, Now Playing and the persistent player.
- Scroll-activated top surface/divider that remains invisible while a page is at its starting position.
- Yellow-led System, Light and Dark Avalonia themes with a restrained charcoal/slate dark-surface hierarchy.
- Muted-yellow Fluent selection states for Library toggles, selected rows and volume, replacing the remaining teal.
- A narrower, responsive Library progress column that protects broadcast-title space at smaller window sizes.
- Consistent semantic navigation/playback icon colours and a bookmark-with-time Moments symbol.
- Research defaults to Needs attention with rounded filters and a reassuring all-clear state.
- Integrated custom window chrome with Connected Access status beside minimise, maximise/restore and close controls.
- WPF-inspired Dashboard balance with continuation content beside Surprise Me and compact, colour-coded listening statistics.
- Dashboard On This Day topics and centred timed pagination dots.
- Matching Recently Added and Unheard Broadcasts discovery lists.
- Live Dashboard and Library playback progress without manual refresh.
- Hearts, playing-row highlighting, compact progress, framed Library lists and a true square-tile year/month archive grid.
- Purposeful outlined cards, rows and detail panes with density controlled through spacing rather than missing boundaries.
- Fixed-width yellow Border-keyline sidebar selection and direct Library row information actions.
- Left-aligned interactive All → year → month archive breadcrumbs, with the full details panel retained beside month/day lists.
- Content-owned headings for Library/Favourites, Moments, Now Playing and Research rather than detached fixed title strips.
- Metadata-first Now Playing layout with a 350-pixel right-side Up Next rail and no duplicate transport/playhead controls.
- A roomier persistent player with larger Favourite/Info actions and one-click Save Moment.
- Dashboard On This Day people/topic pills and a clearly named secondary Up next list.
- Hosts, guests, callers and mentioned people displayed as grouped pills beside topic pills.
- Local and server-backed Research pack import/export, including audited replacement/clear semantics for explicit `authoritative_audit` packs.
- Named playback endpoints, stable client identity, server-owned playback lease and visible Continue on this device actions.
- Generic web/mobile ownership display and stale-writer protection so phone, laptop and server cannot compete over progress.
- Six-direction server/laptop/iPhone acceptance matrix.

The database remains schema 45. LAN capability generation remains 14. API v1 and all pairing, certificate and cache identities are unchanged.
