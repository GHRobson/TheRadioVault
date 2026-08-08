# Radio Vault iOS Agent Handoff

**Snapshot:** 8 August 2026  
**Radio Vault version:** `0.35.0-alpha9-buildfix3`  
**Server API:** `v1`  
**Database schema:** `47`  
**Connected-client capability generation:** `40`  
**Intended next task:** build a native Swift/SwiftUI iPhone and iPad client for the existing Radio Vault Server.

This is the primary starting document for an AI agent receiving the source package on a Mac. Read this file, `README.md`, `docs/guides/ROADMAP.md`, `docs/guides/ARCHITECTURE.md`, `V0.34.0-STABLE.md`, and `V0.35.0-ALPHA9-KNOWLEDGE-PORTABILITY.md` before changing code.

## 1. Mission

Create a genuinely native iOS client which gives a listener dependable access to their private Radio Vault archive from an iPhone or iPad. The app should use Swift, SwiftUI, AVFoundation, URLSession, Keychain and Apple platform conventions. It is a new presentation client, not a port of the Windows Server and not a second owner of the archive database.

The first useful build should pair with an existing Radio Vault Server, browse and search the canonical Library, open Broadcast Info, stream long-form audio, show Now Playing, and synchronise listening progress. Subsequent phases should add favourites, queue, Moments, transcripts, downloads, offline behaviour, transactional playback handoff, Explore and Knowledge.

### Product goals

- Preserve and explore a private long-form broadcast archive.
- Present one canonical broadcast even when multiple recordings or physical files exist.
- Make listening position, completion, favourites, queue and Moments dependable across devices.
- Keep the Server authoritative for the archive, media, durable state, research, transcripts and jobs.
- Provide secure private-LAN access without exposing the service directly to the public Internet.
- Make playback transfer between devices explicit, transactional and recoverable.
- Make Research, Explore pages, citations, timelines and transcripts part of the same archive experience.

### Non-goals for the iOS app

- Do not embed or reimplement Radio Vault Server.
- Do not open, copy or write the Server SQLite database directly.
- Do not access the Server's filesystem paths. API responses deliberately hide them.
- Do not run transcription, scanning, import or preservation workers on iOS.
- Do not make the Server publicly Internet-facing as part of this work.
- Do not silently invent a second local source of truth when the Server is unavailable.

## 2. Current product state

Radio Vault already has:

- a dedicated Avalonia Server application with a settings-only interface;
- a complete Avalonia desktop Client for Windows;
- an Apple Silicon macOS Client alpha which cross-publishes and uses AVFoundation, but still requires real-Mac acceptance testing;
- Radio Vault Web, a responsive browser/PWA client already exercised on iPhone Safari;
- a versioned authenticated API covering the complete native-client surface;
- canonical multipart media manifests and byte-range streaming;
- persistent client caching and reconnect logic;
- server-arbitrated playback ownership and transactional handoff;
- server-owned transcription, diarisation, transcripts and speaker identities;
- an archive-wide `.trvknowledge` exchange database for Research, Explore, citations, images, timelines and transcripts.

At handoff, source validation passes and the complete smoke suite reports **270/270 passing tests**. The packaged source contains no personal archive database, pairing tokens, private certificates, media library or installed transcription models.

## 3. Authoritative architecture

```text
                         Radio Vault Server
       SQLite + media + state + Research + transcripts + jobs
                            API v1 / TLS
             +-------------------+-------------------+
             |                   |                   |
       Desktop Client      Radio Vault Web      Native iOS Client
       Windows / macOS      Browser / PWA        SwiftUI / AVFoundation
```

The Server owns all durable operations. Every visible application is a client, even when it is running on the same computer as the Server. A connected client must never fall through to a local SQLite database or a local copy of server-owned state.

The current API and database identifiers are intentionally separate:

- API version: `v1`
- database schema: `47`
- connected-client capability generation: `40`
- application version: `0.35.0-alpha9-buildfix3`

Negotiate against the Server's bootstrap response and capabilities. Do not infer feature support solely from the app version.

## 4. Domain invariants

Radio Vault separates three identities:

1. **Broadcast** - the programme event the listener browses, researches, favourites and tracks progress against.
2. **Recording** - one capture or assembled multipart representation of that broadcast.
3. **Physical file** - an actual audio file, including duplicates and alternate encodes.

