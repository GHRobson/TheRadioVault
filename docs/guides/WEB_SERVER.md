# Radio Vault Anywhere web companion

The secure LAN web companion shares canonical broadcast identity, multipart timelines, listening progress, queue and playback ownership with the authoritative desktop instance.

v0.29 Alpha 1 added `/api/v1/server-info` and `/api/v1/bootstrap`. Alpha 2 makes bootstrap the normal connected startup path and adds server-side show/year/month/exact-date/listening-status facets, explicit canonical broadcast IDs, reconnect-safe navigation and privacy-safe browser diagnostics.

The Alpha 2 shell is `radio-vault-anywhere-shell-v29`. Live audio remains non-cacheable and byte-range aware. Existing manual audio and artwork downloads retain `audio-v1` and `artwork-v1`, so upgrading the shell does not discard them. Database schema remains 45.

The sections below retain implementation history for the ownership, Safari range, offline and certificate-onboarding work.

## Safari byte-range streaming fix (buildfix17)

The live `/audio/{id}` endpoint now validates every Range request, returns 416 for invalid ranges, emits exact 206 headers, disables partial-response caching and logs each requested and served range. Browser stream URLs include a unique session query value so Safari cannot reuse a stale 206 response as a complete media resource. Shell cache: v21.

## Phone transfer diagnostics (buildfix16)

The web player instruments the complete PC-to-phone transfer lifecycle. A failed transfer opens an in-app report containing ownership state, session generation, media source and decoder state, all relevant HTML media events, seek application, heartbeat result, and exact `play()` promise errors.

## Authoritative PC-to-phone transfer position (buildfix15)

During a live PC-to-phone transfer, the web client now seeks to the exact shared session position. It does not choose a later stale local position, which previously could place Safari at media duration and trigger the misleading ownership-changed error. Shell cache: v21.

## Phone transfer race fix (0.26.0-beta1-preview6-buildfix18-phone-transfer-prime-retry-fix-archivist)

During an explicit PC-to-phone takeover, the browser temporarily treats the local output as active even before the server's ownership response arrives. This prevents an overlapping state poll from stopping Safari's decoder during the handoff. Web centre-button SVGs are now larger, centred and white. Offline shell cache: v19. Database schema: 33.

## Unified playback ownership (0.26.0-beta1-preview6-buildfix13-unified-playback-ownership-archivist)

`GET /api/v1/player` now exposes one authoritative session with separate desktop and phone snapshots, an owner device/client and a generation. Ownership remains assigned while paused. The web player therefore never guesses whether its Play/Pause control should operate the PC or the phone.

When the phone is inactive, its playhead and metadata continue to follow the authoritative session, all transport controls are disabled, and the centre control renders the custom inward-arrow phone icon. Pressing it pauses/releases the desktop, starts Safari's decoder muted at the shared position and speed, submits the ownership heartbeat, then unmutes only after acceptance. When the PC is inactive, WPF follows phone position/speed with a dormant decoder; its custom inward-arrow desktop icon starts that decoder and publishes desktop ownership.

Visible remote-PC transport actions and Continue-on-device text controls are removed. The only cross-device transport action is **bring playback to this device**.

Offline shell cache: v17. Database schema: 33.

## UI-safe automatic ownership (0.26.0-beta1-preview6-buildfix12-phone-handoff-deadlock-fix-archivist)

Desktop commands are serialised by a dedicated `_desktopCommandGate`. The short-lived `_playbackGate` protects only browser playback state and leases; it is released before the provider calls the WPF `IWebPlaybackController`, writes SQLite progress, or publishes synchronous events. This prevents automatic phone takeover from deadlocking the desktop dispatcher while still pausing PC audio before the phone heartbeat becomes authoritative.

The user-facing behaviour remains unchanged: normal phone Play actions take ownership automatically, PC Play reclaims ownership automatically, and no Continue-on-device control is used.

Offline shell cache: v16.

## Automatic playback ownership (0.26.0-beta1-preview6-buildfix11-automatic-playback-ownership-archivist)

