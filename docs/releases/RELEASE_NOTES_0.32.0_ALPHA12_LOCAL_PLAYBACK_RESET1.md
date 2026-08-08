# Radio Vault v0.32.0 Alpha 12 Local Playback Reset 1

This recovery milestone detaches the unstable distributed runtime and returns the Avalonia desktop application to a wholly local execution path.

## Active

- local canonical Library and SQLite database;
- local NAudio playback and progress persistence;
- local Dashboard, Library, Search, Favourites, queue and Moments;
- local Research, Metadata Studio, archive health, backup and manual scanning.

## Detached

- Connected Access, pairing and remote startup;
- `TheRadioVault.Web` project reference and linked LAN/server provider files;
- remote Library cache and media stream clients;
- server hosting and Radio Vault Anywhere;
- device heartbeat, shared ownership and handoff;
- connected-playback diagnostics.

The old federation source is retained only as frozen reference material. Database schema remains 45 and no migration is performed.