The iOS interface should be broadcast-first. Never create one visible Library row per physical file. Use the canonical media manifest to play the preferred recording and map multipart audio onto one logical timeline.

Listening progress, favourites, Moments, transcripts and queue actions attach to the canonical broadcast identity. Preserve the representative episode/broadcast ID returned by the Server; do not attempt to generate a replacement ID on the device.

## 5. Source map

| Path | Purpose for an iOS agent |
|---|---|
| `TheRadioVault.Web/Contracts/WebApiRoutes.cs` | Canonical API v1 route registry. Start here for endpoint names. |
| `TheRadioVault.Web/Models/` | JSON request and response contracts used by native and Web clients. |
| `TheRadioVault.Web/Services/LocalWebServer.cs` | HTTP routing, authentication, range streaming and Web reference behaviour. Large file; search by route constant. |
| `TheRadioVault.Infrastructure/Services/NativeConnectedAccessService.cs` | Existing discovery, pairing, certificate pinning and reconnect reference. |
| `TheRadioVault.Infrastructure/Services/LoopbackServerClient.cs` | Authenticated API calls, timeout and cache behaviour used by the desktop Client. |
| `TheRadioVault.Infrastructure/Services/LoopbackUserStateServices.cs` | Favourites, listening status, Moments and media-manifest client adapters. |
| `TheRadioVault.Infrastructure/Services/NativeDownloadService.cs` | Download lifecycle and integrity behaviour to reproduce with background URLSession. |
| `TheRadioVault.Infrastructure/Services/NativeServerResponseCache.cs` | Desktop cache semantics; use as policy reference, not code to port literally. |
| `TheRadioVault.Infrastructure/Services/ServerMediaProxy.cs` | Desktop workaround for authenticated native media playback. It is not directly reusable on iOS. |
| `TheRadioVault.Services/Models/PlaybackHandoffModels.cs` | Device state, generation and transfer-plan contracts. |
| `TheRadioVault.Infrastructure/Services/LoopbackPlaybackHandoffService.cs` | Exact native-client handoff request sequence. |
| `TheRadioVault.Web/Services/PlaybackTransferCoordinator.cs` | Web handoff behaviour, including iPhone-specific lessons. |
| `TheRadioVault.Core/Playback/` | Platform-neutral progress, persistence and completion rules. |
| `TheRadioVault.Desktop.Avalonia/` | Complete desktop information architecture and feature reference. |
| `TheRadioVault.Web/` | Responsive phone reference and proven Safari behaviour. |
| `TheRadioVault.Tests/Program.cs` | Executable contract index and regression suite. Search test names before changing a protocol. |
| `docs/guides/ROADMAP.md` | Authoritative server-owned universal-client roadmap. |
| `V0.34.0-STABLE.md` | Accepted dedicated-server and multi-device baseline. |
| `CHANGELOG.md` | Detailed history and rationale, particularly the iPhone playback fixes in 0.34 Alpha 19-20. |

The solution has thirteen .NET projects. For iOS work, the most important are Core, Services, Web, Infrastructure, Desktop.Avalonia and Tests. The Swift project should live in a clearly isolated directory such as `clients/RadioVaultIOS/`; do not mix generated Xcode output into the .NET projects.

## 6. Discovery, pairing and trust

### Existing LAN discovery

The Server announces a JSON `WebLanDiscoveryAnnouncement` over IPv4 multicast:

- group: `239.255.82.86`
- port: `30829`
- protocol marker: `radiovault-lan-v1`

The announcement contains the Server instance GUID, display name, API/app/schema/capability versions, secure port, certificate thumbprint and pairing availability. It contains no access token.

iOS local-network and multicast behaviour must be validated against current Apple requirements. Add a clear `NSLocalNetworkUsageDescription`. If raw multicast entitlement or App Store approval becomes a blocker, retain manual address entry for development and consider a narrowly scoped Server addition such as Bonjour discovery or a QR pairing payload. Do not weaken certificate validation to bypass discovery.

### Pairing sequence