Radio Vault exposes one shared session rather than a transfer workflow. The existing `/api/v1/player/command` route accepts the internal `claim-phone` command, which pauses the desktop and opens a short ownership-claim window. The phone's first `/api/v1/player/web-progress` update completes the claim. When the desktop later begins audible playback, `PlaybackOwnershipChangedEvent` clears the phone lease; the browser observes that state and pauses local audio. Ordinary delayed phone heartbeats cannot reclaim an active desktop session.

There is no user-facing Continue on Phone, Continue on Desktop or Continue on this device control. PC remote controls still operate the PC while desktop presence is active. Selecting a normal phone Play action starts local browser audio and claims ownership automatically. Offline playback remains local and reconciles progress after reconnection.

Offline shell cache: v15.

## Desktop remote controls (0.26.0-beta1-preview6-buildfix10-remote-control-revision-fix-archivist)

The buildfix8 presence projection remains the presentation layer and `/api/v1/player` remains the source of truth. Buildfix9 enables only the existing versioned `/api/v1/player/command` transport commands for the current PC session: `play`, `pause`, `seek`, `skip` and `speed`. Each request carries the browser client identity and the last observed desktop revision. A stale revision receives HTTP 409 plus the newest state rather than overwriting a newer desktop action. The provider also keeps a short per-client remote-control lease to prevent two browser clients from fighting over the desktop. Audio never transfers to the phone as a side effect of these controls.

Queue mutation, metadata changes, downloads, automatic handoff and ownership transfer remain outside this stage. Starting a broadcast explicitly on the phone continues to use the unchanged browser-audio path.

# Radio Vault Web Player

The installable web app now uses the same master icon, Radio Vault name, yellow theme colour and dark launch background as the Windows desktop application. iPhone Home Screen, Android PWA and desktop-browser installation metadata are generated from the desktop icon source.


Downloaded artwork is stored with the broadcast record in IndexedDB and mirrored into `radio-vault-anywhere-artwork-v1`. The Service Worker serves it through `/__offline_artwork__/{episodeId}`, giving cards, the full player and Media Session metadata a stable same-origin URL after Safari reloads. When Radio Vault reconnects, downloads created by older builds are checked and missing artwork is backfilled without downloading the audio again.

## Manual offline listening

Radio Vault web player stores explicitly downloaded broadcasts in IndexedDB under the web origin. A download begins only after the user presses **Download** on Broadcast Info or Now Playing. The active foreground transfer shows percentage/byte progress and can be cancelled. Completed downloads appear in the **Downloaded** filter and use a local Blob URL for playback, seeking and resume.

Listening progress is written to a separate local progress store. When the server becomes reachable, pending progress is posted to `/api/v1/broadcasts/{id}/offline-progress`. The server applies only forward progress (or completion), so an old phone cannot rewind newer desktop state.

When secure offline access is enabled, Radio Vault serves a one-time certificate onboarding page over the HTTP setup port and the application itself over HTTPS. After Safari trusts the Radio Vault Local Root CA and has opened the HTTPS page once, a Service Worker caches only the application shell. The phone can then cold-launch Radio Vault web player with the PC unreachable and immediately open its device-specific offline Dashboard or Downloads view. Downloaded media remains in IndexedDB and is never prefetched automatically.

# Local Web Server

Radio Vault web player provides opt-in LAN-only browser streaming with tokenised links. It can run in legacy HTTP mode or secure dual-port mode: HTTP handles certificate onboarding only, while HTTPS carries the UI, APIs, artwork and audio. It never exposes arbitrary paths or the database.

Current mobile views are Dashboard, Library, Downloads, Queue and Archive Health. The Dashboard remains available offline and is rebuilt from downloaded broadcasts and locally saved progress. Now Playing follows one active phone or computer session without explicit transfer buttons. The WPF Now Playing panel also follows phone-selected episode, position and speed state so the PC is ready to resume the shared session. Audio uses HTTP byte ranges for seeking on iPhone.

The current companion already supports progress write-back, queue mutation, remote playback commands, artwork, research details, Archive Health, offline downloads and shared device ownership. The v0.29 work now focuses on capability-led startup, responsive polish, offline storage repair and the application-service contracts required by future LAN desktop clients.

