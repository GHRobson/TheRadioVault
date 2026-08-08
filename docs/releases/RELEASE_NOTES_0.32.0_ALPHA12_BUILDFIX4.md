# Radio Vault v0.32.0 Alpha 12 Buildfix 4

## Transactional handoff recovery

This release replaces the handoff implementation introduced during the Avalonia transition. It is a reliability and data-integrity rewrite rather than another timing patch.

### New transfer contract

Every server, laptop and phone handoff now uses the same server-owned transaction:

1. **Begin** — freeze a protected source identity, ownership generation, play/pause state, speed and non-rewind playhead.
2. **Prepare** — the target opens the canonical broadcast while the source remains authoritative and audible.
3. **Ready** — the target proves its decoder is loaded, aligned and able to run muted when playback is active.
4. **Commit** — the server saves the protected durable boundary and atomically changes the owner.
5. **Source stopped** — the previous physical decoder observes the new generation, pauses and acknowledges.
6. **Unmute** — the target re-confirms that it still owns the committed generation and becomes audible.

A failure before commit cancels the transaction and leaves the original device and saved progress unchanged. Begin and commit are idempotent, so a target can safely retry when an HTTP response is lost without creating a second ticket or ownership generation. A failure after commit cannot be misreported as an unsuccessful move or cause the authoritative target to stop, unless a newer generation has genuinely superseded it.

### Progress protection

- Live heartbeats update in-memory session state only.
- Durable progress is written on a slower cadence and meaningful playback events.
- A transfer cannot replace established progress with startup zero.
- Generation-less legacy retries may advance progress but cannot rewind it.
- Only an explicit seek from the current committed owner may deliberately move progress backwards.
- Browser duration and position values cannot override the canonical multipart duration during preparation.

### Radio Vault Anywhere

- Connected playback now uses the canonical media manifest and individual media parts.
- Logical playhead conversion and automatic part advancement support multipart broadcasts.
- Repeated transfer taps are single-flight.
- Requests and decoder preparation have bounded waits and visible status.
- Safari/browser failure before commit leaves the source playing.
- The phone checks ownership again immediately before unmuting and stays silent if a newer handoff has overtaken it.
- Service-worker cache advances to `radio-vault-anywhere-shell-v34`; installed PWAs should be refreshed once after updating the server.

### Compatibility

- Database schema: **45** — unchanged.
- API: **v1** — unchanged.
- LAN capability generation: **15**.
- Desktop web-shell generation: **11**.
- Existing libraries, pairing records and listening history require no migration.
