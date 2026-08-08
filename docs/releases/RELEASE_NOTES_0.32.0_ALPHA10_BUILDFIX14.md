# Radio Vault v0.32.0 Alpha 10 Buildfix 14

Build identity: `0.32.0-alpha10-buildfix14-persistence-export-ui-refinement`

Buildfix 14 is a contained persistence, Research-export and UI-consistency pass over the user-confirmed Buildfix 13 compile/launch baseline.

## Playback persistence

- Radio Vault stops periodic progress writers before taking the shutdown snapshot.
- The live logical position and active broadcast identity are frozen before pause.
- The canonical local database or authoritative server write is awaited and read back for verification.
- Connected Access cache writes are serialized so an older synchronization save cannot follow the final shutdown cache save.
- Server-confirmed mutations that arrive during an in-flight synchronization request are protected from that older response until a later poll catches up.
- Remote Dashboard cache summaries are rebuilt from current broadcast state rather than retaining an older Continue Listening snapshot.
- A centred **Closing RadioVault** overlay remains visible while this bounded shutdown work completes.

## Research pack export

The disabled Export pack action is replaced with the established WPF workflow:

1. Choose a show.
2. Choose **All years** or one year.
3. Choose a destination `.trvpack` file.
4. Export through the local library or authoritative Connected Access server as appropriate.

The quicker Export show action remains available for the currently selected show.

## Interface refinements

- Correct, aligned minimise, maximise/restore and close vector glyphs.
- Sidebar branding no longer includes the separate yellow RV tile.
- Dashboard Favourite actions and persistent-player Favourite, Save Moment, Info, speed and More actions use clean glyph-only presentation.
- Sidebar and grouped action glyphs share consistent visual centres and spacing.
- Favourites is list-only and no longer displays a redundant list-mode control.
- Library-row hearts remain small and contextual; the visible progress bar is half its previous length.

Database schema remains **45**, LAN capability generation remains **14**, and API/pairing/cache identities remain unchanged.
