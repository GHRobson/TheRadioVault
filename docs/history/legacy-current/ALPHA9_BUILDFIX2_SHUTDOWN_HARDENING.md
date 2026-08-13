# Alpha 9 Buildfix 2 — Shutdown hardening

Target: `0.30.0-alpha9-parity-audit-hardening-buildfix2`

## Accepted defect

Alpha 9 Buildfix 1 compiled and its parity features passed the user's initial acceptance pass. The remaining defect was intermittent: closing Radio Vault could leave the process running indefinitely until it was ended through Task Manager.

## Hardening applied

- The close sequence now starts once and records each shutdown stage in `diagnostic.log`.
- Playback timers, LAN probes and authoritative-library synchronization are stopped before the final progress save.
- A remote client's final server progress mutation has a two-second cancellation deadline rather than an unbounded synchronous wait on the UI thread.
- Shutdown continues past individual cleanup failures while retaining the failure in diagnostics.
- A 12-second watchdog terminates only the already-closing process if any lower-level Windows media, networking, cancellation or storage call fails to return.
- Local recovery-journal behaviour, server progress preservation, schema 45 and LAN capability generation 14 are unchanged.

## Acceptance focus

1. Quit while idle in local-library mode.
2. Quit while local audio is paused and while it is playing.
3. Quit the remote client while server audio is paused and while it is playing.
4. Quit the remote client with the server available, unavailable and during reconnection.
5. Repeat each path several times and confirm the process disappears from Task Manager within 12 seconds.
6. Reopen and confirm listening progress was retained; if the final server save timed out, the previous periodic save should be retained.
7. If a close takes unusually long, inspect the final `Shutdown` entries in `diagnostic.log` to identify the last started step.