## Phoenix 2 extraction

The network host now lives in the independent `TheRadioVault.Web` assembly. The desktop shell provides an `IWebArchiveProvider` adapter and persisted settings only. The web client now supports show filtering and token-protected artwork delivery in addition to existing search, curated views and audio streaming.

Endpoints introduced or retained:

- `GET /api/episodes`
- `GET /api/shows`
- `GET /audio/{episodeId}`
- `GET /artwork/{episodeId}`

## API v1 and deep links

Phoenix 3 adds read-only token-protected endpoints under `/api/v1`: broadcasts, individual Broadcast Info, search, show facets, favourites, research, Archive Health, player state and queue. Legacy `/api/episodes` and `/api/shows` routes remain temporarily available.

Desktop Broadcast Info can copy a private `/broadcast/{episodeId}?token=...` link. The link opens the mobile Broadcast Info view directly while Radio Vault and Web access are running on the same private network.

## Phoenix 3 privacy hardening

Versioned API responses never serialise local audio or artwork filesystem paths. The server retains those paths only inside the provider for validated `/audio/{id}` and `/artwork/{id}` requests. Research links are exposed only when they use HTTP or HTTPS, and the mobile document applies a restrictive Content Security Policy plus `Referrer-Policy: no-referrer` so the private token is not sent to external source sites.

## Phoenix 4 live-state API

Additional token-protected endpoints:

- `GET /api/v1/events?after={sequence}` — bounded change feed for library, research, metadata, favourites, listening status, queue, playback and job events.
- `GET /api/v1/jobs` — retained background-task state and progress.
- `POST /api/v1/jobs/{jobId}/cancel` — requests cooperative cancellation when the task still permits it.
- `POST /api/v1/broadcasts/{episodeId}/favourite` — sets favourite state from a JSON `favourite` boolean.
- `POST /api/v1/broadcasts/{episodeId}/listening-status` — sets listened state from a JSON `played` boolean.

Radio Vault web player displays the desktop player's current broadcast and position, reports active background work, and refreshes only after relevant events. It still does not provide remote desktop play/pause control in this slice. Mutation routes remain explicit and narrow; they cannot execute SQL, browse the filesystem or change arbitrary fields.

## Phoenix 5 branded player and remote control

The browser keeps a hidden `<audio>` element for standards-compliant byte-range streaming, iOS background audio and Media Session integration. All visible controls are Radio Vault UI: artwork, mini-player, expanded player, seek bar, skip controls, speed and favourite/listened actions. Explicit device-handoff controls have been retired in favour of shared live state.

`POST /api/v1/player/command` controls the desktop through `IWebPlaybackController`; commands include an expected playback revision and a stable browser client ID. `POST /api/v1/player/web-progress` records phone progress under a short client lease. Queue mutations are available only through explicit token-protected endpoints. Phone playback also publishes a typed event so the Windows player bar can show which broadcast is active on the phone. The API remains LAN-only and never exposes local paths.


## HTTPS and iPhone certificate onboarding

Secure mode creates a persistent local root certificate in Radio Vault's data folder and a renewable server certificate signed by that root. The server certificate includes current private IPv4 addresses, localhost, the machine name and `radiovault.local`. Restarting Web access renews the server certificate without requiring the phone to reinstall the root profile.

The HTTP setup endpoint exposes only:

- `/secure-setup`
- `/secure-profile.mobileconfig`
- `/secure-root.cer`

All require the existing private token. Other HTTP paths redirect to the HTTPS port. HTTPS uses TLS 1.2 or TLS 1.3. The Service Worker caches the embedded mobile application shell and deliberately ignores audio/API responses; broadcasts remain manual IndexedDB downloads.


## Secure Offline Access diagnostics
The desktop setup window validates private-key availability, validity, Server Authentication EKU, all current DNS/IP SANs, the custom-root chain, and the certificate fingerprint actually presented by each active HTTPS listener.


### Buildfix18 phone transfer retry
Safari may briefly collapse a newly primed ranged stream at the transfer seek point. The web player now suppresses stale old-episode media heartbeats during transfer and automatically rebuilds and restarts the correct stream after ownership acceptance.
