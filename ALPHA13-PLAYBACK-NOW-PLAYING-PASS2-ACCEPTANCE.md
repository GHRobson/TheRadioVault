# Alpha 13 Pass 2 — Playback responsiveness and Now Playing

## Purpose

This pass keeps the accepted local-first desktop and Radio Vault Anywhere boundary intact while reducing local playback startup work and making opening state visible.

## Acceptance

1. Build with `BUILD-AND-RUN.cmd`.
2. Start a previously unplayed local broadcast and confirm the shell stays responsive while it opens.
3. Start a resumed broadcast and confirm it begins at the saved position.
4. Confirm the mini-player and Now Playing page show a loading glyph and meaningful status while audio opens.
5. Confirm one click starts one playback request; repeated row clicks do not create competing sessions.
6. Confirm pause, resume, skip and seek still work.
7. Confirm Dashboard and Library progress continue moving while playback runs.
8. Close and reopen Radio Vault and confirm progress persists.
9. Open Now Playing with nothing active and confirm Up Next remains visible; when the queue contains items, `Play first queued broadcast` works.
10. Confirm Radio Vault Anywhere still starts and browser playback still works.

## Performance diagnostics

Runtime diagnostics now record separate `resolve-broadcast` and `open-local-decoder` timings. The Playback view model also retains the last lookup, decoder and total startup durations for later diagnostics/UI use.

## Deferred

Native desktop federation, remote cache, device ownership and handoff remain post-1.0 work.