1. Discover a Server and retain its instance ID, address, secure port and announced certificate thumbprint.
2. Ask the user for the six-digit one-time code displayed by Server Settings.
3. Create a TLS client that accepts only the certificate matching the announced thumbprint.
4. `POST /api/v1/federation/pair` with `code`, a stable client GUID and a human-readable device name.
5. Confirm the returned Server instance ID and certificate thumbprint still match the discovered identity.
6. Store the returned per-client access token, Server identity and certificate pin securely.
7. Probe `GET /api/v1/federation/bootstrap` using the token and pin before opening the main interface.

Authenticated requests use:

```http
X-RadioVault-Token: <per-client access token>
```

### iOS storage rules

- Store the access token and trust record in Keychain, not UserDefaults or a plain JSON file.
- Store non-secret preferences and cache metadata in an app-group-safe Codable store or database.
- Never log the access token, pairing code, certificate material or authenticated URLs.
- Reject a Server identity or certificate change until the user explicitly forgets and re-pairs.
- Use `URLSessionDelegate` server-trust handling for exact certificate/public-key pin validation.
- Keep a stable device/client GUID across launches and upgrades.

## 7. API orientation

`TheRadioVault.Web/Contracts/WebApiRoutes.cs` is the source of truth. Major route groups include:

- `/api/v1/server-info` and `/api/v1/bootstrap`
- `/api/v1/federation/bootstrap`, pairing, parity, settings and library sync
- `/api/v1/client/library/*` and `/api/v1/client/broadcast-details/*`
- `/api/v1/broadcasts`, search, shows, favourites and archive health
- `/api/v1/player`, queue and transactional transfer routes
- `/api/v1/moments`, transcripts and jobs
- `/api/v1/client/research/*`, `/client/transcripts/*`, `/client/speakers/*`
- `/api/v1/client/transcription/*` and `/client/wiki/*`
- Research/Knowledge import, export and server-owned background-job routes

The server serialises JSON using .NET Web defaults, so Swift models should expect camel-case property names and ISO-8601 dates. Decode defensively: new fields should not break older clients, capability flags should gate optional features, and unknown enum-like strings should have safe fallbacks.

Do not manually duplicate every server model before the first build. Begin with small Codable contracts for discovery, pairing, federation bootstrap, dashboard episodes, library browse, search, broadcast details, playback state and canonical media manifests. Add endpoint-specific models as screens are implemented.

## 8. Media and playback

### Canonical media

For a broadcast, request:

```text
GET /api/v1/broadcasts/{episodeId}/media-manifest
GET /api/v1/broadcasts/{episodeId}/media/{mediaFileId}
```

The manifest returns the canonical key, recording key, logical duration and ordered parts. Each part includes its part number, logical start/end, media-file ID, size and storage state. Media endpoints support HTTP byte ranges and validators.

Multipart playback must appear as one programme timeline. Map logical seeks to the corresponding part and part-relative offset. Progress and Moments remain logical broadcast positions, not per-file positions.

### Critical AVPlayer authentication spike

The API expects `X-RadioVault-Token` on authenticated media requests. Do not build the app around private or undocumented AVFoundation header injection. Before building all playback UI, complete a small proof on a physical iPhone that can:

1. establish pinned TLS trust;
2. obtain a canonical media manifest;
3. play and seek a long MP3/M4A through the authenticated media route;
4. survive AVPlayer range re-requests;
5. continue under lock-screen/background audio conditions.

Safe implementation options to evaluate include a documented AVAsset resource-loader pipeline, URLSession download-to-local playback, or a narrowly scoped Server enhancement that issues short-lived media capability URLs. If a Server change is necessary, tokens must be short-lived, media-specific and absent from routine logs; do not put the permanent client token in a query string.

### Apple playback integration

- Configure `AVAudioSession` for long-form playback and interruption handling.
- Use `MPNowPlayingInfoCenter` and `MPRemoteCommandCenter` for lock-screen controls.
- Support play, pause, seek, skip back/forward, speed and device-local volume conventions.
- Treat route changes, interruptions and app backgrounding as explicit state transitions.
- Keep local volume device-owned. Do not hand off volume between devices.
- Derive displayed progress between server heartbeats, but only persist accepted canonical progress.

### Progress safety

