# Radio Vault v0.32.0 Alpha 12 Playback Baseline Recovery 1

This build stops the escalating handoff repair branch and restores a narrow acceptance target: dependable ordinary playback on the server and connected Avalonia laptop.

## Included

- Immediate startup from the laptop's encrypted cache.
- Background Library reconnection that does not gate playback.
- Direct authenticated media-manifest, audio-stream and progress paths.
- Minimal ownership reporting so the server accepts the laptop's canonical progress writes.
- Local server playback on the existing local decoder/persistence path.

## Intentionally disabled

- Desktop Move to this device controls.
- Transactional prepare/ready/commit handoff.
- Cross-device source-stop acknowledgement.
- Shared-device presentation in the Avalonia Now Playing page.

Radio Vault Anywhere remains present, but this package is not a handoff acceptance build.

## Acceptance order

1. Build and launch on the laptop.
2. Confirm cached Library startup, then play one broadcast for at least one minute.
3. Pause, seek backward, resume, close and reopen; confirm the saved position.
4. Update the server with the same package and repeat local server playback.
5. Relaunch the laptop after the server restart and repeat playback while Library sync is still recovering.

Do not assess handoff in this build. Database schema remains 45 and no library migration is required.
