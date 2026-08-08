# Radio Vault v0.30.0 Alpha 6

## Remote Research parity

The normal Research workspace now behaves the same while connected to a server:

- Import chooses a `.trvpack` from the remote client, uploads it over the certificate-pinned paired connection, and runs the established preview and atomic import against the server database.
- The same preview dialog, conflict counts and result summary remain visible on the client.
- Export is built from the server database and downloaded to a path chosen on the client.
- The exact uploaded package is staged temporarily on the server so duplicate detection, SHA-256 provenance, import history and the normal pre-import safety snapshot remain intact.
- Preview sessions expire after 30 minutes, abandoned staging files are cleaned up, at most four previews are retained, and remote packs are bounded to 64 MB.
- Import cancellation is sent to the server transaction; the client only reports cancellation after the server accepts it.
- The client's dormant local archive database is never used for remote import or export.

## Mode-aware Settings

Remote-client Settings now deliberately reflect the active mode:

- Archive reports the server's folders, counts, health, storage availability, preservation coverage and latest backup age.
- Local folder editing, scanning, metadata-to-file synchronization and backup creation controls are hidden from the client.
- Playback settings are loaded from and saved to the server.
- Connected access shows only the saved server, address, certificate trust, version, capability generation, connection test, refresh, local fallback and forget controls.
- Radio Vault Anywhere hosting, server-hosting and pairing controls, Transcription and Advanced are hidden in remote mode.

## Compatibility

- Database schema: 45 (unchanged)
- LAN capability generation: 12
- Existing local/remote `MainWindow`: preserved
- Alpha 5 Buildfix 5 post-scan canonical promotion: preserved
