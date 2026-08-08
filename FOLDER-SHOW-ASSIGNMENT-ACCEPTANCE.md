# Local Playback Reset 2 Buildfix 1 — Folder Show Assignment

## Purpose

Restore the explicit show chooser when a local archive folder is registered. Automatic detection remains available for genuinely mixed folders, but clear single-show folders no longer depend solely on filename inference.

## Acceptance test

1. Open **Settings → Archive** and choose **Add folder**.
2. Select a folder.
3. Confirm a second window asks **Which show is this folder for?**
4. Confirm the choices include:
   - Auto-detect / mixed-show folder
   - Ron & Fez
   - Bennington
   - Opie & Anthony
   - The Ron & Ron Show
   - Ron Bennington Interviews
   - Unmasked
5. Add a Ron Bennington Interviews folder with that fixed assignment.
6. Add an Unmasked folder with that fixed assignment.
7. Confirm Settings displays the assigned show beneath each registered path.
8. Run a Library scan and confirm recordings appear under the selected top-level show.
9. Confirm cancelling the chooser does not register the folder.
10. Confirm Auto-detect remains usable for a folder containing more than one show.

Removing a registration must continue to leave all audio files untouched.