- Never overwrite meaningful progress with a transient startup zero.
- Do not let generation-less or stale retries rewind a newer position.
- Live heartbeats are not automatically durable progress writes.
- Mark completion only after a natural end within the accepted completion window.
- Preserve speed, canonical broadcast ID and queue position through handoff.

## 9. Transactional playback handoff

Handoff is a protocol, not a visual shortcut. The source device remains authoritative until the target proves it is prepared.

The route sequence is:

1. `POST /api/v1/player/transfer/begin`
2. prepare the target at the protected broadcast, logical position and speed
3. prove the decoder is ready (and, for requested playing state, audibly/temporally advancing)
4. `POST /api/v1/player/transfer/ready`
5. `POST /api/v1/player/transfer/commit`
6. the source physically stops playback
7. `POST /api/v1/player/transfer/source-stopped`

Cancellation has its own route. Tickets contain transfer IDs, source/target identities, generation, protected and commit positions, desired play state and expiry. Preserve idempotency: retries with the same transfer identity must not create a second transfer or move ownership twice.

Study the 0.34 Alpha 18-20 sections of `CHANGELOG.md` before implementation. Those releases record subtle iPhone failures involving decoder replacement, user-gesture boundaries, positioned streams, misleading readiness and repeated cross-device transfers.

## 10. Offline and caching policy

The iOS client may keep a read cache and downloaded media, but the Server remains authoritative.

- Use an on-device database or structured cache for bootstrap, Library projections and detail responses.
- Encrypt or protect sensitive cache data using iOS data-protection classes.
- Show cached content as read-only while disconnected unless an operation has an explicit synchronisation contract.
- Reconnect with bounded exponential backoff while retaining the pinned Server identity.
- Use the federation library-sync session, sequence and revision fields for incremental recovery.
- Use background URLSession for downloads and maintain an explicit download manifest per canonical broadcast.
- Verify expected lengths/hashes where the server contract provides them; never treat a partial file as complete.
- Map downloaded multipart files to the same logical timeline used for streaming.
- Apply offline progress through the explicit offline-progress route and protect against stale rewinds.

## 11. Information architecture

The desktop Client's current primary destinations are:

- Dashboard
- Search
- Library
- Favourites
- Moments
- Explore
- Knowledge
- Downloads
- Settings
- Now Playing

On iPhone, use a smaller tab structure and hierarchical navigation rather than reproducing the desktop sidebar literally. A reasonable starting structure is Home, Library, Search, Explore and Now Playing, with Favourites, Moments, Downloads, Knowledge and Settings available within those areas. On iPad, use NavigationSplitView where appropriate.

The app should feel native to iOS while preserving Radio Vault terminology, visual identity, canonical episode cards and information priority. The Web client is the strongest existing phone-layout reference; the Avalonia Client is the complete feature reference.

## 12. Proposed iOS delivery roadmap

This iOS workstream is new. It extends, but does not replace, the repository's authoritative universal-client roadmap.

### Phase 0 - contract and playback spike

- Create the Swift/Xcode project, environments and test targets.
- Implement pinned TLS, discovery/manual connection, pairing and Keychain storage.
- Decode federation bootstrap and capability information.
- Prove authenticated AVPlayer playback, seeking and background operation on a physical iPhone.
- Decide whether a narrowly scoped Server media-authentication addition is required.

**Exit:** a paired development build can play and seek one real long-form broadcast without exposing credentials.

### Phase 1 - useful listening client

- Dashboard bootstrap, Library browse, search and Broadcast Info.
- Now Playing, queue view, play/pause/seek/skip/speed and progress persistence.
- Artwork, empty/error/loading states and reconnect status.
- iPhone and iPad navigation foundations.

**Exit:** the app is useful for daily streaming from the private LAN.

### Phase 2 - personal state and offline use

- Favourites, listened/unlistened actions, Moments and queue mutations.
- Background downloads, offline manifests and local playback.
- Cache-first startup and bounded read-only outage behaviour.
- Lock-screen metadata, interruptions and audio-route handling.

**Exit:** ordinary listening remains dependable through brief Server outages and downloaded broadcasts work offline.

### Phase 3 - multi-device integrity

- Device presence and ownership UI.
- Complete transactional handoff in both directions.
- Repeated transfer, cancellation, restart and stale-generation recovery tests.
- Long-duration playback and multipart coverage.

