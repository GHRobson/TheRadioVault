# Radio Vault v0.30.0 Alpha 1 Buildfix 1

This build repairs the two blockers found during the first LAN federation test.

## Fixed

- Connected Access checkboxes now retain the user's selected values after navigating away or restarting Radio Vault.
- LAN discovery no longer relies on whichever network adapter Windows chooses as its default multicast route.
- Radio Vault announces itself through every active private IPv4 Wi-Fi/Ethernet route and through directed subnet broadcast.
- The receiving server listens on every active private IPv4 adapter.
- Discovery waits eight seconds and shows which adapters were checked when no server is found.

## Preserved

- Six-digit expiring pairing codes.
- Certificate-pinned HTTPS trust.
- Per-client persistent credentials and revocation.
- Authenticated bootstrap verification.
- Database schema 45 and capability generation 8.
- The accepted Radio Vault Anywhere implementation and offline caches.
