# Radio Vault v0.32.0 Alpha 4 Buildfix 2

Build identity: `0.32.0-alpha4-buildfix2-library-row-polish-responsive-overscroll`

This focused interaction and Library-layout buildfix retains the accepted Alpha 4 favourites, Queue, Moments, playback and per-show navigation workflows.

## Changes

- Elastic overscroll ignores tiny trailing trackpad deltas and begins returning on the next render frame after meaningful input stops.
- The return spring is faster while remaining single-owner and jitter-free.
- Collection-specific Library views no longer repeat the show name under every row.
- The Structure column is removed from the Library list.
- Playback controls move to the left edge of each row, appear only while that row is hovered, and show Pause for the currently playing broadcast.
- Dates use a bold two-line day/date treatment.
- The search glyph is larger and centered.

Database schema remains 45. LAN capability generation remains 14. API v1 and pairing/cache identities are unchanged.