**Exit:** transfers never stop the source before target readiness and never duplicate/rewind canonical progress.

### Phase 4 - archive parity

- Transcripts and timed transcript navigation.
- Explore dashboards, pages, citations, images and timelines.
- Knowledge browsing and safe server-owned job status surfaces.
- Full accessibility, Dynamic Type, VoiceOver, localisation readiness and iPad refinement.

**Exit:** all listener-facing desktop areas have an intentional iOS equivalent or a documented platform-specific reason for omission.

### Phase 5 - distribution

- Real-device matrix, battery/network/background testing and privacy review.
- TestFlight packaging, crash diagnostics and upgrade/cache migration.
- App Store metadata and review notes explaining private-LAN Server dependency.

**Exit:** signed TestFlight build passes the acceptance checklist below.

## 13. Acceptance checklist

### Trust and connection

- Local-network permission text clearly explains why access is needed.
- Pairing requires the six-digit Server code and verifies the pinned identity.
- Credentials live in Keychain and never appear in logs.
- Certificate or Server instance changes fail closed.
- Reconnect retains the trusted identity and never pairs silently.

### Library and state

- Dashboard, Library, Search and Broadcast Info show canonical broadcasts only.
- Favourites, listening status, progress, queue and Moments remain consistent with desktop/Web clients.
- Cache-first launch clearly distinguishes live and cached state.
- Unknown JSON fields and optional capabilities do not crash the app.

### Playback

- MP3 and M4A streams start, seek and resume correctly.
- Multipart broadcasts use one continuous logical timeline.
- Speed, skip, pause and natural completion work under foreground, background and lock-screen control.
- Incoming calls, Siri, AirPods changes and route interruptions recover safely.
- Several-hour playback does not drift, loop, jump or rewrite progress backwards.

### Downloads

- Downloads resume after suspension and never mark partial content complete.
- Offline playback uses the same broadcast identity and logical timeline.
- Removing a download does not remove Server state or the broadcast.
- Storage pressure and unavailable files have clear recovery paths.

### Handoff

- Desktop/Web to iPhone and iPhone to desktop/Web preserve broadcast, position, speed, queue and play/pause intent.
- The source continues until the target has demonstrated readiness.
- Cancellation and timeout leave ownership unambiguous.
- Repeated round trips and stale retries are idempotent.
- Volume remains local to each device.

### Platform quality

- iPhone and iPad layouts work in supported orientations and text sizes.
- VoiceOver labels all navigation and playback controls.
- App lifecycle, background audio and background downloads are tested on physical devices.
- No private Apple API is used.
- A clean install and an update retain pairing and safe cached state.

## 14. Engineering workflow on the Mac

1. Extract the source ZIP to a short writable path.
2. Verify `SOURCE_MANIFEST.sha256.json` before making changes.
3. Read the documents listed at the top of this handoff.
4. Initialise a Git repository or create a branch before editing if the extracted package has no `.git` history.
5. Install .NET 8 and PowerShell 7 if shared contract tests or source validation will run on the Mac.
6. Run `pwsh ./validate-source.ps1` and the smoke-test executable before protocol changes.
7. Create the Swift project under `clients/RadioVaultIOS/` with separate application, unit-test and UI-test targets.
8. Keep generated build output, DerivedData, signing profiles and user-specific Xcode settings out of source control and source packages.
9. Add contract fixtures/tests for every Codable model and mutation before building a dependent screen.
10. Test pairing, playback and handoff against a disposable/current Radio Vault Server before the user's authoritative archive.
11. When changing the Server contract, preserve API v1 compatibility where possible, add server and Swift contract tests, increment capability generation when the negotiated feature surface changes, and document the change.
12. Regenerate the source manifest/package using `tools/Package-Source.ps1` when returning work.

## 15. Recommended Swift structure

```text
clients/RadioVaultIOS/
  RadioVaultIOS.xcodeproj
  RadioVaultIOS/
    App/
    Core/                 # identifiers, errors, capability gates
    Networking/           # pinned session, API client, Codable contracts
    Pairing/              # discovery, pairing, Keychain trust store
    Library/              # dashboard, browse, search, details
    Playback/             # AVFoundation, logical timeline, progress
    Downloads/            # background URLSession and offline manifests
    Handoff/              # transfer state machine
    Explore/
    Knowledge/
    Settings/
    Persistence/          # read cache and migrations
    Resources/
  RadioVaultIOSTests/
  RadioVaultIOSUITests/
```

