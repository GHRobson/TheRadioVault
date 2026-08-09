# Local Playback Reset 1 — Acceptance Test

Use this test independently on the PC and laptop. Each machine is a completely local Radio Vault installation in this build.

## Before starting

1. Close every older Radio Vault window and check Task Manager for any remaining `TheRadioVault.exe` process.
2. Build using `BUILD-AND-RUN.cmd`.
3. Confirm there is no green server indicator, pairing screen, cached-client status or handoff control.

## Test A — Local Library

1. Open Settings → Archive.
2. Confirm the machine's own local Library folders are present.
3. Run a manual scan only if the Library is missing expected local files.
4. Open Dashboard and Library and confirm local broadcasts appear.

## Test B — Playback

1. Start one local broadcast and measure roughly how long it takes before audio is audible.
2. Let it play for at least 30 seconds.
3. Pause and resume.
4. Seek forward to an obvious position and resume.
5. Confirm the playhead continues advancing.

## Test C — Persistence

1. Pause partway through the broadcast.
2. Close Radio Vault normally and wait for the closing overlay to finish.
3. Reopen Radio Vault.
4. Confirm the same broadcast resumes near the saved position.
5. Confirm Dashboard and Library show the same local progress without a manual refresh.

## Report back

```text
Machine:
Library opened: Yes/No
Audio audible: Yes/No
Approximate startup delay:
Pause/resume: Pass/Fail
Seek: Pass/Fail
Playhead advanced: Yes/No
Progress survived restart: Yes/No
Dashboard/Library progress matched: Yes/No
Any unexpected network/pairing UI:
```
