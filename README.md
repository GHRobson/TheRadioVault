# Radio Vault 0.35.0 Alpha 9 Buildfix 3

Radio Vault is a private broadcast archive with a dedicated Windows Server, native Windows, macOS and iOS Clients, and a browser-based Radio Vault Web client.

## Architecture

Radio Vault Server runs on the computer that owns the archive. It is authoritative for:

- the Library database and archive folders;
- media streaming and shared playback state;
- favourites, listening progress, queue and Moments;
- Research records and Deep Research Packs;
- transcription models, jobs, transcripts and remembered speakers;
- device pairing, Radio Vault Web and playback handoff.

The Server has a settings-only interface and can start automatically with Windows. Radio Vault Client supplies the complete native desktop interface and connects to the Server even when both are installed on the same computer. Radio Vault Web is served by the Server for phones, tablets and browsers on the private local network.

## Major features

- Native-style Dashboard, Search, Library, Favourites, Moments, Explore, Knowledge, Downloads, Settings and Now Playing workspaces, with Transcription Studio inside Knowledge.
- First-class support for Ron & Fez, Bennington, Opie & Anthony, The Ron & Ron Show, Ron Bennington Interviews and Unmasked.
- Canonical multipart playback with shared progress, favourites, queue, Moments and listened/unlistened actions.
- Transactional handoff between native and Web devices: playback remains at the source until the target decoder is ready and aligned.
- Persistent encrypted Client caching, cache-first startup and automatic recovery after Server restarts or temporary network loss.
- Phone and native downloads with integrity checking and offline playback.
- Local whisper.cpp transcription, VAD and multi-speaker diarisation managed by the Server.
- Full, sample and batch transcription with pause, resume, cancellation, retry, transcript review, speaker confirmation and subtitle exports.
- One archive-wide portable Knowledge Database for every show and year, containing research records, Explore pages, citations, images, timelines and transcripts.
- Advanced search across broadcasts, people, Research records and transcript speech.
- Human-editable Explore pages with an article-first encyclopaedia reader, compact infoboxes, contents navigation, separate Home and Browse destinations, browser-style Back/Forward navigation and reading as the default.
- Rendered Explore articles with automatic inline links, contents, infoboxes, backlinks, related pages, missing-page indicators, dated/captioned images, Broadcast Info links and revision history.
- Engaging Wiki and Research dashboards spanning knowledge coverage, archive range, featured pages, shows, people, topics, eras, recent changes and On this date.
- Combined citation, broken-link, duplicate-page and orphan-page auditing.
- Canonical topic identities with automatic safe deduplication, aliases, merge history and ranked review suggestions across Research, tags and Wiki pages.
- Clickable people and topic chips throughout the native client and Radio Vault Web, with related Wiki pages surfaced from the Library, Broadcast Info, Now Playing, Search and Research.
- A read-focused Radio Vault Web Wiki for exploring the same pages, images, sources and timelines on phones and browsers.
- Archive-aware starter pages for shows, recurring people and topics, generated without overwriting existing human work.
- Portable `.trvknowledge` databases for AI-assisted enrichment, with an embedded AI handbook and schema guide, integrity-checked atomic export, retained pre-import backups, page-identity reconciliation, import preview and protected human edits.

## Installation

Install **Radio Vault Server** on the computer containing the main archive. Add the Library folders in Server Settings and leave the Server running in the background.

Install **Radio Vault Client** on every Windows computer where you want the native interface. A Client on the Server computer uses the local loopback connection automatically; another computer can pair using the code created in Server Settings.

The macOS Client is currently an Apple Silicon alpha. It connects to the same
Windows Server and uses the same pairing, cache, download, playback and handoff
contracts. See [MACOS-CLIENT.md](MACOS-CLIENT.md) for build and on-Mac acceptance instructions.

The iOS Client is an early iPhone and iPad alpha built with native UIKit
controls and navigation. It provides local-network discovery and pairing, a
mobile Dashboard and searchable Library, canonical multipart playback, native
Now Playing controls and secure Keychain storage. It uses
the existing Server v1 API with certificate pinning; no iOS-specific Server is
required. See [BUILDING.md](BUILDING.md#ios-client) for the Simulator build.

Use the Radio Vault Web QR code in Client or Server Settings to connect a phone on the same private network.

The installers preserve the authoritative database, archive-folder configuration, models, transcripts, certificates, paired-device trust, preferences and encrypted caches during upgrades.

## Network scope

Radio Vault 0.35 Alpha 6 supports the Server computer, paired local-network Clients and Radio Vault Web on the private LAN. It is not designed to be exposed directly to the public internet. Internet remote access remains reserved for a later release with a dedicated security boundary.

Research and Wiki exchange now use one inspectable `.trvknowledge` Archive Knowledge Database.

## Build and validation

From a source checkout on Windows with the .NET 8 SDK:

```powershell
.\release-gate.ps1
```

The release gate validates the source architecture, builds the solution deterministically with warnings treated as errors, runs the complete smoke suite and verifies the product version.

See [BUILDING.md](BUILDING.md) for build, publish and installer details, [V0.35.0-ALPHA9-KNOWLEDGE-PORTABILITY.md](V0.35.0-ALPHA9-KNOWLEDGE-PORTABILITY.md) for the current acceptance guide, [V0.35.0-ALPHA6-UNIFIED-KNOWLEDGE.md](V0.35.0-ALPHA6-UNIFIED-KNOWLEDGE.md) for the unified format foundation and [V0.34.0-STABLE.md](V0.34.0-STABLE.md) for the previous stable contract.

## Shared development

The Windows Client, Windows Server, macOS Client and iOS Client are maintained from this
one repository. Do not keep separate platform copies of the source. Every
change should use a short-lived branch and pass Windows, macOS and iOS GitHub
Actions checks before it is merged into `main`.

See [DEVELOPMENT.md](DEVELOPMENT.md) for the Git and Codex workflow used on each
development computer.