Prefer actor-isolated state owners for connection, playback, downloads and handoff. Keep SwiftUI views declarative and thin. Do not let screens issue ad-hoc URLSession calls; use one pinned authenticated API boundary and feature repositories/services above it.

## 16. Known risks and decisions required early

1. **Authenticated AVPlayer media:** prove a documented approach before expanding the player.
2. **iOS multicast discovery:** confirm current entitlement and distribution requirements; retain a secure fallback.
3. **Self-issued Server certificate:** implement fail-closed pinning and test renewal/re-pairing behaviour.
4. **Background restrictions:** test on physical devices; simulator success is insufficient.
5. **Multipart logical seeking:** build one timeline mapper and use it for streaming, downloads, progress and Moments.
6. **Handoff readiness:** decoder metadata is not proof of audible/advancing playback.
7. **Offline mutation conflicts:** only queue operations that have an explicit server reconciliation contract.
8. **API naming:** some types say `DesktopPairing` for historical reasons even though the trust contract is suitable for another native client. Avoid breaking existing clients solely to rename types.
9. **Current source has a Mac client alpha:** it compiles and packages but is not evidence that iOS behaviour has been tested.
10. **No public remote access:** private LAN is the accepted security boundary in this release line.

## 17. Rules that must not be weakened

- Server authority and one durable source of truth.
- Exact Server identity and certificate pinning.
- Per-client tokens and secret-safe logging.
- Canonical broadcast identity across recordings and files.
- Range-correct long-form media responses.
- No stale progress rewind or transient-zero overwrite.
- Transactional, idempotent playback handoff.
- Source continues until target readiness and commit.
- Cache state is visibly non-authoritative while offline.
- Full validation before changing API, schema, capability or package identities.

## 18. Return package expectations

When the iOS agent returns work, include:

- the complete modified source tree;
- the Xcode project and all Swift source/tests, without DerivedData;
- a concise change log and current build status;
- exact Xcode, iOS deployment target and Swift versions;
- simulator and physical-device test results, clearly separated;
- a list of any Server contract changes and compatibility implications;
- unresolved risks and reproduction steps for failures;
- an updated source SHA-256 manifest;
- a signed TestFlight archive only if the owner has supplied the required Apple credentials and authorised distribution.

Do not claim the iOS client is complete merely because it compiles. A working build requires real-device pairing, authenticated long-form playback, background/lock-screen behaviour, progress safety and at least one end-to-end handoff acceptance pass.

## 19. Core references inside this package

- `README.md`
- `BUILDING.md`
- `CHANGELOG.md`
- `docs/guides/ROADMAP.md`
- `docs/guides/ARCHITECTURE.md`
- `V0.34.0-STABLE.md`
- `V0.34.0-ALPHA19-BUILDFIX1-TRANSCRIPTION-IOS-HANDOFF.md`
- `V0.34.0-ALPHA19-BUILDFIX2-IPHONE-RANGE-PLAYBACK.md`
- `V0.34.0-ALPHA20-BUILDFIX1-IPHONE-REPEATED-HANDOFF.md`
- `V0.34.0-ALPHA20-BUILDFIX2-IPHONE-RANGE-CONTINUITY.md`
- `V0.34.0-ALPHA20-BUILDFIX3-DEVICE-LOCAL-VOLUME.md`
- `V0.35.0-ALPHA9-KNOWLEDGE-PORTABILITY.md`
- `MACOS-CLIENT.md`
- `TheRadioVault.Web/Contracts/WebApiRoutes.cs`
- `TheRadioVault.Web/Models/WebModels.cs`
- `TheRadioVault.Infrastructure/Services/NativeConnectedAccessService.cs`
- `TheRadioVault.Infrastructure/Services/LoopbackPlaybackHandoffService.cs`
- `TheRadioVault.Services/Models/PlaybackHandoffModels.cs`
- `TheRadioVault.Tests/Program.cs`

If documents conflict, the current executable contracts and tests take precedence over historical release notes. Preserve compatibility unless a deliberate, tested migration is documented.
