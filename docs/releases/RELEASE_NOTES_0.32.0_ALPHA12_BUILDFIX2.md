# Radio Vault v0.32.0 Alpha 12 Buildfix 2

Build identity: `0.32.0-alpha12-buildfix2-live-progress-consistency`

## Purpose

This buildfix removes stale listening progress from the Avalonia Dashboard and Library. The visible percentage, completion state and Continue Listening membership now come from the same live authoritative playback projection used by Now Playing rather than from whichever cached page snapshot happened to load first.

## Live progress authority

- While this desktop owns playback, the local decoder's current logical position and duration are authoritative.
- While playback belongs to the phone, server or another desktop, the latest server-owned shared-session position is authoritative.
- Local decoder state is never replaced by a slightly older shared heartbeat, preventing visible backwards jumps.
- Broadcast matching accepts both representative episode identity and canonical key so canonical aliases and multipart broadcasts update the correct row.

## Dashboard

- Featured Continue Listening and its percentage update as playback advances.
- Starting an unplayed broadcast adds it to Continue Listening immediately and removes it from Unheard.
- Completing a broadcast removes it from Continue Listening immediately.
- The active broadcast is promoted to the featured position without duplicating cards.
- Completed and in-progress totals adjust with the active listening transition rather than waiting for a new overview query.
- Revisiting Dashboard forces a current authoritative overview and then overlays any newer live position.

## Library

- Visible rows update their logical position, actual duration, percentage and play/pause highlight from live playback.
- Continue Listening, Unplayed, Completed and Hide completed list membership responds when the active broadcast crosses a listening-state boundary.
- Newly eligible rows can enter the current filtered list without requiring manual refresh.
- Revisiting Library or Search forces a fresh authoritative snapshot.

## Percentage semantics

- A completed broadcast always displays `100%`, even when the persisted final position is slightly shorter than the measured duration.
- An incomplete broadcast is capped at `99%`, avoiding a false `100%` caused by integer rounding near the end.

## Compatibility

- Database schema: **45**
- LAN capability generation: **14**
- API: **v1**
- No database migration or Library Truth re-adoption is required.
- All Alpha 12 Research/interface features and Alpha 12 Buildfix 1 repairs are retained.
