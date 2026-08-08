# Radio Vault roadmap

This is the authoritative roadmap after Radio Vault 0.33. Older release and architecture documents describe the history of earlier implementations; they do not override this direction.

## Product model

Radio Vault is becoming a server-owned archive with universal clients.

### RadioVault Server

The RadioVault Server will be a dedicated background application with only a compact administration and settings interface. It will not contain the Dashboard, Library, Search, Research or player UI.

The server will own:

- the authoritative Library database and media locations;
- scanning, metadata, archive maintenance, backup and recovery;
- progress, favourites, queue, Moments and playback-session state;
- Research records, Deep Research Packs and provenance;
- transcripts, diarisation, speaker identities and transcription jobs;
- artwork, media streaming and client authentication;
- connected-client state and handoff coordination.

### Universal clients

Every visible Radio Vault experience will be a client of the server:

- the native RadioVault Client on the server computer, connected over loopback;
- the same native RadioVault Client on another computer, connected remotely;
- Radio Vault Anywhere in a browser or installed as a PWA.

There will not be separate local and remote feature sets. The same server contract will expose the complete Radio Vault feature set to every authorised client. Durable writes and archive operations remain server-owned, and clients must never silently fall back to an unrelated local database.

## Phase 1 — Radio Vault 0.33 stable

Promote the accepted Avalonia and transcription implementation to a stable, recoverable baseline.

- Freeze new features.
- Fix release-blocking defects only.
- Pass the complete release and data-safety gates.
- Publish verified built and source packages.

## Phase 2 — Dedicated server foundation

Extract the authoritative runtime into RadioVault Server before rebuilding client surfaces against it.

- Create a separately runnable background server application.
- Provide settings, status, health, backup, model and connected-client administration only.
- Define one versioned contract for every Library, playback, Research, transcription and maintenance capability.
- Preserve the existing database, media identity and user data without a destructive migration.
- Add secure loopback access for the native client on the same computer.

## Phase 3 — Native application becomes a client

Convert the current Avalonia interface into RadioVault Client.

- Route all reads, writes, jobs and media access through the server contract.
- Connect to the local server over loopback by default.
- Retain the complete accepted native interface and behaviour.
- Remove accidental direct ownership of the server database and archive files from the client.

## Phase 4 — Rebuild Radio Vault Anywhere

Rebuild the browser/PWA interface to resemble the native client as closely as the platform permits.

- Share colours, typography, icons, terminology, navigation and interaction hierarchy.
- Maintain a native-versus-Anywhere parity ledger.
- Surface every Radio Vault feature rather than hiding unsupported operations.
- Implement real server-backed read and write behaviour for those features.
- Preserve responsive desktop, tablet and phone layouts.

## Phase 5 — Native and Anywhere handoff

Implement reliable two-way playback handoff between the local native client and Radio Vault Anywhere.

Handoff must preserve the canonical broadcast, exact position, playing or paused intent, queue position and playback ownership. Repeated transfers, reconnects and temporary network interruption must not select the wrong recording, duplicate progress or leave ownership ambiguous.

## Phase 6 — Full remote native clients

Allow the same RadioVault Client application to connect to a RadioVault Server on another computer.

- Expose every capability available when connected over loopback.
- Stream server-owned media when files are not local.
- Securely invoke server-owned maintenance and long-running operations instead of removing them from the interface.
- Provide dependable authentication, reconnect, caching and conflict behaviour.
- Keep the server authoritative at all times.

## Phase 7 — Universal handoff hardening

Extend and harden playback handoff across the server computer's native client, remote native clients and every Radio Vault Anywhere session.

The server will arbitrate playback-session ownership and the last accepted state. Handoff is complete only when it remains reliable through restarts, disconnects, delayed clients and repeated cross-device transfers.

## Completion rule

The server owns the archive and all durable operations. Every visible Radio Vault interface is a client of that server, every authorised client can access the complete feature set, and handoff works reliably between all of them.
