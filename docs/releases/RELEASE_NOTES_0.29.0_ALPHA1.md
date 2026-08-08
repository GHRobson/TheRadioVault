# Radio Vault v0.29.0 Alpha 1

Radio Vault Anywhere now has a stable first-contact contract. Every server has a persisted identity and advertises exactly which read, write, client and planned capabilities it supports. A new bootstrap endpoint provides bounded library, playback and queue state for the PWA and future LAN desktop clients.

The secure web app displays the authoritative server name and version while connected and retains its existing offline Dashboard, Downloads, progress synchronisation and device-ownership behaviour. Existing downloaded audio and artwork are not invalidated.

This release remains on schema 45 and does not rewrite archive data.
