# Unreleased — 0.44 architecture and testability

- Began splitting the mobile session façade by extracting shared playback-ownership and handoff-evidence rules into a focused coordinator with behavioural regression tests.
- Extracted multipart logical-position mapping, decoder-settling protection, seeking and completion state into a side-effect-free mobile playback timeline with focused regression coverage.
- Extracted remote playback observation, deterministic playhead projection and committed handoff source-stop acknowledgement behind fakeable transport and decoder boundaries.
- Extracted serialized mobile library metadata synchronization—including change-feed cursors, reset paging, delta/deletion application, durable cache adoption and activity state—behind a fakeable transport with focused behavioural tests.
- Extracted durable mobile offline-mutation synchronization for favourites, listened state and Moments, including ordered single-flight replay, duplicate acceptance, already-applied recovery and first-failure retention.
- Extracted mobile download lifecycle, Wi-Fi/retention policy, pause/resume/cancel state, storage maintenance and durable-index projection behind a focused coordinator, plus isolated downloaded-progress authority reconciliation from the session façade.
- Extracted cache-first mobile Explore page/image hydration and Knowledge snapshot, coverage and triage queries behind fakeable transports, reducing the session façade while preserving offline Library fallbacks.
- Extracted mobile discovery/pairing transitions and cache-first Library projection, filtering, archive grouping and search contracts behind focused coordinators, including normalized duplicate-show handling.
- Extracted the versioned player handoff, playback command and queue HTTP routes from the 10,000-line web-server dispatcher into one focused partial boundary while preserving route order, method rules and handler bodies.
- Extracted authenticated federation status, Library synchronization, remote administration, Research-pack and Wiki-pack routing from the main web dispatcher into a single ordered boundary while leaving pairing before authorization and normal client APIs after it.
- Introduced a platform-neutral desktop playback state machine for local transport, pending intent and remote handoff state, plus a remote-progress interpolator that removes small heartbeat corrections without hiding deliberate seeks or ownership changes.
- Began splitting the shared test runner by moving web-route, Knowledge integration and desktop source inspections into a dependency-free `TheRadioVault.SourceChecks` project that runs separately from behavioral regression tests in both local and Windows release gates.

# 0.41.0

- Made the source repository public so standard GitHub-hosted parity builds are no longer metered, while retaining read-only workflow permissions.
- Added a one-command local macOS/iOS release gate and native Mac Client/Server packaging so normal development does not depend on hosted CI.
- Hardened iOS Lock Screen metadata and remote media commands by coalescing playback updates and keeping UIKit/MediaPlayer work on the main thread.
- Aligned Client, Server and iOS product versions and added a regression check that prevents future drift.
- Added the cross-platform source audit and staged roadmap through Radio Vault 1.0.

# 0.35.0-alpha9-buildfix3

- Adds the first Apple Silicon macOS Client bundle, reusing the complete Avalonia interface and the existing paired Windows Server contracts without adding a macOS Server.
- Adds native AVFoundation audio playback for server streams and downloaded files, plus Mac bundle metadata, icon generation, hash manifest and an included signing/notarization finalizer.
- Preserves the complete Alpha 7, Alpha 8 and Alpha 9 feature line while moving large Knowledge imports into server-owned background jobs that continue if a window closes or a client disconnects.
- Replaces scrolling activity strips with measurable import percentages and processed-record counts in the Client, native Server settings and Radio Vault Web.
- Speeds archive-wide import by decoding the portable SQLite database in place and building the archive match index once per preview or transaction instead of querying the library for every record.
- Adds native Knowledge Database preview, import, cancellation and archive-wide export to the Server app; Radio Vault Web retains the archive-wide export contract without a show picker.
- Keeps import writes transactional, creates and verifies the pre-import Server backup, and reports schema-safe conflicts without retaining partial changes.
- Blocks older Client or Server installers from replacing a newer installed Radio Vault version.
- Adds regression coverage for resumable jobs, determinate progress, indexed matching and installer downgrade protection, plus a full benchmark of the 8,906-record enriched archive package.

# 0.35.0-alpha9-buildfix2

- Removes the obsolete show and year picker from native Knowledge Database export; the action is now explicitly labelled `Export full Knowledge Database…`.
- Makes the desktop, paired-client, server and Radio Vault Web export contract archive-wide only, preventing a scoped selection from being reintroduced accidentally.
- Removes Radio Vault Web's hidden requirement to select a show before exporting the complete Knowledge Database.
- Adds regression coverage proving both export interfaces are archive-wide and that the portable database still contains every show and transcript.

# 0.35.0-alpha9-buildfix1

- Fixes the confirmed server-side Knowledge import failure caused when an authoritative pack encountered a pending reconciliation candidate and attempted to write the non-schema `ambiguous` research state.
- Keeps ambiguous records visibly flagged for review using the supported `conflicting_information` state across import cleanup, review completion and conflict resolution.
- Shows final apply failures in the Knowledge import panel instead of leaving the error only in the small workspace status line.
- Adds a database-level regression test for the exact state constraint and retains Alpha 9's verified pre-import server backup.

# 0.35.0-alpha9

- Makes Knowledge import reconcile an incoming Explore page with an existing page that has the same canonical slug, even when an AI-produced pack carries a different GUID.
- Extends local and paired-server import timeouts for large archive-wide databases and returns the underlying safe failure detail with a diagnostic reference.
- Creates and verifies a retained server database backup before applying any Knowledge import.
- Validates SQLite integrity and required tables on import and validates every generated database before an atomic export replaces an existing file.
- Embeds a structured AI handbook, table-by-table schema guide, evidence rules, stable-identity rules, return checklist, advisory validation data and append-only agent change log in every exported `.trvknowledge` database.
- Adds regression coverage for existing-page reconciliation and the embedded AI documentation contract.

# 0.35.0-alpha8

- Renames Wiki to Explore and Research to Knowledge throughout the native and Web navigation while preserving stable internal routes and data contracts.
- Opens cited and timeline broadcasts on Broadcast Info instead of starting playback, and removes redundant Explore panels from compact broadcast surfaces.
- Rebuilds the Now Playing transcript reader to match Broadcast Info and gives the empty queue a clear icon and message.
- Locks Dashboard Up Next and On this day to matching heights and contains the Up Next progress bar correctly.
- Makes every Knowledge Database export archive-wide across every show and year, including all matching transcripts and the complete Explore snapshot.
- Repairs server imports of AI-enriched Knowledge Databases whose otherwise valid citation sources omit a title by deriving a stable readable title.
- Moves Now Playing to the bottom of native and Web navigation and replaces missing artwork with a line-art Radio Vault mark.

# 0.35.0-alpha7

- Rebuilds Wiki reading around an encyclopaedia-style article hierarchy with a readable lead, contents, infobox, dated images, chronology and numbered references.
- Makes the Wiki dashboard more story-led with a searchable hero, live knowledge statistics and a prominent On this date history strip.
- Adds a dedicated Research dashboard describing the size, date range, show coverage, source coverage and outstanding work in the Archive Knowledge Database.
- Removes redundant Explore in the Wiki panels from Research and Broadcast Info while retaining direct people and topic links.
- Moves the neutral, app-themed transcript reader below Topics on Broadcast Info.
- Enriches Continue Listening and On this day with contextual summaries drawn from the Wiki.
- Rebuilds Search with softer rounded surfaces, clearer filter groups, friendlier results and correctly styled discovery cards.

# 0.35.0-alpha6

- Replaces separate Research and Wiki exchange packs with one inspectable `.trvknowledge` Archive Knowledge Database containing Research, Wiki pages, citations, images, timelines, transcripts and stable cross-links.
- Makes the show Timeline Explorer a smooth vertical history in both the native client and Radio Vault Web.
- Keeps the client splash visible for at least three seconds while startup caching completes and replaces the transport loading glyph with a real spinner.
- Moves Transcription Studio under Research, removes the standalone transcript navigation item and adds collapsible transcript reading to Broadcast Info.
- Makes Wiki archive sources open their exact cited broadcast or Moment and keeps inline/related Wiki navigation clickable.
- Matches the Downloads navigation colour to Settings and advances LAN capability generation to 40 and the Web shell cache to v65.

# 0.35.0-alpha5

- Adds canonical topic identities spanning Research records, ordinary broadcast tags and Topic Wiki pages.
- Automatically merges only safe capitalisation, punctuation, spacing and possessive variants when the Wiki opens.
- Preserves every original name as an alias and canonicalises future Research edits.
- Adds ranked meaning-based suggestions with confidence, evidence counts and explicit one-click approval.
- Consolidates Wiki text, citations, images, timeline events and relationships into the strongest page.
- Archives and redirects merged Wiki pages instead of deleting their IDs or revision history.
- Adds durable topic merge history plus native and Radio Vault Web cleanup status and controls.
- Advances the database schema to 47, connected capability generation to 39 and the Web shell cache to v64.

# 0.35.0-alpha4

- Rebuilt the Wiki around distinct Home, Browse, Article and Timeline Explorer destinations.
- Added browser-style Back and Forward history, full-width article reading and a compact three-dot management menu.
- Added search suggestions, automatic inline page links, missing-page indicators, related pages and backlinks.
- Expanded the dashboard with eras, On this date and richer show, person, topic and recent-change discovery.
- Added an interactive show Timeline Explorer with a year scrubber and exact broadcast/Moment playback.
- Added article infoboxes, richer image/citation presentation and clearer read-versus-edit separation.
- Added combined citation, broken-link, duplicate-page and orphan-page quality auditing.
- Brought navigation, discovery, articles and Timeline Explorer to responsive Radio Vault Web.

# 0.35.0-alpha3

- Replaces the Wiki editor as the section's front door with a dedicated exploration dashboard.
- Adds featured starting points, recently updated pages, show, person and topic entry points, and timeline-rich histories.
- Adds direct Wiki home, browse, random-page and Manage Wiki navigation while keeping article reading and authoring deliberately separate.
- Keeps dashboard authoring controls out of the way until the user enters the Wiki management workspace.
- Renders Markdown articles with contents navigation, internal page links and dated/captioned images in the native reader.
- Surfaces related Wiki pages from Library, Broadcast Info, Now Playing, Search and Research, and makes people/topic chips open the relevant Wiki page or matching search.
- Makes timeline and citation links play the canonical `EpisodeId` or `MomentId` at the exact stored timestamp.
- Adds revision comparison and safe restoration as a new revision, plus an automatic post-import citation audit.
- Adds a read-focused Wiki dashboard, search and article reader to Radio Vault Web, including images, references and exact archive links.
- Keeps database schema 46 and connected capability generation 38 because the change is entirely within the native Client experience.

# 0.35.0-alpha2

- Turns the Wiki foundation into an archive-aware authoring workspace with separate reading and editing modes.
- Adds idempotent starter-page generation for every show plus recurring people and topics found in Library and Research metadata. Existing Wiki pages are never overwritten.
- Adds native human editors for citations and source provenance, dated/licensed images, and timeline events.
- Adds archive search and selectors so timeline events can link to exact broadcasts and Moments.
- Extends `.rvwiki` exports with `archive-context.json`, a read-only catalogue of shows, broadcasts, people/topics and transcript availability for an AI agent.
- Replaces count-only import confirmation with per-page Added, Changed, Unchanged and Protected decisions; newer human revisions remain protected.
- Keeps database schema 46 and advances connected capability generation to 38 for the expanded Wiki client contract.

# 0.35.0-alpha1-buildfix1

- Makes Deep Research Pack import tolerant of harmless AI-produced JSON variations, including a single `supports` value in place of an array and numeric confidence metadata in place of text.
- Keeps exported packs canonical: text collections are written back as arrays and scalar metadata as strings.
- Returns the exact incompatible JSON path for genuinely malformed packs instead of an unexplained preview flash.
- Reproduces full preview and transactional import for the supplied Ron & Ron, Bennington and Ron Bennington Interviews packs against disposable databases.

# 0.35.0-alpha1

- Introduces the first end-to-end Radio Vault Wiki workspace, owned by the dedicated Server and editable from the native Client.
- Adds revision-protected human editing for Wiki pages, aliases, Markdown articles, page types, publication status and revision notes.
- Adds structured Wikipedia-style sources and citations, image binaries with provenance/licence/date metadata, page relationships and scrollable timeline events linked to broadcasts and Moments.
- Adds portable `.rvwiki` authoring packs containing Markdown, JSON metadata, cited sources, dated images, timelines, a machine-readable schema and instructions for external AI agents.
- Requires pack preview before import, checks the exact package SHA-256 at apply time and skips pages whose newer human revision would otherwise be overwritten.
- Adds authenticated Server endpoints and loopback Client adapters for Wiki editing and pack transfer, advancing database schema to 46 and connected capability generation to 37.

# 0.34.0

- Promotes the tested dedicated Server, native Client and Radio Vault Web architecture to the stable 0.34 release without changing schema 45, API v1, pairing identities or installer application IDs.
- Locks the accepted iPhone playback and transactional handoff repairs, cache-first native startup, encrypted persistent response cache, server-owned transcription, Research-pack exchange and native download workspace.
- Corrects the main README, build guide, Library Truth status wording and generated architecture report so the release no longer describes completed client/server work as an unfinished alpha.
- Advances the Radio Vault Web shell cache to v63 so installed browser and Home Screen clients receive the stable release identity.
- Updates the .NET 8 SQLite stack to the current security rollup, removing the high-severity advisory inherited through the older native SQLite package.
- Makes stable source packages source-only, excluding historical root-level installer binaries and regenerating a versioned SHA-256 manifest.

# 0.34.0-rc1-buildfix5

- Fixes an iPhone handoff failure where a stalled positioned stream was incorrectly reported as Safari refusing the user's playback tap.
- Gives the positioned iPhone stream a bounded readiness window, then starts the ordinary canonical-media fallback on the already activated audio element before applying the shared playhead.
- Keeps playback ownership on the source device until the fallback decoder is running and aligned, and records distinct positioned-stream, fallback and genuine permission diagnostics.
- Advances the Radio Vault Web shell cache to v62 so installed Home Screen clients receive the playback repair.

# 0.34.0-rc1-buildfix4

- Rebuilds the client launch screen as a compact Radio Vault shell with a larger approved logo, truthful server/cache stages and determinate startup progress.
- Reworks Radio Vault Web around the desktop visual language, retaining the full desktop rail while replacing the narrow-screen tab bar with a full-height accessible hamburger drawer and scrim.
- Rebuilds the Web Library as compact native-style rows with smaller play, download and overflow actions so large libraries are easier to scan on phones and computers.
- Adds a first-class Downloads page to the Windows client, including persistent server-scoped offline copies, progress, cancellation, repair, removal and local-first playback.
- Adds Download and Remove download actions to native Library broadcasts and keeps listening progress available when playback uses a device-local copy.
- Replaces the text handoff marker in the client player with a monitor-and-arrow icon tailored to moving playback onto the PC.
- Fixes installed iPhone home-screen artwork by generating the Apple touch icon as an opaque, full-bleed Radio Vault tile, serving shell artwork on authenticated same-PC HTTP sessions and versioning icon URLs to bypass stale Safari caches.
- Advances the Radio Vault Web shell cache to v61 so installed browsers receive the rebuilt navigation, Library and corrected artwork.

# 0.34.0-rc1-buildfix3

- Gives Radio Vault Server its own approved family variant: the broadcast mast becomes a bold two-slot server tower while retaining the main yellow tile, signal waves and dark vault ring.
- Removes the small top-ring markers so the server mark remains clean and distinct at Windows tray and taskbar sizes.
- Applies the server identity only to the server executable, tray icon, settings header, shortcuts and installer; Radio Vault Client and Radio Vault Web retain the main broadcast logo.
- Generates dedicated 16, 20, 24, 32, 40, 48, 64, 128 and 256 pixel server icon frames from the approved server master.

# 0.34.0-rc1-buildfix2

- Introduces the new Radio Vault identity: a yellow archive tile with a dark broadcast/vault mark, subtle highlights and dimensional shadowing matched to the application's icon language.
- Replaces the temporary mast and `RV` branding on the client splash screen, native navigation shell, server settings and Radio Vault Web startup/header surfaces.
- Regenerates the Windows executable, tray, shortcut and installer icon as a multi-resolution icon family derived from the approved master artwork.
- Regenerates standard, Apple touch and maskable PWA icons with platform-appropriate safe areas and backgrounds.
- Advances the Radio Vault Web shell cache to v60 so installed phone and browser clients receive the new identity immediately.
- Makes the server settings version label derive from the built assembly so it can no longer remain stuck on an obsolete alpha number.
- Adds a repeatable asset generator so every product surface stays derived from the same approved master.

# 0.34.0-rc1-buildfix1

- Fixes Deep Research Pack selection briefly flashing and then returning to the unchanged Research screen without an explanation or final import action.
- Adds a persistent inline failure panel that names the selected pack, displays the server's real error and provides a one-click retry without risking a partial import.
- Preserves server diagnostic references in native-client upload, import and download failures instead of reducing every response to a status code.
- Raises the guarded authenticated Deep Research Pack limit from 64 MB to 256 MB for transcript-rich whole-show packages and allows up to ten minutes for their LAN upload.
- Validates empty, missing and oversized packs before upload and keeps the import confirmation hidden unless server analysis succeeds.
- Corrects the preview copy to explain that the connected server performs the transactional import and protects manual edits.
- Adds an end-to-end server upload-preview test and advances connected capability generation to 36.

# 0.34.0-rc1

- Freezes the 0.34 feature set as the first release candidate after the Alpha 20 client/server, Radio Vault Web, transcription and handoff hardening cycle.
- Fixes healthy 15-second connection probes repeatedly emptying the native client's short-lived response cache and forcing unnecessary remote reloads.
- Clears stale memory responses once when a server genuinely recovers, and immediately removes the client's cached-only flag after the independent live probe succeeds.
- Fixes a rapid server stop/start race by binding HTTP, HTTPS and discovery loops to the immutable listener and cancellation generation that created them.
- Adds automated cached-outage recovery and eight-generation server restart tests.
- Revalidates installer upgrade preservation for the database, pairing, settings, certificates, transcripts, models and encrypted client cache.
- Retains Buildfix 2 continuous Safari range playback and Buildfix 3 device-local laptop volume isolation.
- Advances connected capability generation to 35 and Radio Vault Web shell cache to v59.

# 0.34.0-alpha20-buildfix3

- Fixes playback moving from Radio Vault Web back to the native client at an unexpectedly high laptop volume.
- Stops assigning Radio Vault's internal volume directly to the Windows WASAPI application session whenever a handoff rebuilds the decoder output.
- Applies the player slider and temporary transactional mute inside Radio Vault's decoded audio stream instead, preserving the volume chosen in Windows Volume Mixer.
- Keeps volume device-local: Web volume, laptop system volume and Radio Vault's own player level are no longer allowed to overwrite one another during handoff.
- Adds a regression contract for native handoff volume-session isolation.
- Advances connected capability generation to 34 and Radio Vault Web shell cache to v58.

# 0.34.0-alpha20-buildfix2

- Fixes long-running iPhone playback developing small periodic forward skips and then increasingly noticeable backward repeats.
- Proves the cause with real encoded audio: independently reopened Safari-style byte ranges did not match one continuous positioned WAV representation.
- Keeps one bounded Media Foundation decoder timeline per Web playback session, so consecutive HTTP ranges contain byte-identical PCM at every boundary.
- Retains lossless-quality positioned WAV playback, exact shared playhead reporting and the Alpha 20 Buildfix 1 repeated-handoff correction.
- Cleans up idle decoder sessions after ten minutes and disposes all sessions when Radio Vault Server stops.
- Extends the real-media regression to compare a continuous positioned response with the same audio assembled from 32 KB ranges.
- Advances connected capability generation to 33 and Radio Vault Web shell cache to v57.

# 0.34.0-alpha20-buildfix1

- Fixes Radio Vault Web on iPhone leaving **Move to this device** behind an endless preparation spinner after playback has moved from the phone back to a native client.
- Stops speculative dormant audio-decoder preparation on iOS, where Safari requires replacement and playback of the decoder to remain inside the listener's direct tap.
- Keeps desktop-browser dormant preparation intact while ensuring it can never disable either iPhone Move control.
- Retains the transactional handoff, positioned-stream alignment and decoder proof inside the iPhone user gesture.
- Adds a regression contract for repeated client → iPhone → client → iPhone round trips.
- Advances connected capability generation to 32 and the Radio Vault Web shell cache to v56 so phones receive the corrected interface.

# 0.34.0-alpha20

- Freezes new feature work for a release-hardening pass across client startup, server connection state, playback and handoff, transcription, Library/Research paths and both Windows installers.
- Derives the client splash, Settings and Research-pack version from the built application instead of maintaining separate release strings that can become stale.
- Shows the capability generation negotiated with the active server and replaces obsolete Radio Vault Anywhere and local-transcription wording with accurate server/client ownership language.
- Removes the obsolete local-only connection message that described native networking as future work.
- Makes both installer entry points rebuild and validate their client or server payload before Inno Setup runs, preventing a newly named installer from silently containing an older executable.
- Adds an Alpha 20 regression contract for visible release truth and installer freshness.
- Advances connected capability generation to 31 and Radio Vault Web shell cache to v55.

# 0.34.0-alpha19-buildfix8

- Fixes iPhone Safari playback succeeding for the first broadcast but failing after changing to another broadcast.
- Tracks the broadcast actually assigned to the audio element instead of inferring decoder identity from an asynchronously changing manifest.
- Opens a different target broadcast's server-positioned stream synchronously inside the playback tap, before transfer or manifest requests can reuse the previous decoder.
- Keeps that fresh decoder muted while another playback session exists and unmutes it only after the transactional ownership boundary succeeds.
- Extends phone diagnostics with the audio element's broadcast identity and adds a dedicated consecutive-switch regression.
- Strengthens positioned-stream coverage to prove that the requested source offset changes both the decoded representation length and its validator.
- Advances connected capability generation to 30 and Radio Vault Web shell cache to v54.

# 0.34.0-alpha19-buildfix7

- Fixes Radio Vault Web receiving the correctly positioned Safari-safe stream but occasionally interpreting its media clock as though it still began at canonical zero.
- Stores the positioned stream's canonical base synchronously when the user's playback gesture creates the media source, before manifest loading or transfer responses can interleave.
- Simplifies logical playback time to `source canonical base + decoder clock`, removing the fragile later manifest-offset attachment.
- Keeps normal canonical parts based at their own logical start and retains positioned-stream backwards-seek replacement.
- Extends phone diagnostics with the source's canonical base, derived logical playhead and authoritative transfer-ticket positions.
- Advances connected capability generation to 29 and Radio Vault Web shell cache to v53.

# 0.34.0-alpha19-buildfix6

- Fixes iPhone playback varying by recording because Safari could report a successful seek while silently returning to the start of an MP3 with an incomplete or unreliable seek table.
- Adds a server-positioned, range-capable PCM stream for iPhone playback and non-zero Radio Vault Web resume and handoff positions, so the server handles source decoding/seeking and Safari receives one consistent representation.
- Tracks the positioned stream's logical offset separately from its media-element clock, keeping the shared playhead, progress and canonical multipart timeline accurate.
- Supports backwards seeking from a positioned stream by opening a new positioned representation inside the listener's seek gesture.
- Adds an end-to-end regression that creates a real MP3, seeks it through the web server and verifies the returned partial WAV representation contains playable decoded audio.
- Advances connected capability generation to 28 and Radio Vault Web shell cache to v52.

# 0.34.0-alpha19-buildfix5

- Fixes the server rejecting a proven audible phone decoder even though the public playback session correctly reported owner `None`.
- Treats an authority with no source episode and no running playback as genuinely output-free even when the provider retains its internal `Server` sentinel label.
- Keeps the audible-decoder exception limited to this no-output condition; paused or playing sessions with a real episode still require the normal transactional handoff proof.
- Advances connected capability generation to 27 and Radio Vault Web shell cache to v51.

# 0.34.0-alpha19-buildfix4

- Fixes first playback from the phone Library being rejected after the decoder had already loaded and played successfully, because Safari's audible-play permission expired during the later transactional work.
- Adds a canonical `media-start` route so an unowned phone session can open the correct recording synchronously inside the original Library tap without waiting for a manifest lookup.
- Starts that no-owner path audibly within the user gesture and retains the same blessed decoder through manifest attachment, alignment and ownership commit.
- Extends the transfer proof contract to accept an audibly running decoder only when the server confirms there is no current playback owner; cross-device transfers still require a muted target until the source stops.
- Advances connected capability generation to 26 and Radio Vault Web shell cache to v50.

# 0.34.0-alpha19-buildfix3

- Fixes the remaining iPhone Safari failure where the full recording briefly loaded and was then replaced by the browser's 0-1 byte MP3 probe, collapsing duration to 1 ms.
- Removes the incorrect `Vary: Range` separation so successive `206` responses remain parts of one strongly validated representation, and implements `If-Range` fallback semantics.
- Simplifies canonical audio headers to Safari's proven MP3 range-response shape and adds `no-transform` protection.
- Reuses the healthy iPhone decoder already warmed at zero instead of replacing its source inside the Move gesture; decoder-clock proof still occurs before the real seek.
- Stops retrying seeks after WebKit has ended or collapsed a decoder, keeping transactional ownership safely at the original output.
- Advances connected capability generation to 25 and Radio Vault Web shell cache to v49.

# 0.34.0-alpha19-buildfix2

- Repairs ordinary Radio Vault Web playback and transactional handoff on iPhone Safari at the shared canonical media-stream layer.
- Gives every canonical audio response a stable strong `ETag`, `Last-Modified` value and consistent private caching policy so Safari can safely assemble successive MP3 byte ranges.
- Adds `Vary: Range` and an inline media disposition while retaining exact `206`, `Content-Range`, `Content-Length` and identity-encoding behavior.
- Waits for a freshly started iPhone decoder's playback clock to advance before applying a resume or handoff seek, rather than treating metadata availability as decoder readiness.
- Adds privacy-safe alignment diagnostics that distinguish stream startup, decoder-clock proof and the later seek.
- Advances connected capability generation to 24 and Radio Vault Web shell cache to v48.

# 0.34.0-alpha19-buildfix1

- Fixes server-owned transcription jobs failing at the final save with SQLite `CHECK constraint failed: source` by storing generated transcripts with the portable `local` source value.
- Adds a defensive transcript-source normalization boundary so unexpected future labels cannot discard otherwise completed transcription work.
- Replaces iPhone Safari's reused paused handoff decoder with a fresh cache-busted stream started muted inside the Move tap, preventing long MP3 duration state from collapsing to the old playhead.
- Keeps iOS dormant handoff preparation at the start of the media resource instead of repeatedly seeking a paused range-backed decoder; non-iOS clients retain aligned dormant preparation.
- Preserves Alpha 19's truthful cache-first splash, persistent encrypted response cache and incremental post-launch refresh.
- Advances connected capability generation to 23 and Radio Vault Web shell cache to v47.

# 0.34.0-alpha19

- Replaces the obsolete local-archive splash copy with the actual selected Radio Vault Server, connection location and encrypted client-cache state.
- Keeps the splash visible until the Dashboard, show navigation, Search, Moments and Queue have been hydrated, so the main desktop opens populated instead of visibly loading its first page.
- Uses the existing encrypted response cache as a cache-first warm-start source while retaining live network reads for uncached routes and all mutations.
- Persists a per-server synchronization cursor and checks the server's bounded change journal after launch.
- Adds a metadata-only Library sync mode so the client can determine changed areas without downloading a duplicate full Library projection.
- Refreshes only affected native views for library, queue, moment and transcription changes; a no-change launch performs no view reload.
- Retains safe full refresh when the server restarts, the journal rolls over or an older client has no usable cursor.
- Advances connected capability generation to 22 and Radio Vault Web shell cache to v46.

# 0.34.0-alpha18-buildfix1

- Fixes an iPhone Safari handoff failure where a redundant sub-second seek could make a healthy long-form MP3 decoder report the current playhead as its new duration and immediately end.
- Preserves an already-aligned dormant phone decoder when it is within the transactional commit tolerance instead of assigning `currentTime` again during the transfer gesture.
- Keeps meaningful seeks, multipart logical-position mapping, decoder proof and transactional source ownership checks unchanged.
- Advances the connected capability generation to 21 and the Radio Vault Web offline shell cache to v45 so phones receive the corrected player immediately after the server upgrade.

# 0.34.0-alpha18

- Makes every explicit Play gesture use the transactional move path whenever another phone, browser or native client owns playback.
- Prevents a delayed non-transactional decoder claim from stealing an active playback generation during a multi-device race.
- Bounds retry-safe remote requests to five seconds per attempt so unreachable servers enter cached read-only mode promptly instead of inheriting a ten-minute operation timeout.
- Correctly exposes cached read-only connection state and clears short-lived native response memory after an independent healthy-server probe.
- Refreshes loaded Dashboard, Search, Library, Moments, Transcripts and Queue data after a bounded 30-second age while retaining instant reuse for recent views.
- Refreshes the server-backed show navigation after the same bounded age so newly scanned shows appear without restarting the remote client.
- Advances the connected capability generation to 20 and the Radio Vault Web offline shell cache to v44.
- Makes installer upgrade-directory, task retention and diagnostic logging behaviour explicit while keeping all user data outside the installed binary folders.

# 0.34.0-alpha17

- Fixes Radio Vault Web handoffs timing out against large server libraries even though the browser decoder was already ready.
- Replaces the transfer path's forced full-library rebuild with a direct, indexed lookup of the requested canonical broadcast.
- Uses the same bounded lookup at final commit and when preserving a different outgoing broadcast, preventing the delay from moving to a later transfer stage.
- Keeps the source device authoritative until the existing transactional handoff fully commits; this changes lookup cost without weakening playback ownership safety.

# 0.34.0-alpha16

- Stops force-reloading Dashboard, Search, Library and Transcripts every time their navigation tab is selected.
- Adds a short-lived in-memory cache for server-backed Library and client read responses, invalidated immediately by native-client mutations.
- Warms Search, Moments and Transcripts after the Dashboard becomes usable instead of making the initial window wait for Search first.
- Prevents a passive native client from sending playback heartbeats or durable progress while another phone or client owns the session.
- Restores a visible move-to-this-device arrow in the native centre transport button, with the existing transactional handoff command and tooltip.

# 0.34.0-alpha15

- Restores the explicit show-selection question whenever a Library folder is added from the dedicated Server settings app.
- Adds **Change selected show** for existing mixed or incorrectly assigned server folders and automatically rescans after the correction.
- Refreshes the native client's server-backed show navigation whenever Library is opened, so newly imported shows appear without restarting the client.
- Moves native playback from legacy WaveOut to Windows shared-mode WASAPI and bypasses the speed adapter at 1x for a cleaner direct decoded-audio path.
- Keeps server streaming byte-for-byte original; there is still no bitrate reduction or audio transcoding in either client path.

# 0.34.0-alpha14

- Renames the browser/PWA companion from **Radio Vault Anywhere** to **Radio Vault Web** throughout the visible client, server, browser shell, installable manifest and diagnostics.
- Restores a dedicated Radio Vault Web control card in Server settings with open, copy, first-time HTTPS setup and private-link renewal actions.
- Returns the private Web and secure-setup addresses to authenticated paired native clients, resolving the missing client-side Web controls after the dedicated-server migration.
- Adds locally generated phone QR codes to both Server settings and Client settings without sending private URLs or access tokens to an external service.
- Adds a second QR for the first-time HTTPS certificate setup flow, while keeping server lifecycle, ports, certificates and link renewal server-owned.
- Advances the Web shell generation to 14 and the compatible service-worker shell cache to v43 so installed browser clients receive the Radio Vault Web branding.
- Preserves existing internal Anywhere route, cache-prefix and offline-storage identities so the visible rename does not discard downloads, sessions or playback handoff state.

# 0.34.0-alpha13-buildfix1

- Restores Library-folder administration in the dedicated server settings app with a server-local folder picker, enabled/disabled state, safe registration removal and an immediate Library scan action.
- Keeps folder paths server-owned: clients display the authoritative list but direct path changes to the computer that physically owns the archive drives.
- Restores **Mark as listened** and **Mark as unlistened** to Library broadcast right-click menus and routes both actions through the active server's canonical listening-state contract.
- Adds a standard per-user Windows installer for Radio Vault Client while retaining its pairing, appearance settings and encrypted response cache during upgrades.
- Publishes matching client and server Setup executables alongside portable ZIP recovery packages.

# 0.34.0-alpha13

- Completes the dedicated-server remote native-client path: the normal Radio Vault Client uses the same Library, playback, Research, transcription and administration contracts over secure LAN or loopback connections.
- Selects a newly paired server automatically for the next client launch instead of leaving the client in a misleading local mode.
- Adds automatic certificate-pinned remote health monitoring with live Library counts, encrypted read-only cache status, bounded reconnect backoff and visible retry timing.
- Makes the client Server connection card show live, cached, reconnecting and failed states with traffic-light status colours.
- Adds a standard per-user Windows installer for Radio Vault Server with safe in-place upgrades, Start Menu and optional desktop shortcuts, optional start-at-sign-in, normal uninstall support and preservation of server data outside the installation folder.
- Retains the portable server ZIP as a recovery and testing option.

# 0.34.0-alpha12

- Makes the dedicated Radio Vault Server the native client's only archive database owner, including when both programs run on the same computer.
- Routes archive folders, Archive Health, scans, transcription status, transcription configuration and recommended transcription installation through the active server.
- Removes the native client's local archive database, backup and transcription-worker composition paths; local and remote clients now use the same service boundary.
- Rebuilds Radio Vault Server settings around clear red, amber and green service states, and only shows Start, Stop, transcription installation and cancellation actions when they are relevant.
- Returns the server interface to the standard Radio Vault yellow and neutral palette instead of using transcription teal across every heading.
- Keeps teal as the Transcripts navigation identity while returning the Transcripts workspace itself to the normal application palette.
- Enables auto-hiding scrollbars in both the native client and server settings.

# 0.34.0-alpha11-buildfix2

- Rebuilds Settings > Server connection around the server currently used by the client, including its name, address and whether it is on this computer or another computer.
- Makes **Use this computer** permanently visible and explains that local client/server use requires neither discovery nor pairing.
- Separates optional remote pairing from the active connection and explains that Find Servers searches other computers rather than the server already running locally.
- Relabels Radio Vault Anywhere as a server-hosted service, identifies its host and location, and explains that it continues running when the native client closes.
- Prevents a remotely connected client from accidentally constructing an Anywhere private link from unrelated local-server credentials.

# 0.34.0-alpha11-buildfix1

- Fixes the Settings crash caused when the dedicated-server Anywhere status event completed on a worker thread and raised Avalonia button command changes outside the UI dispatcher.
- Applies the same UI-dispatch boundary to paired-server connection state changes, preventing Test/Reconnect status updates from triggering the equivalent cross-thread failure.
- Adds a packaged-source regression gate and smoke test for both Settings event paths; server protocol, database schema 45, API v1 and capability generation 19 are unchanged.

# 0.34.0-alpha11

- Activates the saved paired Radio Vault Server as the native client startup source, with explicit paired-server and this-computer choices in Settings.
- Routes native Library, Dashboard, Search, Queue, Moments, Research, Transcripts, transcription control, Broadcast Info and deep research packs through the selected server.
- Replaces the last direct-file playback shortcut with an authenticated loopback media bridge backed by the server's canonical multipart manifests and byte-range streams.
- Keeps pairing tokens private inside the native process and preserves certificate pinning while Windows Media Foundation seeks and decodes remote recordings.
- Routes remote archive folders, Archive Health and Library scan status/actions to the selected server instead of the client's dormant local database.
- Keeps successful server reads in a bounded AES-GCM authenticated cache derived from the private pairing relationship, allowing previously opened views to remain read-only during a temporary outage and refreshing them automatically when requests reconnect.
- Preserves transactional native/Anywhere handoff, bounded reconnect retries, server-owned progress and the selected playhead across client changes.
- Advances the native server-client capability contract to generation 19; database schema 45 and API v1 identities remain unchanged.

# 0.34.0-alpha10

- Turns Radio Vault Server into a single-instance background application with a tray menu and settings-only window.
- Removes the active embedded Anywhere host from the native client so it cannot compete for the server ports or overwrite server-owned remote-access settings.
- Closing the settings window now hides it while the archive server, Anywhere service and transcription workers continue running.
- Adds a real current-user Windows sign-in registration, including background launch and the selected authoritative database path.
- Activates secure native-client discovery and one-time six-digit pairing over pinned HTTPS.
- Gives every paired native client its own saved token and certificate identity, with connection test, reconnect and forget controls in the native app.
- Adds server-side trusted-client management with individual and complete revocation controls.
- Advances the remote-client capability contract to generation 18; full remote native Library and playback activation remains the Alpha 11 boundary.

# 0.34.0-alpha9

- Replaces the remaining Radio Vault Anywhere “Soon” destinations with live native-style Moments, Research and Settings workspaces.
- Adds complete Research browsing across overview, records, undated broadcasts, coverage, import history, source summaries and record details.
- Adds transactional deep research pack preview/import/cancellation and export, with transcript counts visible and matching transcripts included in exported packs.
- Adds authoritative date assignment, research review decisions, source/conflict inspection and direct links back to canonical Broadcast Info.
- Adds full Moments browsing, playback, editing and deletion plus server-owned broadcast metadata editing from Broadcast Info.
- Adds server archive, storage, preservation, playback-preference, Library scan, connected-client and parity controls to Anywhere Settings.
- Hardens native-client server requests with bounded reconnect retries for safe reads, progress updates and transactional handoff operations.
- Revalidates server/session ownership when a suspended web client becomes visible and advances the secure Anywhere shell cache to v42.

# 0.34.0-alpha8-buildfix2

- Fixes silent Radio Vault Anywhere playback for users who had never changed the new volume control; an absent saved setting now correctly defaults to 100% instead of being interpreted as zero.
- Keeps an intentionally paused Anywhere player registered as the selected playback output with a lightweight five-second presence heartbeat.
- Prevents the paused player from incorrectly changing to the handover interface after the server's stale-client timeout.
- Retains paused web ownership in the server's controller state and advances the secure Anywhere shell cache to v41.

# 0.34.0-alpha8-buildfix1

- Rebuilds the Radio Vault Anywhere Dashboard around the native client's exact listening-first structure: featured Continue Listening, Surprise Me, four archive statistics, Up Next, On This Day, Recently Added and Unheard Broadcasts.
- Rebuilds the Anywhere desktop player to match the native 330/centre/370 layout with Favourite, Moment, information, speed, more actions and volume controls.
- Keeps the compact phone player while making the full Dashboard responsive at phone widths.
- Fixes a phone handoff race where dormant decoder preparation or decoder priming could leave the commit request carrying a stale playhead.
- Re-measures the target after decoder priming and refreshes the source boundary immediately before commit while preserving the transactional source-stop guarantee.
- Advances the secure Anywhere shell cache to v40 and passes 208/208 smoke tests plus live desktop and phone browser checks.

# 0.34.0-alpha8

- Rebuilds Radio Vault Anywhere around the native app's information architecture before adding further feature areas.
- Replaces the wide desktop header with a 224-pixel navigation rail using the native icon set, signature feature colours and expandable show links.
- Adds native navigation destinations for Dashboard, Search, Library, Favourites, Moments, Transcripts, Now Playing, Research, Downloads, Queue, Archive Health and Settings; future parity areas are clearly marked rather than presented as completed screens.
- Moves Library and Search controls into the page workspace beneath a native-style title and description header.
- Rebuilds the desktop bottom player as a 110-pixel transport bar with artwork, identity, back/forward, play, timeline, information, speed and Now Playing controls.
- Adds an icon-first phone navigation strip and compact phone player while retaining full accessible names for every control.
- Refreshes the shared dark palette, surfaces, borders, cards and responsive spacing to match the Avalonia client more closely.
- Advances the secure Anywhere shell cache to v39 and passes 207/207 smoke tests plus live desktop and phone browser layout checks.

# 0.34.0-alpha7

- Adds a dedicated teal Transcripts destination to Radio Vault Anywhere with a distinct microphone identity matching the native client.
- Adds a responsive native-style transcript library with title/show search, timed speaker segments and playback from any phrase.
- Adds TXT, SRT and VTT transcript export directly from Anywhere.
- Surfaces server transcription readiness, recommended setup installation, individual job monitoring and pause, resume, retry and cancel controls.
- Surfaces batch-run progress, included broadcasts and pause, resume, retry-failed and cancel controls.
- Adds full-broadcast and five-minute transcription actions to Anywhere Broadcast Info while preserving safe confirmation before replacing a transcript.
- Advances the secure Anywhere shell cache to v38 and passes 206/206 smoke tests.

# 0.34.0-alpha6

- Moves Whisper execution, audio preparation, multi-speaker diarization, remembered-voice processing and transcription batches into one long-lived background-server runtime.
- Makes the native Transcripts screen a remote controller for queue, retry, pause, resume, cancel, batch and voice-learning operations.
- Adds one-click installation of the recommended Whisper, VAD and diarization setup to the server settings application.
- Keeps transcription jobs running when the native client closes and recovers interrupted jobs and batches once at server startup.
- Fixes Alpha 5 repeatedly opening repositories and incorrectly marking active transcription jobs as interrupted.
- Passes 205/205 smoke tests with zero build warnings or errors.

# 0.34.0-alpha5

- Routes the native Research workspace, record edits, date review and coverage reads through authenticated full-fidelity server operations.
- Routes transcript browsing, editing, deletion, speaker identity and transcription-job records through the dedicated server.
- Keeps Whisper and diarization execution on the native computer for this transition slice while requiring server authorization for queue, retry, pause, resume and cancel actions.
- Routes Deep Research Pack preview, import, cancellation and export through the server, including transcript counts and transcript payloads.
- Raises the guarded full-client payload ceiling for long transcripts and adds live HTTP coverage for the new service boundary.
- Passes 204/204 smoke tests with zero build warnings or errors.

# 0.34.0-alpha4

- Routes native favourite changes, persistent queue operations and Moment creation, editing and deletion through the dedicated server.
- Routes native durable listening progress, completion state, deliberate seeks and play-count increments through the server while retaining local audio decoding.
- Completes the server Moment contract with identity-preserving edits instead of delete-and-recreate behaviour.
- Keeps Library reads, Broadcast Info and transactional native/Anywhere handoff on the accepted Alpha 3 server boundary.
- Adds source guards preventing the native composition from restoring direct favourite, queue or Moment database writers.
- Passes 203/203 smoke tests with zero build warnings or errors.

# 0.34.0-alpha3

- Moves native Library overview, collection browsing, archive periods, search facets, autocomplete and individual broadcast summaries behind authenticated server APIs.
- Moves native Broadcast Info reads behind the same full-fidelity server contract, including people, topics, catalogue metadata, notes and canonical media structure counts.
- Introduces one certificate-pinned loopback client shared by native Library reads and transactional playback handoff.
- Keeps local audio decoding and write operations transitional so this cutover can be tested independently before the remaining services move.
- Adds a live HTTP contract test and source guards that prevent native Library and Broadcast Info from being wired directly back to SQLite.
- Passes 202/202 smoke tests with zero build warnings or errors.

# 0.34.0-alpha2

- Connects the native Avalonia playback engine to the dedicated Radio Vault Server over authenticated loopback HTTPS.
- Enables the native player’s existing contextual **Move playback to this device** action when Anywhere owns the shared session.
- Publishes native play, pause, seek, speed and playhead heartbeats to the server and observes server ownership every second.
- Uses the existing prepare, align, commit and physical source-stop acknowledgement protocol in both native-to-Anywhere and Anywhere-to-native directions.
- Preserves exact canonical broadcast, position, duration, speed and playing/paused intent across repeated transfers while preventing an older generation from becoming audible.
- Adds deterministic server-session ownership mapping coverage, bringing the suite to 201 tests.
- This is a playback-only native-client bridge; full native read/write cutover remains Phase 3 work.

# 0.34.0-alpha1-buildfix2

- Fixes the Radio Vault Anywhere audio element omitting the versioned `/api/v1` prefix from canonical media-part URLs, which made every browser stream request hit the generic 404 route even though its manifest and file were valid.
- Advances the installed Anywhere shell to generation 12/cache v37 so browsers receive the corrected stream URL builder.
- Adds regression coverage that requires canonical audio sources to use the versioned API route.
- Retains Buildfix 1's media-plan refresh, one-time retry and privacy-safe server diagnostics.
- Retains all 200 dedicated-server and Radio Vault regression tests.

# 0.34.0-alpha1-buildfix1

- Recovers Radio Vault Anywhere playback when a server restart, library refresh or retained browser state leaves the page holding an outdated canonical media plan.
- Re-resolves a temporarily unavailable server media part once before returning a 404, then refreshes the browser manifest and retries with a new cache-busted stream URL.
- Adds precise server-side diagnostics for missing manifests, stale part identities, empty paths and unavailable indexed files without exposing private archive paths.
- Reproduces episode 3662 against the live standalone server: its manifest, media part and byte-range response all resolve successfully after the recovery changes.
- Retains all 200 dedicated-server and Radio Vault regression tests.

# 0.34.0-alpha1

- Adds a separately runnable, settings-only `RadioVault.Server.exe` without Library, Dashboard, Search, Research or player screens.
- Extracts database and HTTP/PWA ownership into a UI-independent `RadioVaultServerRuntime` in shared infrastructure.
- Adds a headless, revision-safe playback-session controller so the server can coordinate client state without owning an audio output device.
- Preserves the existing database path, schema 45, server identity, ports, access token and certificate preferences.
- Keeps native federation disabled until the complete server contract and native-client cutover are ready.
- Adds server isolation, temporary-database startup and stale-playback-revision regression coverage, bringing the suite to 200 tests.
- Keeps the independently packaged Radio Vault 0.33 stable build as the recovery baseline.

# 0.33.0

- Promotes the accepted Alpha 8 Buildfix 3 implementation to the stable Radio Vault 0.33 transcription baseline.
- Includes local full-broadcast and sample transcription, multi-speaker diarisation, remembered-voice review, safe re-transcription and durable batch processing.
- Includes transcript-aware Search, transcript access from broadcast surfaces and complete transcript content in Deep Research Packs.
- Retains the long-form continuity, deterministic timestamp mapping, pathological-output filtering and implausible-speaker safeguards proven against the affected archival recording.
- Retains the redesigned Transcript review, Transcription activity and Batch runs workspaces with unclipped, readable controls.
- Passes the complete 199-test release suite and the Avalonia-only architecture gate without compiler warnings or errors.
- Records the dedicated background-server and universal-client architecture as the authoritative roadmap without introducing that runtime into the stable 0.33 package.

# 0.33.0-alpha8-buildfix3

- Redesigns Transcripts into focused **Transcript review**, **Transcription activity** and **Batch runs** workspaces.
- Promotes the current-broadcast transcription action and explains how playback determines which broadcast will be transcribed.
- Replaces ambiguous export and playback controls with complete labels and clearer action grouping.
- Fixes clipped text buttons by reserving the shared 30-pixel compact style for icon-only controls.
- Lets phrase-review, job and batch actions wrap without losing their labels at laptop window sizes.
- Retains every Buildfix 2 long-form continuity, timestamp-mapping, safe replacement and diarization safeguard.

# 0.33.0-alpha8-buildfix2

- Prevents VAD from removing long sections of genuine speech from full-broadcast and batch transcription; VAD remains available for bounded samples up to 30 minutes.
- Maps timestamps from prepared M4A/M4B/AAC/WMA/MP4 samples back onto the original broadcast timeline deterministically.
- Detects pathological zero-duration and heavily repeated Whisper output and records it as unclear audio instead of presenting hallucinated wording as speech.
- Calibrates the hidden diarization clustering default from 0.5 to 0.9 against the affected archival recording and migrates existing default settings in memory.
- Rejects implausible speaker-analysis output rather than saving hundreds of invented speaker identities.
- Adds an explicit **Re-transcribe full broadcast** action; the existing transcript remains available unless and until the replacement completes successfully.
- Adds long-form continuity, timestamp, speaker-sanity and hallucination regression coverage, bringing the suite to 199 tests.

# 0.33.0-alpha8-buildfix1

- Makes Deep Research Pack creation explicit and removes the overlapping second Research export button.
- Confirms pack transcript coverage in the interface: export and import summaries now report the transcript count.
- Keeps `transcripts.json` mandatory in every newly created pack and preserves timed text plus diarized speaker labels through round trips.
- Fixes the Research workspace failing to refresh after an import because its own busy guard suppressed the reload.
- Removes synchronous UI-thread waiting from theme changes, avoiding a potential startup or settings deadlock.
- Corrects the Deep Research Pack instructions heading and duplicated step numbering.
- Retains the complete Alpha 8 batch-transcription feature set and passes the full release gate.

# 0.33.0-alpha8

- Adds a batch builder for a whole show, year, exact date range or the complete archive.
- Previews every selected broadcast and records existing transcripts as skipped instead of creating duplicate work.
- Persists batches and their item order, saved transcription settings, active job linkage and per-broadcast outcomes in SQLite.
- Adds batch-wide pause, resume and cancellation while preserving every completed transcript.
- Adds pending-item reordering, failed-only retry, aggregate progress, completed/remaining/skipped/failed counts and an estimated time remaining.
- Recovers active batches as Interrupted after an app restart and resumes safely from the next unfinished broadcast on request.
- Keeps the Alpha 7 transcript correction, speaker memory, local search and TXT/SRT/VTT export workflows unchanged.
- Expands the smoke suite to 198 passing tests with zero build warnings or errors.

# 0.33.0-alpha7

- Adds durable phrase correction, reviewed/needs-attention state, automatic midpoint splitting and safe same-speaker merging.
- Adds transcript-local search with previous/next match navigation and playback from the selected result.
- Lets people be assigned to an entire diarized speaker cluster from archive metadata, researched people or a typed name.
- Turns the installed sherpa-onnx embedding model into a local remembered-voices engine; confirmations build reusable profiles and high-confidence later matches remain suggestions until confirmed.
- Adds one-click readable TXT, SubRip SRT and WebVTT exports with timestamps and confirmed speaker names.
- Keeps edits revisioned in SQLite and preserves corrected transcript text and speaker assignments in Deep Research Packs.
- Passes the complete 197-test smoke suite with zero build warnings or errors.

# 0.33.0-alpha6

- Adds transcript status and actions directly to Full Broadcast Information and Now Playing.
- Opens an existing transcript with the correct broadcast already selected; broadcasts without one can queue a full local transcription in place.
- Prevents duplicate queued or running transcription jobs for the same broadcast across every start surface.
- Adds advanced Search scope chips for titles and summaries, people, topics, Research records and transcript speech.
- Adds listening-status chips, show and year facets, and a transcript-availability filter that can work with or without text.
- Adds archive-aware autocomplete for shows, broadcast titles, known people and topics.
- Keeps relevance ordering and match excerpts while applying combined facets.
- Passes the complete 196-test release suite with zero build warnings or errors.

# 0.33.0-alpha5

- Includes all available full text, timed segments and anonymous speaker labels in Deep Research Packs through a new `transcripts.json` entry.
- Expands Search across people, topics, Research records and transcript speech, sorts results by relevance and explains transcript or metadata matches inline.
- Adds **Transcribe broadcast** to broadcast right-click menus in Library and Search; playback is no longer required to start these jobs.
- Adds genuine pause/resume controls for the active native Whisper process while preserving its in-memory work, alongside the existing safe cancellation and retry paths.
- Persists paused job state and safely resumes a paused worker before cancellation.
- Advances the Deep Research Pack schema to version 6 and expands the smoke suite to 196 passing tests.

# 0.33.0-alpha4

- Replaces the unfinished hard-coded purple startup window with a theme-aware launch experience using Radio Vault's current surfaces, broadcast mast, typography and palette.
- Gives Transcripts a distinct microphone icon and turquoise signature colour across the sidebar and workspace.
- Makes the no-current-broadcast state more prominent in the Transcripts header.
- Automatically converts the selected range of M4A, M4B, AAC, WMA and MP4 audio to a temporary 16 kHz mono WAV before Whisper runs, without altering archive files.
- Improves failed job summaries so the visible job row reports the first useful error instead of only “Transcription failed”.
- Verifies the M4A repair end to end against the broadcast that exposed the problem.

# 0.33.0-alpha3

- Replaces the two-speaker TinyDiarize experiment with automatic multi-speaker diarization powered locally by sherpa-onnx.
- Adds verified automatic downloads for the Pyannote segmentation and NeMo speaker-embedding ONNX models.
- Runs speech recognition and speaker clustering as separate stages, then maps anonymous speaker labels onto word-timed phrases.
- Supports an unknown number of speakers by default and records the diarization engine, models and discovered speaker count in transcript metadata.
- Removes TinyDiarize from the active settings, model catalogue and transcription worker path.
- Expands the smoke suite to 195 passing tests.

# 0.33.0-alpha2-buildfix1

- Fixes the TinyDiarize model download 404 by following whisper.cpp's official special-case source for `-tdrz` models.
- Automatically clears and disables TinyDiarize when the configured model is not compatible, preventing standard models from failing immediately.
- Preserves the successful local transcription workspace, official worker/VAD setup and all Alpha 2 behaviour.

# 0.33.0-alpha2

- Activates Radio Vault's existing platform-neutral transcription engine, SQLite repository and durable job coordinator in the Avalonia application.
- Adds a first-class Transcripts workspace for full-broadcast jobs and five-minute samples from the current playback position.
- Shows transcript summaries, timed phrases, recent job state and progress, and supports playback from a selected timestamp.
- Adds cancel and retry actions; jobs left queued or running by an earlier shutdown reopen as Interrupted and remain retryable.
- Makes Settings update the live Whisper engine immediately through one atomic settings store.
- Adds guided automatic setup for the latest stable official Windows x64 worker, the selected official speech model and official Silero VAD model, while retaining manual path selection.
- Resolves the latest worker through the official GitHub release API, verifies its published SHA-256 digest, blocks unsafe archive paths and writes model downloads atomically.
- Keeps transcription local; no cloud audio upload is introduced.
- Expands the smoke suite from 190 to 194 passing tests.

# 0.33.0-alpha1

- Moves the remaining platform-neutral models and infrastructure out of the retired WPF-era `TheRadioVault` source folder into a dedicated `TheRadioVault.Infrastructure` project.
- Replaces Avalonia and test-project linked source files with normal project references.
- Removes the last retired shell directory, duplicate icon and unused legacy app-icon asset.
- Preserves existing namespaces and runtime behaviour while giving database, scanning, Research Pack, preservation, backup and Radio Vault Anywhere code an explicit UI-independent home.
- Adds source validation for the new project boundary and keeps the full 190-test suite passing with zero build warnings or errors.
- Establishes the clean foundation for the next 0.33 phase: transcription.

# 0.32.0

- Promotes the successfully built, fully release-gated and user-accepted RC1 implementation unchanged to the stable 0.32 release.
- Introduces no runtime feature, database-schema, Research Pack, browser API or playback-contract change from RC1.

- Removes the retired WPF desktop project, its WPF/Windows adapter project, WPF presentation sources and obsolete WPF validation tools; Avalonia is now the only application shell.
- Keeps the platform-neutral local archive, database, Research Pack, scanner, preservation and Radio Vault Anywhere sources used by Avalonia.
- Fixes short recordings being marked complete merely because they fall inside the five-minute completion window; the final-window rule now also requires at least 80 percent progress.
- Normalises hyphenated `Part6-of-6` filename families so annotated multipart recordings stay together.
- Updates stale database, playback-transfer, server-capability and Library Truth smoke fixtures to the current guarded contracts.
- Adds real Research Pack export/import round-trip coverage for all date-review decisions and proves date decisions persist across fresh workspace-service instances.
- Promotes the suite to 190 passing smoke tests and adds Avalonia-only release validation.

# 0.32.0-alpha13-library-dashboard-pass3-buildfix2-buildfix6-quick-date-approval

- Replaces the single date-review list with persistent Active, Ignored and Completed queues.
- Adds the fast primary workflow: Approve, Keep existing and Ignore, with automatic movement to the next decision.
- Adds keyboard shortcuts (`A`, `K`, `I`, and `Ctrl+Z`) and a one-click undo that returns the last decision to Active.
- Keeps ignored suggestions recoverable and leaves recording-only, release/archive-only and undated treatments under More date choices.
- Preserves `kept_existing` and `ignored` decisions through Research Pack export/import without clearing trusted Library dates.
- Adds a database-backed regression for Ignore, Keep existing and reopening while retaining the prior Library date.

# 0.32.0-alpha13-library-dashboard-pass3-buildfix2-buildfix5-live-date-projection

- Fixes approved and manually assigned Research dates not appearing in the visible canonical Library when a newer completed Library Truth analysis exists but has not been adopted.
- Writes authoritative dates to both the active verified/adopted Library projection and the newest completed analysis, while keeping historical projections untouched.
- Updates canonical broadcasts, every mapped episode, linked Research records and the affected Library Truth file rows in one guarded transaction.
- Applies the same projection rule when reopening a decision, keeping recording/release-only evidence, or deliberately returning an item to Undated.
- Adds a database-backed regression proving the Library immediately reads the approved date from the active adopted projection.
- Restores two missing test-runner helpers from the supplied source package so its regression executable compiles locally.

# 0.32.0-alpha13-library-dashboard-pass3-buildfix2-buildfix4-all-show-date-review

- Extends guarded Research date review to all six first-class shows: Ron & Fez, Bennington, Opie & Anthony, The Ron & Ron Show, Unmasked and Ron Bennington Interviews.
- Surfaces missing dates, uncertain confidence, conflicting research dates, recording/release-role conflicts and earlier automatically adopted Research dates without inventing chronology.
- Preserves the same approve, custom-date, recording-only, release-only, leave-undated and reopen actions for every show.
- Keeps settled High, Confirmed and Manual dates out of the queue when all evidence agrees.
- Generalises schema-5 research-pack date decisions and guarded import behaviour to every first-class show while retaining catalogue-specific caution for RBI, Unmasked and Ron & Ron.
- Includes the preceding date-review SQL and TrvPack model compile repairs.

# 0.32.0-alpha13-library-dashboard-pass3-buildfix2-buildfix3-date-review-model-repair

- Fixes the Avalonia compile failure in `DatabaseService.cs` by reading preserved catalogue metadata from `TrvPackBroadcast.Research.Catalogue`, matching the actual research-pack model.
- Retains the complete catalogue date-review workflow and the safer non-interpolated date-review SQL introduced by the previous buildfix.

# 0.32.0-alpha13-library-dashboard-pass3-buildfix2-buildfix2-date-review-compile-repair

- Fixes the date-review build failure in `ResearchWorkspaceService.cs` by replacing the interpolated raw SQL string with a plain raw SQL template and an explicit predicate placeholder, so the literal empty JSON object cannot be parsed as C# interpolation.
- Retains the complete Buildfix 2 catalogue date-review queue, guarded decisions, schema-5 research-pack round-trip, Pass 3 Library changes, local playback and Radio Vault Anywhere.

# 0.32.0-alpha13-library-dashboard-pass3-buildfix2-date-review

- Adds a dedicated **Date review** workspace for Ron Bennington Interviews, Unmasked and The Ron & Ron Show.
- Surfaces exact and partial research dates as explicit decisions instead of silently leaving them outside the Library or automatically committing uncertain chronology.
- Shows the proposed date, date type, confidence, source count, provenance and same-day collision warnings.
- Adds guarded actions to approve a Library date, keep the date as recording evidence, keep it as release/archive evidence, leave the item undated, or reopen a previous decision.
- Marks imported and existing unresolved catalogue dates as Needs attention.
- Includes older research rows that only carry a top-level date, plus filename/title year and month clues, so legacy RBI and Unmasked packs are not invisible to the review workflow.
- Shows the current Library date beside each candidate and warns when approval will replace it.
- Advances research-pack schema to 5 so date-review decisions, previous state and provenance round-trip through export/import.
- Stops unapproved catalogue dates from silently changing Library chronology; previously auto-adopted Research dates can be returned to undated when rejected.
- Preserves unrelated conflict/reconciliation review state when a catalogue-date decision is resolved.
- Preserves Pass 3 mini-panel simplification, Hide Completed, local playback, sidebar activity and Radio Vault Anywhere.

# 0.32.0-alpha13-library-dashboard-pass3-buildfix1

- Fixes the two Pass 3 research-pack compile failures by adding the intended three-value `FirstNonEmpty` fallback overload.
- Removes the unused local-startup variable warning while preserving the accepted `--local` argument.
- Retains all Pass 3 Library mini-panel, catalogue date, Hide Completed, local playback and Radio Vault Anywhere behaviour.

# 0.32.0-alpha13-library-dashboard-pass3

- Simplifies the Library mini information panel for RBI, Unmasked and The Ron & Ron Show to the same summary, people and topics layout used by ordinary shows. Deep catalogue and provenance fields remain in Research, Now Playing and Full Broadcast Information.
- Adds non-invented catalogue date handling: exact full dates become canonical dates, while month/year and year-only clues remain partial research metadata.
- Makes schema-4 research imports apply exact RBI, Unmasked and Ron & Ron dates to matched Library, canonical and Library Truth records without overwriting an existing local date unless the pack is authoritative.
- Makes Hide Completed work consistently in List, Year and Month views.
- Preserves local playback, live progress, Radio Vault Anywhere and the post-1.0 native federation boundary.

## 0.32.0 Alpha 13 — Sidebar activity status

- Replaces the full-width page loading bars with a compact activity panel at the bottom of the sidebar.
- Shows plain-English task names and live status text for Library scanning, Research import/export, metadata saves, backups, archive health, Radio Vault Anywhere changes, playback preparation, Library loading and other busy views.
- Polls the active Library scan so filename discovery and canonical-promotion phase messages update in the sidebar while the scan is still running.
- Keeps the sidebar activity visible while navigating to another page, so long-running work remains understandable without taking over the main content area.
- Preserves compact progress indicators inside Research action buttons and the safe-closing overlay.
- Retains the accepted faster local playback, Radio Vault Anywhere, six-show support and catalogue Research fields.

## 0.32.0 Alpha 13 — Local UX and parity pass 1

- Establishes the accepted local-first plus Radio Vault Anywhere baseline as the start of Alpha 13.
- Removes the dormant Connected Access view model and service from the active Avalonia composition and startup path.
- Removes visible handoff/device-state remnants from the persistent player while leaving the frozen post-1.0 source available for reference.
- Rewords Settings, startup and Research text around the local archive rather than an authoritative server/client model.
- Simplifies Dashboard continuation cards by keeping their primary playback action prominent and moving favourite management back to Library and Now Playing.
- Preserves local playback, Radio Vault Anywhere, all six show types, catalogue-style import and research-pack support.

## 0.32.0 Alpha 12 Local Playback Reset 3 — Radio Vault Anywhere restoration

- Restores the established embedded Radio Vault Anywhere web server and browser/PWA interface on top of the accepted local Avalonia archive.
- Adds a dedicated Settings section for hosting, private links, HTTPS setup, certificate reset and browser-only diagnostics.
- Preserves browser Library access, audio streaming and web write-through through the existing web application and local database provider.
- Forces native LAN federation off and withholds saved desktop-pairing credentials from the running server.
- Keeps Connected Access, Avalonia remote caching, desktop-to-desktop streaming, device ownership and handoff detached until after Radio Vault 1.0.
- Retains all accepted show expansion, catalogue-style parsing and canonical show-projection repairs.

## 0.32.0 Alpha 12 Local Playback Reset 2 Buildfix 4 — Show projection repair

- Refreshes sidebar show sections immediately after a completed local Library scan.
- Counts Library content by canonical show identity rather than raw collection IDs, so legacy names such as `Opie and Anthony` cannot hide broadcasts from **Opie & Anthony**.
- Shows only collections that actually contain Library or Research content in the Research workspace.
- Makes Research records, undated broadcasts, coverage and whole-show research-pack export include every collection row belonging to the same canonical show.
- Preserves canonical names in the UI and export manifest while retaining compatibility with older databases and aliases.
- Keeps the local-only 1.0 direction intact; native client/server and handoff remain detached.

## 0.32.0 Alpha 12 Local Playback Reset 2 Buildfix 3 — Catalogue-style import

- Hides empty show sections from the sidebar and shows them as soon as canonical content exists.
- Imports every available undated item assigned to The Ron & Ron Show, Unmasked or Ron Bennington Interviews as its own file-backed canonical broadcast.
- Parses guest/topic catalogue filenames, uses the explicit folder assignment while parsing, and preserves year-only clues in the headline.
- Opens catalogue-style shows in complete List view so undated items are not hidden by the Year/Month grid.
- Includes scanned library episodes without existing Research rows in whole-show research-pack exports, with the original filename retained in Archive Notes.

# v0.32.0 Alpha 12 Local Playback Reset 2 Buildfix 2 — Sidebar Show Sections

- Makes all six first-class shows permanent Library destinations in the Avalonia sidebar, including shows with zero broadcasts before their first scan.
- Adds **The Ron & Ron Show**, **Ron Bennington Interviews**, and **Unmasked** alongside the existing Ron & Fez, Bennington, and Opie & Anthony sections.
- Uses the current Library counts when navigation loads, while preserving additional/custom collections only when they contain broadcasts.
- Keeps **Unsorted** hidden while empty and surfaces it automatically when unresolved recordings exist.
- Adds a regression test proving a newly initialised schema-45 Library exposes all six empty first-class show sections.
- Preserves explicit folder show assignment, local-only playback, and the post-1.0 freeze on native client/server and handoff work.

# v0.32.0 Alpha 12 Local Playback Reset 2 Buildfix 1 — Folder Show Assignment

- Restores the explicit show chooser after selecting a new local Library folder.
- Offers Auto-detect / mixed-show folder plus all six first-class show identities.
- Suggests a matching show when the selected folder name clearly identifies it, while still requiring confirmation.
- Saves the chosen collection assignment on the folder registration and displays it in Settings.
- Cancelling the chooser leaves the folder unregistered.
- Keeps the application local-only; native client/server and handoff remain detached.

# v0.32.0 Alpha 12 Local Playback Reset 2 — Show Expansion

- Adds **The Ron & Ron Show**, **Unmasked**, and **Ron Bennington Interviews** as first-class canonical show types.
- Seeds the new shows and aliases into existing schema-45 databases on normal startup; no destructive migration is required.
- Extends filename parsing, headline cleanup, Library Truth detection and US archive-date handling for all three shows.
- Makes all three shows available in the Research workspace show selector, including whole-show/year export and local `.trvpack` import matching.
- Keeps the Avalonia application local-only. Native desktop client/server, cache synchronisation and handoff remain detached.
- Radio Vault Anywhere restoration remains a separate next step so this small feature build does not reintroduce federation risk.

# v0.32.0 Alpha 12 Local Playback Reset 1

- Forces the Avalonia application into a local Library session on every launch.
- Detaches the desktop project from `TheRadioVault.Web` and all linked LAN federation/server source files.
- Removes Connected Access, Radio Vault Anywhere and connected-playback diagnostics from the active Settings UI.
- Removes connection state and handoff controls from the shell and Now Playing UI.
- Keeps ordinary playback on the local NAudio engine with direct local SQLite persistence.
- Replaces Web-server-backed Library maintenance with a direct local scanner.
- Ignores old pairing and remote-startup preferences; `--remote` now fails with a clear local-only message.
- Database schema remains 45. No database migration is performed.

# 0.32.0-alpha12-playback-baseline-recovery1

- Establishes a deliberately narrow playback-recovery build from the last Buildfix 5 source known to compile on the user's Windows machines, while preserving the accepted Alpha 12 interface and research work.
- Disables desktop transactional handoff controls and transfer operations until ordinary server and laptop playback pass a separate acceptance cycle.
- Keeps remote playback ownership reporting only as the minimum server authorisation boundary required for durable listening-progress writes.
- Opens returning remote clients immediately from their encrypted cache and retries Library synchronisation in the background.
- Allows remote media resolution and progress persistence while Library metadata is temporarily cached/read-only.
- Routes media-manifest and progress requests through the isolated playback connection pool; audio continues through the isolated media pool.
- Makes connected diagnostics explicitly skip transactional handoff in this recovery build.
- Preserves database schema 45, LAN capability generation 16, web-shell generation 11, Radio Vault Anywhere shell cache v36 and all API/pairing identities.

# 0.32.0-alpha12-buildfix5-playback-connectivity-diagnostics

- Separates ordinary Play from transactional handoff so local server audio and remote-client streams open immediately; shared ownership is published after the decoder starts and cannot cancel working audio.
- Adds dedicated bounded TLS pools for Library synchronisation, media streaming, playback/session traffic and maintenance operations.
- Expires abandoned remote playback owners after a missed-heartbeat lease while preserving generation-bound progress safety.
- Makes Dashboard On This Day detail enrichment asynchronous so the global loading bar clears with the primary overview.
- Adds Settings → Advanced connected-playback diagnostics on server and client, including quick/stress tests, isolated muted decoder checks, safe handoff prepare/ready/cancel, progress-integrity verification, shared session codes and privacy-safe `.trvdiag` export.
- Records connection transitions, Library sync timing, media resolution, decoder startup and ownership publication in a bounded runtime journal.
- Advances LAN capability generation to 16 and Radio Vault Anywhere shell cache to v36; database schema remains 45 and API v1 identities are unchanged.

# 0.32.0-alpha12-buildfix4-buildfix2-playback-start-connectivity

- Fixes explicit Play on server, laptop and phone inheriting a stale shared paused state and completing as silent playback.
- Creates a transactional handoff only when another active device actually owns the session.
- Serialises Avalonia handoff refreshes and slows normal session polling to one second.
- Reuses a bounded certificate-pinned `SocketsHttpHandler` pool so rapid Connected Access requests no longer churn TLS connections and force the laptop into cached mode.
- Verifies that the committed desktop target playback engine actually enters Playing after volume is restored.
- Makes Radio Vault Anywhere Play/Move gestures explicitly request playback and verifies audible Safari output after commit.
- Serialises normal phone player/change polling and reports canonical multipart progress on page exit.
- Advances the Radio Vault Anywhere shell cache to v35 while preserving schema 45, capability generation 15, API v1 and desktop web-shell generation 11.

# 0.32.0-alpha12-buildfix4-buildfix1-avalonia-provider-link

- Buildfix 1: links `WebArchiveProvider.PlaybackTransfers.cs` into the Avalonia desktop project so the concrete provider implements all five transactional handoff interface members during the default-shell build.
- Adds a source-validation guard that verifies every transactional provider partial required by Avalonia is explicitly linked.
- No handoff protocol or runtime behaviour changes from Buildfix 4.

## Alpha 12 Buildfix 4 — Transactional handoff recovery

- Replaces the Avalonia-era claim-before-load handoff paths with a shared prepare–verify–commit transaction across the server, remote desktop client and Radio Vault Anywhere.
- Keeps the source output authoritative and playing while the target opens canonical media, seeks to a protected projected playhead, and proves its decoder can run muted.
- Persists a monotonic durable progress boundary before ownership changes; failed preparation, failed seeks and startup-zero browser positions cannot erase listening progress.
- Separates one-second live playback heartbeats from durable five-second/event-bound progress writes.
- Adds generation-bound source-stop receipts: the outgoing physical decoder pauses and acknowledges before the new target unmutes, with a bounded fallback for sleeping/offline devices.
- Serializes transfer preparation, makes begin/commit retries idempotent after lost responses, rejects stale tickets/generations/acknowledgements, and re-checks ownership at the exact unmute boundary so rapid successive handoffs cannot create competing outputs.
- Migrates Radio Vault Anywhere live playback to canonical media manifests and multipart part switching rather than the legacy single-file audio route.
- Adds bounded request/media preparation waits, visible transaction status, safe failure rollback, and post-commit recovery that never mistakes a presentation error for a failed ownership commit.
- Adds behavioural coverage for all six handoff directions, startup-zero rejection, source mutation, cancellation, single-flight preparation, durable-progress isolation, physical source-stop acknowledgement and superseding generations.
- Advances LAN capability generation to 15, Radio Vault Anywhere shell cache to v34 and desktop web-shell generation to 11. Database schema remains 45.

# 0.32.0-alpha12-buildfix3-buildfix2-handoff-takeover-loading-feedback

## Alpha 12 Buildfix 3 Buildfix 2 — Reliable takeover and loading feedback

- Replaces the Avalonia server's presentation-flag handoff pause with an explicit playback-engine release path, matching the proven WPF behaviour.
- Reserves a laptop/phone takeover before remote media opening and extends the pending ownership lease to 45 seconds, preventing slow stream startup from expiring the claim.
- Preserves the source play/pause state and projected playhead, confirms the new owner after the first heartbeat, and shows a clear error if the server does not accept the move.
- Rejects non-conflict claim responses that did not actually change server state instead of silently continuing.
- Adds an animated activity glyph to the contextual Play/Pause/Move button throughout playback preparation and handoff.
- Adds visible indeterminate progress and working labels during Research pack preview and import, yielding a UI render turn before local backup/parsing work begins.
- Preserves all prior Alpha 12 functionality. Database schema remains 45, LAN capability generation remains 14, and API v1 identities are unchanged.

# 0.32.0-alpha12-buildfix3-buildfix1-avalonia-handoff-icon-xaml

## Alpha 12 Buildfix 3 Buildfix 1 — Avalonia handoff icon compile repair

- Replaces the WPF-only `StrokeLineJoin` property on the new persistent-player handoff icon with Avalonia's supported `StrokeJoin` property, resolving `AVLN2000` in `MainWindow.axaml`.
- Adds a targeted validation guard so future Avalonia handoff icon changes cannot reintroduce WPF-only stroke-property names.
- Normalizes Research import-change strings before provenance recording, removing the four nullable warnings reported by the same Windows build.
- Preserves all Alpha 12 Buildfix 3 contextual handoff and tight-playhead behaviour. Database schema remains 45, LAN capability generation remains 14, and API v1 identities are unchanged.

# 0.32.0-alpha12-buildfix3-contextual-handoff-tight-playhead

## Alpha 12 Buildfix 3 — Contextual handoff and tight shared playhead

- Replaces the separate Continue on this device action with the accepted WPF-style arrow-into-desktop icon in the centre Play/Pause position whenever another endpoint owns playback.
- Keeps the primary transport control enabled while remote media is resolving or buffering and records immediate pause/resume intent instead of leaving the button greyed out.
- Preserves the source endpoint's playing or paused state when playback moves to the laptop or desktop.
- Reduces Avalonia ownership polling and authoritative playback heartbeats to one-second intervals.
- Adds timestamp-based shared-position projection every 250 ms so inactive endpoints display a smooth, tightly aligned playhead between server heartbeats without increasing durable database writes.
- Uses the projected server position at the instant of transfer, reducing the normal handoff gap to approximately the network/decoder startup delay rather than a full heartbeat interval.
- Tightens phone heartbeat and remote-session rendering intervals to match the desktop convergence model.
- Keeps skip, seek, stop and speed controls locked to the owning endpoint while leaving the contextual move control available.
- Preserves all Alpha 12, Buildfix 1 and Buildfix 2 functionality. Database schema remains 45, LAN capability generation remains 14, and API v1 identities are unchanged.

# 0.32.0-alpha12-buildfix2-live-progress-consistency

## Alpha 12 Buildfix 2 — Live progress consistency

- Adds one authoritative live-progress presentation model sourced from the local decoder while this endpoint owns playback and from the shared server session while playback belongs to another phone, desktop or server endpoint.
- Updates Dashboard Continue Listening percentages, featured ordering and membership immediately as playback starts, advances, completes or moves between devices.
- Keeps Dashboard completed and in-progress totals synchronized with the active broadcast rather than waiting for the next persisted overview snapshot.
- Updates visible Library rows with live logical position and duration, including multipart broadcasts and canonical aliases, and moves rows into or out of Continue Listening, Unplayed, Completed and Hide completed views as their state changes.
- Forces fresh Dashboard, Library and Search snapshots when revisiting those destinations, then overlays any newer live playback state.
- Makes completed broadcasts display exactly 100% and prevents incomplete broadcasts from rounding up to 100%.
- Preserves all accepted Alpha 12 Research/interface work and Alpha 12 Buildfix 1 handoff, remote Archive Health and library-discovery repairs. Database schema remains 45, LAN capability generation remains 14, and API v1 identities are unchanged.

# 0.32.0-alpha12-buildfix1-handoff-health-library-sync

## Alpha 12 Buildfix 1 — Handoff, Archive Health and Library synchronization repair

- Repairs Avalonia-hosted playback handoff by accepting desktop claim commands and replacing the server shell's null handoff adapter with the authoritative shared-session service.
- Adds an always-visible Shared playback panel to Now Playing, listing known phone, laptop and server endpoints with active ownership plus Continue on this device and Move playback to server controls.
- Publishes live Avalonia playback heartbeats every two seconds, including logical multipart position, duration, speed and play/pause state, and completes pending ownership immediately after media opens.
- Prevents a newly claimed desktop from pausing itself against a stale previous-owner snapshot and pauses abandoned local output if a replacement handoff fails.
- Makes connected-client Archive Health use the server report and server analysis path rather than an empty client-local report.
- Adds manual server Library scanning from both local and connected Settings, with live status and result counts.
- Adds debounced server folder watching plus an hourly safety scan, then publishes a full library revision so connected Library, Dashboard and Search caches discover additions automatically.
- Preserves all accepted Alpha 12 Research and interface changes. Database schema remains 45, LAN capability generation remains 14, and API v1 identities are unchanged.

# 0.32.0-alpha12-research-coverage-undated-broadcasts-polish

## Alpha 12 — Research coverage, undated broadcasts and interface polish

- Keeps the Now Playing Up Next queue visible in the idle state and retains a dedicated nothing-playing panel beside it.
- Removes favourite actions from Dashboard On This Day, Recently Added and Unheard Broadcasts cards.
- Replaces generic missing-artwork music-note and text placeholders with one shared Radio Vault radio-mast glyph across Dashboard, Library, Now Playing, the mini-player, Broadcast Info and Research.
- Adds a compact Library list-view checkmark filter for hiding completed broadcasts; Clear Filters resets it and result text reports how many completed items were hidden.
- Adds an Undated Broadcasts Research mode for canonical audio whose date is absent, unknown or ambiguous, including parser candidates, evidence and warnings.
- Adds protected manual date assignment across canonical aliases, linked Research records, Library Truth rows and canonical broadcasts. Later filename scans do not overwrite a Manual date.
- Allows a manually confirmed formerly unmapped recording to enter its dated Library position immediately through the guarded canonical promoter.
- Adds a whole-show calendar heatmap with per-day audio presence, known-missing state and core metadata coverage for headline, summary, people, topics, sources, transcript and artwork.
- Extends authenticated federation routes and Avalonia remote services so undated browsing, manual date assignment and heatmap coverage remain server authoritative.
- Adds route-contract coverage to the console test harness.
- Preserves database schema 45, LAN capability generation 14, API v1 and all accepted Alpha 11 playback/Settings/Anywhere compatibility behaviour.

# 0.32.0-alpha11-buildfix4-web-playback-nullable-episode-id

## Alpha 11 Buildfix 4 — Nullable web-playback episode identity

- Fixes the remaining Alpha 11 compile failure in `AvaloniaWebPlaybackController.CreateState`, where C# could not infer a common type between the non-nullable `long` broadcast identifier and `null`.
- Declares the local episode identifier explicitly as `long?`, matching `WebPlaybackState.EpisodeId`.
- Leaves Settings, Radio Vault Anywhere, playback persistence, the v0.31 server compatibility fallback and all accepted Avalonia UI behaviour unchanged.
- Database schema remains 45 and LAN capability generation remains 14.

# 0.32.0-alpha11-buildfix3-filename-parser-namespace-boundary

## Alpha 11 Buildfix 3 — Filename-parser namespace boundary

- Fixes the remaining Alpha 11 compile failure where the linked `LibraryScannerService` could not resolve the platform-neutral `FilenameParserService` type.
- Extends the Avalonia-only linked-service global-using boundary with `TheRadioVault.Core.Services`, matching the non-WPF namespace import inherited by the working WPF project.
- Keeps the boundary free of `System.Windows`, `Microsoft.Win32` and Windows-platform namespaces.
- Leaves Settings, Radio Vault Anywhere, playback persistence, the v0.31 server compatibility fallback and all accepted Alpha 10 UI behaviour unchanged.
- Database schema remains 45 and LAN capability generation remains 14.

# 0.32.0-alpha11-buildfix2-linked-service-global-usings

## Alpha 11 Buildfix 2 — Linked-service global usings

- Fixes the remaining Alpha 11 compile failure where linked legacy database partials could not resolve plain model types such as `ScanHistoryItem`, `LibraryHealthSummary`, `MomentItem`, `QueueItem` and the core `ParsedFilename` model.
- Adds an Avalonia-only global-using boundary for `TheRadioVault.Models` and `TheRadioVault.Core.Models`, matching the non-WPF portion of the imports those service partials historically received from the WPF shell.
- Does not import `System.Windows` or any other WPF namespace into the Avalonia project.
- Preserves the Alpha 11 Settings, Radio Vault Anywhere and accepted v0.31-server playback-progress compatibility behaviour.
- Database schema remains 45 and LAN capability generation remains 14.

# 0.32.0-alpha11-buildfix1-linked-legacy-models

## Alpha 11 Buildfix 1 — Linked legacy model boundary

- Fixes the Alpha 11 compile failure where legacy database and Radio Vault Anywhere service files were linked into the Avalonia executable without the plain C# model files they require.
- Links Library scan, personal-state migration, preservation, Research import-preview, Research library and Search/Explore models into the same Avalonia compilation.
- Adds validation so every legacy model dependency required by the linked service graph must remain present.
- Leaves the Alpha 11 Settings, Radio Vault Anywhere and accepted v0.31-server playback-progress compatibility behaviour unchanged.
- Database schema remains 45 and LAN capability generation remains 14.

# 0.32.0-alpha11-settings-anywhere-parity

## Alpha 11 — Settings and Radio Vault Anywhere parity

- Rebuilds Avalonia Settings as a seven-section control centre: Archive, Playback, Appearance, Connected Access, Radio Vault Anywhere, Transcription and Advanced.
- Restores local Archive Health, Library-folder registration, guarded backup creation/restoration and playback-preference controls.
- Expands Connected Access while retaining local/server ownership boundaries.
- Adds authoritative Radio Vault Anywhere hosting controls for HTTP/HTTPS, private links, secure setup, desktop pairing, paired-client revocation, certificates and diagnostics.
- Presents server-owned guidance instead of unsafe hosting controls on connected clients.
- Preserves the accepted Alpha 10 Buildfix 15 Buildfix 2 playback progress compatibility path for v0.31 servers.
- Database schema remains 45 and LAN capability generation remains 14.

# 0.32.0-alpha10-buildfix15-buildfix2-presentation-log-boundary

## Alpha 10 Buildfix 15 Buildfix 2 — Presentation logging boundary

- Fixes the second Buildfix 15 compile failure by removing the invalid dependency from `TheRadioVault.Presentation` on the WPF shell-only `DiagnosticLog` type.
- Records the non-fatal handoff compatibility diagnostic through `System.Diagnostics.Trace`, which is available within the Presentation project without crossing the WPF project boundary.
- Supersedes Buildfix 15 Buildfix 1, whose namespace qualification was insufficient because `DiagnosticLog.cs` is compiled by the WPF shell project rather than `TheRadioVault.Services`.
- Leaves the 0.31-server progress compatibility and glyph-polish behaviour from Buildfix 15 unchanged.
- Database schema remains 45 and LAN capability generation remains 14.

## Alpha 10 Buildfix 15 — Server progress compatibility and glyph polish

- Compares Avalonia with the accepted 0.31 remote-client path and detects the older server response to the 0.32-only `claim-device` command.
- Falls back to the established `offline-progress` mutation used successfully by Radio Vault 0.31, while leaving three-device ownership/handoff enabled when connected to a supporting server.
- Prevents a version-specific or transient handoff telemetry failure from suppressing the canonical progress write; genuine current-server ownership conflicts still block stale writers.
- Keeps the final shutdown write, server confirmation, encrypted cache flush and Closing RadioVault overlay introduced in Buildfix 14.
- Removes Fluent hover tiles from glyph-only Dashboard and mini-player actions so hover feedback is limited to the glyph itself.
- Replaces yellow square play buttons in Dashboard discovery cards and Library rows with the unframed blue Now Playing vector; the Library row switches to a matching blue pause glyph while active.
- Preserves the accepted Dashboard, Library, Now Playing, Research, Moments and Favourites layouts. Database schema remains 45 and LAN capability generation remains 14.

# 0.32.0-alpha10-buildfix14-persistence-export-ui-refinement

## Alpha 10 Buildfix 13 — Seek, resume and integrated shell chrome

- Makes playhead dragging a complete gesture rather than relying on the Slider's ordinary bubbling release event: tunnel handlers retain the gesture, ValueChanged records the requested target and pointer-capture loss provides a second commit path.
- Rebuilds and verifies the NAudio output position after manual seeks and post-open resume, while generation/cancellation guards prevent an older seek from applying to a newly selected broadcast.
- Tracks the continuously observed logical position separately from one-off decoder reads and freezes that trusted position before the shutdown save, preventing reopen from falling back to the position at which the session originally started.
- Allows an intentional replay or backward seek to reset a completed broadcast to an in-progress resumable state without altering historical completion counts.
- Uses one shared 22-pixel vector heart and bookmark treatment for Library, Now Playing and the persistent player, removing the old Moments clock bubble and undersized favourite glyphs.
- Removes the reserved full-width chrome strip: the sidebar reaches the top, the content begins behind the integrated window controls, and a temporary surface/divider appears only once a full-page view has actually scrolled.
- Replaces the verbose Connected Access chrome label with a status-only green/yellow/red dot and preserves its detailed connection tooltip/flyout.
- Preserves the accepted Dashboard, Library, Now Playing, Moments, Favourites and Research layouts. Database schema remains 45 and LAN capability generation remains 14.

# 0.32.0-alpha10-buildfix12-playback-reliability-ui-polish

## Alpha 10 Buildfix 12 — Playback reliability and focused UI polish

- Makes drag-to-seek authoritative: the playhead owns its value while dragged, stale timer updates are ignored until release, and the NAudio output queue is rebuilt so playback resumes from the requested position.
- Prevents playback position leaking between broadcasts by refusing stale broadcast/media snapshots and by no longer publishing the previous engine state under a newly selected broadcast identity.
- Saves the real engine position during periodic and shutdown writes, and adds a cancellable pre-close flush before the Avalonia window is allowed to close.
- Aligns Connected Access playback with local behaviour: completed broadcasts reopen from the beginning, deliberate backward seeks replace pending offline writes, and active remote clients may persist an explicit reset to the beginning.
- Replaces the mixed-font sidebar symbols with consistently sized vector icons, reusing the Dashboard broadcast symbol for Library and simplifying Moments to a clear bookmark.
- Enlarges the persistent-player Favourite glyph, removes the misleading Grid option from Favourites, and halves the Library-row progress bar while using a compact percentage label that gives broadcast titles more room.
- Preserves the accepted Dashboard, Library, Now Playing, Moments and Research layouts. Database schema remains 45 and LAN capability generation remains 14.

# 0.32.0-alpha10-buildfix11-research-consistency-refinement

## Alpha 10 Buildfix 11 — Research and visual-consistency refinement

- Replaces the remaining Fluent teal states with the established desaturated yellow used by Radio Vault people/topic pills, including Library view toggles, selected broadcast rows and the persistent-player volume control.
- Gives the sidebar selection keyline additional right-edge clearance while preserving its fixed-width, transparent outlined treatment.
- Narrows the Library listening column and lets the broadcast title own the flexible width, with clean ellipsis behaviour as the window contracts.
- Clips and rounds the Moments selected-detail surface correctly, removing the square protrusions at its upper corners.
- Replaces the abstract Moments diamond with a consistent bookmark-and-time icon, and extends the Dashboard semantic icon colours across navigation and playback actions.
- Restyles Research search/edit fields and filters with rounded Radio Vault surfaces rather than black hover and square Fluent states.
- Makes Needs attention the default Research view, limited to review flags, unresolved conflicts and ambiguous reconciliation decisions; All research remains explicitly available.
- Adds a reassuring Everything is up to date empty state when no Research work is waiting.
- Deliberately postpones animation work until the structural and visual consistency changes have been accepted. Database schema remains 45 and LAN capability generation remains 14.

# 0.32.0-alpha10-buildfix10-now-playing-layout-refinement

## Alpha 10 Buildfix 10 — Now Playing, shared headings and persistent-player refinement

- Fixes the clipped right edge of the fixed-width yellow sidebar keyline by retaining a consistent selected surface with safe space for the sidebar scrollbar.
- Moves Library/Favourites, Moments, Now Playing and Research headings into their own page content and suppresses the detached fixed heading strip for those routes.
- Rebuilds Now Playing around a metadata-first main column and a 350-pixel right-side Up Next rail matching the Library detail-panel rhythm.
- Removes the duplicate seek bar, transport controls and volume control from the main Now Playing page because playback remains continuously available in the persistent player.
- Presents Now Playing hosts, guests, callers, mentioned people and topics with the same grouped accent-pill language as the Library mini information panel.
- Gives the persistent player more bottom breathing space, larger Library-style Favourite and Info actions, and a direct Save Moment action.
- Hides inactive shared-playback wording; transfer information appears only when playback is owned by another device.
- Renames the secondary Dashboard continuation list to Up next and expands On This Day with researched people/topic pills plus better-positioned Play and Favourite actions.
- Preserves the accepted Buildfix 9 Library grid/breadcrumb work, authoritative Research audit semantics, schema 45 and LAN capability generation 14.

# 0.32.0-alpha10-buildfix9-library-breadcrumb-sidebar-fix

## Alpha 10 Buildfix 9 — Library breadcrumb and sidebar selection correction

- Replaces the sidebar navigation ToggleButton selection surface with an explicit fixed-width Border keyline, preventing the Avalonia theme from painting the selected destination teal or shrinking the highlight to the text width.
- Keeps main destinations at 192 pixels and expanded show destinations at 172 pixels, with transparent selected backgrounds and a Radio Vault yellow outline.
- Replaces the far-right archive Back button with a left-aligned interactive breadcrumb trail: All → year → month. Earlier levels jump directly back to the relevant year or month grid, while the current level remains clearly highlighted.
- Reuses the complete selected-broadcast details panel beside month/day drill-down lists, including artwork, playback, favourite, full-information, summary, people and topic controls.
- Preserves the accepted Buildfix 8 Library rows, frames, pills and square year/month grid, together with the Buildfix 7 Dashboard and custom chrome. Database schema remains 45 and LAN capability generation remains 14.

# 0.32.0-alpha10-buildfix8-library-view-refinement

## Alpha 10 Buildfix 8 — Focused Library view refinement

- Refines the dark palette from the Buildfix 7 blue/slate treatment toward a more neutral charcoal/slate hierarchy while retaining enough cool colour to avoid flat neutral grey. Radio Vault yellow remains the primary accent.
- Replaces solid sidebar selection fills with fixed-width, yellow-keyline navigation states for both main destinations and expanded show entries.
- Places the primary Library broadcast list and month/day drill-down lists inside a shared framed surface instead of leaving individually framed rows visually free-floating.
- Reorders Library row actions to play, favourite, date, title and listening progress, with a direct circular information action at the far right. Hover-only play and information actions remain available on selected rows.
- Adds a direct full-information action to the selected-broadcast panel and presents hosts, guests, callers and mentioned people as grouped pills alongside topic pills.
- Rebuilds year and month browsing as a true square-tile WrapPanel grid with show context, broadcast count, listened percentage, favourites and optional artwork behind the information hierarchy.
- Returns month drill-down broadcasts to the accepted framed list treatment with direct row information actions.
- Deliberately limits this buildfix to shared dark surfaces, sidebar selection and the Library experience. The accepted Buildfix 7 Dashboard and custom chrome remain unchanged. Database schema remains 45 and LAN capability generation remains 14.

# 0.32.0-alpha10-buildfix7-integrated-chrome-dashboard-theme

## Alpha 10 Buildfix 7 — Integrated window chrome and Dashboard theme refinement

- Replaces the native application title bar with an Avalonia-drawn chrome row while retaining the platform resize border. The app label sits on the left; Connected Access status, minimise, maximise/restore and close controls sit together on the right.
- Makes the remaining chrome surface draggable and supports double-click maximise/restore without changing shutdown or application-lifetime behaviour.
- Collapses the old fixed Dashboard page header and moves the Dashboard title and description into the main scrolling content. Other screens retain their existing fixed page headings.
- Shifts the dark theme from flat near-black surfaces to the accepted mock-up's blue/slate/purple-tinted hierarchy while preserving Radio Vault yellow as the primary action accent.
- Removes circular stat-icon containers and replaces them with heavier vector icons using distinct yellow, blue, green and pink key colours.
- Deliberately limits this buildfix to chrome, Dashboard heading placement, theme surfaces and Dashboard stat presentation. Buildfix 6 layout, authoritative Research audit semantics and all earlier corrections remain unchanged. Database schema remains 45 and LAN capability generation remains 14.

# 0.32.0-alpha10-buildfix6-dashboard-layout-refinement

## Alpha 10 Buildfix 6 — Focused Dashboard layout refinement

- Rebalances the Dashboard top area around the accepted WPF composition: the featured continuation card now occupies the left side while a compact Surprise Me card and four listening-stat cards sit together on the right.
- Restyles Surprise Me as a purposeful discovery panel with explanatory text rather than a detached header button.
- Preserves the timed On This Day carousel and pagination dots while enlarging its artwork/content treatment and giving researched broadcast topics a clearly labelled, usable area.
- Keeps additional unfinished broadcasts beside On This Day rather than removing continuation choices.
- Replaces the full-width Recently Added list with two equal discovery columns: Recently Added and Unheard Broadcasts.
- Adds the five newest unheard broadcasts through the existing local/remote library browsing contract; playback, favourites and live progress continue to update across every Dashboard representation.
- Deliberately limits this buildfix to the Dashboard. Research authoritative-audit behaviour and all Buildfix 1–5 corrections are retained unchanged. Database schema remains 45 and LAN capability generation remains 14.

# 0.32.0-alpha10-buildfix5-authoritative-audit-scope-fix

## Alpha 10 Buildfix 5 — Authoritative-audit importer scope correction

- Fixes `CS0136` in `AvaloniaResearchPackTransferServices.cs`, where the per-record `authoritativeAudit` local collided with the later import-summary local in the same method scope.
- Uses distinct `recordIsAuthoritativeAudit` and `containsAuthoritativeAudit` names so the intended per-record replacement behaviour and final import summary remain unchanged.
- Retains Buildfix 4's restored interface frames and complete authoritative-audit Research replacement semantics, together with Buildfixes 1–3.
- Adds packaged-source validation that rejects the original conflicting declaration pattern. Database schema remains 45 and LAN capability generation remains 14.

# 0.32.0-alpha10-buildfix4-restored-frames-authoritative-audit

## Alpha 10 Buildfix 4 — Restored frames and authoritative Research audit

- Restores visible one-pixel frames to the shared `card` and `card-raised` surfaces rather than trying to create density by removing structural boundaries.
- Restores framed Dashboard continuation/recent rows, Library broadcast rows and period rows, the Library selected-broadcast details pane, and a retained yellow playing-row edge inside the restored outer row frame.
- Keeps the Alpha 10 content-first layout; subsequent visual work can tune padding, margins and hierarchy without deleting useful grouping.
- Ports the v0.31 RC1 `authoritative_audit` Research-pack buildfix into the current WPF/server importer and the default Avalonia local and remote Research workflows.
- Authoritative packs may intentionally clear stale research-owned fields, replace durable sources/people/topics/research moments, bypass superseded manual protection for those fields, reset the durable Research manual flag and retire superseded unresolved scalar conflicts.
- Ambiguous broadcast identity matches remain unapplied and review-only. Media identity, playback history, favourites, queue state and personal Moments remain outside the replacement path.
- Shows an explicit authoritative-mode warning before import and retains the normal restorable pre-import database backup and transaction boundary.
- Preserves the Buildfix 1 presentation link, Buildfix 2 Avalonia property fixes and Buildfix 3 UI-thread-safe theme startup. Database schema remains 45 and LAN capability generation remains 14.

# 0.32.0-alpha10-buildfix3-theme-ui-thread-dispatch

## Alpha 10 Buildfix 3 — UI-thread-safe theme startup

- Fixes the launch-time `InvalidOperationException` raised when `AvaloniaThemeService` changed `Application.RequestedThemeVariant` from the background host-composition task.
- Marshals the theme mutation through `Dispatcher.UIThread` while preserving synchronous application of the saved System, Light or Dark preference before host creation completes.
- Keeps database and service composition work off the UI thread so the startup window remains responsive.
- Retains the Buildfix 1 presentation-helper link and Buildfix 2 Avalonia XAML corrections.
- Does not change database schema 45, LAN capability generation 14, API v1, pairing, certificates, cache identity, Research data or playback-handoff behaviour.

# 0.32.0-alpha10-buildfix2-avalonia-xaml-property-fixes

## Alpha 10 Buildfix 2 — Avalonia XAML property corrections

- Fixes `AVLN1000` in `LibraryView.axaml` by removing `Width="Auto"` from a `Border`; Avalonia width is numeric and the omitted value already uses automatic layout.
- Fixes `AVLN2000` in `MainWindow.axaml` by replacing the WPF property name `StrokeLineJoin` with Avalonia's `StrokeJoin`.
- Adds package validation that rejects numeric layout properties set to `Auto` and WPF-only shape join/cap property names anywhere in Avalonia AXAML.
- Retains the Buildfix 1 `EpisodePresentationService.cs` compilation link.
- Does not change database schema 45, LAN capability generation 14, API v1, pairing, certificates, cache identity, Research data or playback-handoff behaviour.

# 0.32.0-alpha10-buildfix1-episode-presentation-link

## Alpha 10 Buildfix 1 — Episode presentation helper link

- Fixes the Avalonia compile failure where the linked shared `Models.cs` referenced `TheRadioVault.Services.EpisodePresentationService`, but the helper source was only compiled by the retained WPF project.
- Links `EpisodePresentationService.cs` into the Avalonia project so `SummaryTeaser`, `DiscoveryLine` and `ContextBadge` resolve in both desktop shells.
- Adds packaged-source validation that rejects a future Avalonia source package if this shared presentation dependency is omitted again.
- Does not change database schema 45, LAN capability generation 14, API v1, pairing, certificate, cache, Research or playback-handoff behaviour.

# 0.32.0-alpha10-visual-identity-parity-handoff

## Alpha 10 — Visual Identity, Parity Recovery & Three-Device Handoff

- Restores Radio Vault's yellow visual identity across dark and light themes and adds System, Light and Dark appearance selection.
- Reduces frames-within-frames and static chrome, with a denser Research master/detail workspace and more usable Metadata Studio space.
- Reworks Dashboard On This Day with timed pagination dots and topics, keeps Recently Added to five, and places Surprise Me with the primary listening actions.
- Synchronizes visible Dashboard and Library row progress with the live playhead instead of requiring manual refresh.
- Uses hearts for favourites, a persistent playing-row highlight, compact progress bars and proper vector search/volume presentation.
- Restores guarded local and server-backed Research pack import/export, including a pre-import local database backup and protection of manually modified research fields.
- Generalizes the authoritative playback session from phone/server to named playback devices and adds an Avalonia client handoff service.
- Adds visible active-device state, Continue on this device, Move playback to server, stable device identities, heartbeat/lease arbitration and a six-direction three-device acceptance matrix.
- Generalizes the iPhone/web handoff UI to display and claim playback from any named endpoint, and rejects stale remote progress writes after ownership changes.
- Preserves database schema 45, LAN capability generation 14, API v1 and all pairing, certificate and cache identities.

# 0.32.0-alpha9-buildfix1-dashboard-visual-tree-events

## Alpha 9 Buildfix 1 — Dashboard visual-tree event arguments

- Fixes the Avalonia Dashboard compile failure by using the framework-root `global::Avalonia.VisualTreeAttachmentEventArgs` type for attach/detach handlers.
- Removes the incorrect assumption that the event-argument type lives in `Avalonia.VisualTree`.
- Adds packaged-source validation so this namespace/type mismatch is rejected before Windows compilation.
- Does not change Dashboard timing, archive navigation, Search, playback, schema, LAN, API, pairing, certificate or cache behaviour.

## 0.32.0 Alpha 9 — Dashboard, Archive Navigation & Search UX

- Moves Connected Access into Settings and removes Queue as a standalone sidebar destination.
- Places Now Playing at the bottom of the navigation and integrates the persistent Queue into that screen.
- Restores dedicated Search and Favourites destinations, including show and useful-collection discovery.
- Rebuilds Library Grid mode as the WPF-style chronological hierarchy: years, months, then broadcasts.
- Adds listened percentages, counts and progress summaries to year and month cards.
- Reworks Dashboard around the most probable next action: one featured unfinished broadcast, four additional continuation choices, five recently added items, Surprise me and a timed On this day carousel.
- Preserves progressive disclosure, contextual secondary actions, responsive elastic scrolling and local/remote ownership safeguards.
- Preserves schema 45, LAN generation 14, API v1 and all pairing/certificate/cache identities.

# 0.32.0-alpha8-ux-audit-progressive-disclosure

## 0.32.0 Alpha 8 — UX Audit & Progressive Disclosure

- Audits the complete Avalonia shell against task-first, progressive-disclosure and platform-familiar interaction principles.
- Simplifies navigation, connection status and the persistent player while keeping primary actions obvious.
- Moves secondary actions into hover states, context menus and overflow menus across Dashboard, Library, Now Playing, Queue and Moments.
- Adds debounced search-as-you-type to Library, Moments and Research.
- Reduces duplicate metrics and persistent technical labels.
- Collapses technical broadcast identity, advanced Research filters/metadata, Connected Access setup and maintenance tools until requested.
- Improves keyboard focus visibility without changing the accepted reduced-motion and elastic-scroll behavior.
- Preserves schema 45, LAN generation 14, API v1 and all pairing/certificate/cache identities.

# 0.32.0-alpha7-desktop-tools-library-parity

## 0.32.0 Alpha 7 — Desktop Tools, Library Grid and Now Playing Parity

- Added WPF-style List and Grid Library modes across All broadcasts and per-show views.
- Simplified the Library toolbar and compact details panel, added people/topics context, and moved technical identity/media metadata to a full Broadcast information route.
- Added a dedicated Now Playing page with full transport, seek, speed, volume, favourite, Queue, Moment, summary, people, topics and notes presentation.
- Simplified the sidebar by removing redundant Workspace and server-Library labels.
- Added guarded Settings & Tools for Library folders, archive health, application data and diagnostics.
- Added local and remote `IBroadcastDetailsService` implementations without changing server ownership or allowing local-database fallback.
- Preserved schema 45, LAN capability generation 14, API v1 and pairing/certificate/cache identities.


- Fixes the Avalonia-linked remote cache service build by explicitly importing `TheRadioVault.Web.Contracts`, rather than relying on the WPF-only global-usings file.
- Removes the two nullable-analysis warnings reported by the Windows Alpha 6 build.
- Added the Avalonia Connected Access workspace with server discovery, certificate-pinned pairing, connection testing, startup-mode selection, reconnect, cache and ownership diagnostics.
- Added live and encrypted cached remote-library startup through the application-owned session coordinator.
- Added remote Dashboard, Library, artwork, playback, favourites, Queue, Moments, Research and normal metadata-write adapters behind the existing platform-neutral contracts.
- Added canonical remote multipart streaming through the loopback credential proxy and NAudio HTTP media support.
- Added pending playback-progress retention and retry after reconnection.
- Preserved strict ownership guards: cached sessions are read-only and remote services never fall through to the client local SQLite database.
- Preserved database schema 45, LAN capability generation 14, API v1 and all pairing/certificate/cache identities.

# 0.32.0-alpha5-research-workspace-metadata-studio

- Added the Avalonia Research workspace with overview counts, show/status/review filters, record browsing and evidence coverage.
- Added a platform-neutral `IResearchWorkspaceService` over the existing schema-45 Research library.
- Added Metadata Studio editing for broadcast metadata, people, topics, confidence, notes and linked artwork.
- Manual Metadata Studio writes now create protected `research_field_provenance` records and update linked archive metadata through a guarded service boundary.
- Added source diagnostics, evidence browsing and read-only research import history.
- Added an explicit remote-owned write guard so a future connected client cannot fall through to its local cache.
- Preserved empty Research fields without generated failure wording.
- Retained all accepted Alpha 4 Buildfix 2 Library, playback, Queue, Moments, show-navigation and responsive-overscroll behaviour.
- Database schema remains 45; LAN capability generation remains 14; API v1 and pairing/cache identities are unchanged.

# 0.32.0-alpha4-buildfix2-library-row-polish-responsive-overscroll

- Retains the user-accepted Alpha 4 favourites, persistent Queue and Moments workflows.
- Reworks elastic overscroll so displacement tracks active input immediately and only the release phase uses the damped spring.
- Keeps the Alpha 4 jitter fix while reducing release delay and increasing snap-back responsiveness.
- Adds an expandable/collapsible Library parent entry in the sidebar.
- Populates Library children from the real collection overview, including show names and broadcast counts.
- Filters the canonical Library by `CollectionId` when a show is selected, while preserving search and listening filters inside that show.
- Shows the active collection in the Library page title and collection pill.
- Retains schema 45, LAN capability generation 14, API v1 and all pairing/cache identities.

# 0.32.0-alpha3-local-playback-rubberbanding

- Adds real local playback to the Avalonia default shell through the hardened playback coordinators.
- Adds `ILocalPlaybackLibraryService` to resolve canonical preferred recordings, multipart segments and shared canonical listening state without exposing SQLite to presentation code.
- Adds an Avalonia-local NAudio engine with play, pause, seek, skip, volume and 0.5×–3× speed control.
- Connects Dashboard and Library rows/details to Play or Resume actions.
- Replaces the inactive playback shell with live Now Playing artwork, identity, progress, part state and transport controls.
- Persists resume position, duration, speed, play count and natural completion across canonical broadcast members.
- Adds automatic transitions between multipart segments.
- Adds one global elastic overscroll behaviour for all Avalonia `ScrollViewer` surfaces while preserving wheel, keyboard, inertia and nested scroll chaining.
- Disables elastic displacement when Windows requests reduced client-area animation.
- Retains Avalonia as `TheRadioVault.exe`, WPF as `TheRadioVault.WpfReference.exe`, schema 45, LAN capability generation 14, API v1 and all pairing/cache identities.

# 0.32.0-alpha2-avalonia-design-foundation

- Replaces the temporary Avalonia engineering scaffold with the first cohesive Radio Vault desktop design system.
- Adds system-aware dark and light theme dictionaries and reusable visual tokens for surfaces, borders, text, accents, status colors and spacing.
- Adds reusable shell navigation, button, card, pill, search, filter, list-row and progress treatments.
- Redesigns the Dashboard around real archive totals, listening completion, continue-listening and recent-broadcast states.
- Redesigns the canonical Library with a clearer search/filter surface, selected rows, metadata badges, listening progress and a structured details panel.
- Adds selected navigation state without introducing Avalonia dependencies into the toolkit-neutral presentation project.
- Adds loading, empty-results, no-selection, favourite and metadata-attention states.
- Adds the persistent bottom playback-bar shell in preparation for Alpha 3 while keeping playback intentionally disabled in Avalonia.
- Restyles the hardened startup window to match the new desktop visual language.
- Retains Avalonia as `TheRadioVault.exe`, WPF as `TheRadioVault.WpfReference.exe`, schema 45, LAN capability generation 14, API v1 and all pairing/cache identities.

# 0.32.0-alpha1-buildfix5-avalonia-startup-hardening

- Opens a lightweight Avalonia startup window immediately instead of performing database and composition work before any UI is visible.
- Moves local database/application-host creation off the UI thread while retaining the existing database, schema and service boundaries.
- Keeps a failed startup window open with the complete exception and an **Open log location** action rather than silently exiting.
- Adds top-level, AppDomain, task and Avalonia UI-thread exception capture.
- Writes ordered startup checkpoints and full exception details to `%APPDATA%\TheRadioVault\avalonia-startup-failure.log`.
- Shows a native Windows error dialog when failure occurs before Avalonia can display a window.
- Retains all Buildfix 2, 3 and 4 compiler/XAML corrections, Avalonia as `TheRadioVault.exe`, WPF as `TheRadioVault.WpfReference.exe`, schema 45, LAN capability generation 14, API v1 and all pairing/cache identities.

# 0.32.0-alpha1-buildfix4-avalonia-default-shell

- Replaces unsupported `Grid.Padding` usage in the Avalonia Library view with padded `Border` wrappers.
- Replaces the Avalonia 12 obsolete `TextBox.Watermark` property with `PlaceholderText`.
- Adds packaged-source guards that reject `Grid.Padding` and `TextBox.Watermark` before the Windows compiler/XAML compiler stage.
- Retains the Buildfix 2 lifetime aliases and Buildfix 3 constructor/root-namespace corrections.
- Preserves Avalonia as `TheRadioVault.exe`, WPF as `TheRadioVault.WpfReference.exe`, schema 45, LAN capability generation 14, API v1 and all pairing/cache identities.

# 0.32.0-alpha1-buildfix3-avalonia-default-shell

- Fixes the nullable `AvaloniaStartupOptions` default construction by naming the `DatabasePath` argument, avoiding ambiguity with the generated record copy constructor.
- Qualifies `Application`, `Thickness` and `Media.TextWrapping` through `global::Avalonia` so the project namespace `TheRadioVault.Desktop.Avalonia` cannot shadow the Avalonia framework root namespace.
- Adds source-validation guards for all four compiler findings reported by the second Windows build.
- Preserves Avalonia as `TheRadioVault.exe`, WPF as `TheRadioVault.WpfReference.exe`, schema 45, LAN capability generation 14, API v1 and all pairing/cache identities.

# 0.32.0-alpha1-buildfix2-avalonia-default-shell

- Makes Avalonia the canonical desktop entry point and output executable, `TheRadioVault.exe`.
- Renames the retained complete WPF shell output to `TheRadioVault.WpfReference.exe` so it cannot be mistaken for the new default application.
- Places the Avalonia project first in the solution and adds a shared Visual Studio solution launch profile that starts Avalonia only.
- Adds `BUILD-AND-RUN.cmd` and `RUN-RADIO-VAULT.cmd` so normal build/launch does not require entering PowerShell commands.
- Updates release-gate and packaging expectations for the new executable identities while preserving dual-shell validation.
- Preserves the Alpha 1 feature boundary, database schema 45, LAN capability generation 14, API v1 and all pairing/cache identities.

# 0.32.0-alpha1-avalonia-shell-foundation

- Begins the Avalonia desktop rebuild from the accepted v0.31.0 Core Hardening baseline while retaining the complete WPF application as the working reference shell.
- Adds `TheRadioVault.Desktop.Avalonia`, targeting .NET 8 and using pinned Avalonia 12.1.0 desktop/Fluent packages.
- Adds toolkit-neutral `TheRadioVault.Presentation` view models, commands and shell navigation without Avalonia, WPF or database implementation dependencies.
- Adds Avalonia platform adapters for dispatching, file/folder selection, clipboard, external launching, appearance, screen bounds, application lifetime and user notifications.
- Adds `ILibraryBrowseService` and a canonical read model which prefers adopted Library Truth and safely falls back to legacy broadcasts when canonical cutover is unavailable.
- Delivers the first functional vertical slice: Dashboard metrics, continue listening, recent broadcasts, Library search/filter/list and broadcast details.
- Keeps the Avalonia slice read-only; playback, editing, Research, Connected Access and specialised workflows remain in the accepted WPF shell.
- Adds dual-shell build/run/package tooling plus executable architecture, WPF-independence and Avalonia-foundation validation.
- Preserves database schema 45, LAN capability generation 14, API v1, pairing/certificate state and all existing web/cache identities.

# 0.31.0

- Promotes the successfully built, fully release-gated and runtime-accepted RC1 implementation to the stable Core Hardening release.
- Introduces no runtime feature, database migration, LAN/API capability, pairing or cache identity change from RC1.
- Finalises build identity as `0.31.0` across VERSION.txt, WPF assembly metadata, architecture/WPF-independence reports, validation, documentation and release packaging.
- Records the stable channel and accepted RC1 baseline in `BUILD_INFO.json`.
- Retains the Windows PowerShell 5.1 validator fixes, deterministic Release gate, compiled ProductVersion verification and clean self-contained `win-x64` packaging.
- Adds stable release, acceptance, static-validation and Avalonia-handoff documentation.
- Preserves database schema 45, LAN capability generation 14, API v1, web-shell generation 10, shell cache v33, IndexedDB v2 and v1 audio/artwork cache identities.

# 0.31.0-rc1-core-hardening-release-candidate

- Promotes the successfully built, fully release-gated and runtime-accepted Beta 1 Core Hardening implementation unchanged into v0.31 RC1.
- Integrates the Windows PowerShell 5.1 validator corrections discovered during Beta 1 acceptance: safe `${relative}` interpolation and distinct typed XML versus project-path variables.
- Freezes database schema 45, LAN capability generation 14, API v1, web-shell generation 10, shell cache v33, IndexedDB v2 and v1 audio/artwork cache identities.
- Updates VERSION.txt, WPF assembly metadata, architecture/WPF-independence report identities, release tooling and documentation to the RC1 identity.
- Marks packaging as the RC channel and records the accepted Beta 1 Windows build, release-gate and runtime baselines in BUILD_INFO.json.
- Adds final clean-extraction, existing-install upgrade, local/remote parity, repeated-shutdown and daily-driver acceptance guidance.
- Introduces no feature, database migration, LAN/API capability change, pairing change or cache reset requirement.

# 0.31.0-beta1-wpf-independence-proof

- Freezes the accepted Alpha 6 Buildfix 1 runtime feature set for the first Core Hardening beta.
- Adds an executable WPF-independence proof which verifies neutral-project purity, application dependency direction and all critical startup, shutdown, playback, platform and remote-session boundaries.
- Produces a machine-readable Avalonia handoff report with hard guarantees, current WPF coupling metrics and explicit presentation-replacement work packages.
- Adds an Avalonia handoff document separating reusable backend seams from work that correctly remains in the current WPF presentation.
- Strengthens the release gate and source validation so both architecture validation and the WPF-independence proof must pass before compilation and packaging.
- Changes release packaging metadata from alpha to beta while preserving database schema 45, LAN capability generation 14, API v1, pairing credentials and every cache identity.
- Preserves the accepted remote Now Playing parity fix and all Alpha 6 composition, playback and shared-session behavior.

# 0.31.0-alpha6-di-tests-cleanup-buildfix1

- Restores full Now Playing information on authoritative remote clients instead of querying the remote client’s isolated local/cache database for server-owned broadcast knowledge.
- Shows the synchronized show, date, headline and summary immediately, including a useful cached read-only fallback.
- Loads live station/slot/part identity, hosts, guests, callers, mentioned people, topics, archive notes and artwork from the server broadcast-details endpoint.
- Cancels stale detail/artwork requests when playback changes, the window switches mode or the application closes, preventing an older response from replacing the current broadcast.
- Adds source validation and acceptance coverage for remote Now Playing parity.
- Preserves all accepted Alpha 6 dependency-injection, playback, session, database, LAN, pairing and cache behaviour.

# 0.31.0-alpha6-di-tests-cleanup

- Hardened the platform-neutral application service registry with explicit singleton/transient lifetimes, lazy singleton factories, duplicate-registration rejection, dependency-cycle detection, composition reporting, runtime freezing and orderly singleton disposal.
- Validates every required application/platform service before the first window opens and writes a concise composition diagnostic.
- Added `ILocalPlaybackEngineFactory` and a Windows implementation so `MainWindow` no longer constructs `WpfMediaPlaybackEngine` directly.
- Added `PlaybackSessionFactory` so application playback-session construction no longer lives in WPF presentation code.
- Explicitly disposes the shared remote Library session during ordered window shutdown and local/remote mode replacement.
- Expanded regression coverage for composition completeness, frozen registrations, lazy singleton lifetime, cyclic dependency rejection and playback-session factory ownership.
- Strengthened architecture checks against direct WPF local-engine/session construction and corrected stale Alpha 2/Alpha 4 release-tooling labels.
- Preserves database schema 45, LAN capability generation 14, API v1, pairing credentials, encrypted client caches and all accepted Alpha 5 behaviour.

# 0.31.0-alpha5-lan-shared-session-consolidation

- Added a platform-neutral `RemoteLibrarySessionCoordinator` which owns the remote Library synchronization cursor, single-flight gate, timeout/cancellation lifetime, live/cache/unavailable state, reconnect backoff and diagnostics.
- Replaced WPF-owned remote synchronization semaphores, cancellation sources, cursor fields, retry fields and diagnostic fields with one shared session snapshot and synchronization lease.
- Routed cached startup, initial connection, routine polling, forced refresh, manual reconnect and orderly shutdown through the same lifecycle.
- Preserved in-place Beta 1 Library deltas, encrypted cache behaviour, server-owned mutation guards and Alpha 4 playback/media boundaries.
- Added regression and architecture checks for cursor advancement, duplicate-sync suppression, cached read-only failure state and immediate manual reconnect.
- Preserved database schema 45, LAN capability generation 14, API v1, pairing, cache identities and accepted Alpha 4 behaviour.

# 0.31.0-alpha4-playback-media-boundary-cleanup

- Added a platform-neutral `PlaybackSessionCoordinator` which owns the live local and LAN playback-engine command path, events, state and disposal.
- Added `PlaybackProgressCoordinator` to preserve the strongest known listening position when a media backend transiently reports zero and to produce one persistence plan for local and remote saves.
- Added `PlaybackCompletionCoordinator` to track natural movement into the completion window without treating seeks, skips or backend corrections as completed listening.
- Moved the ordinary WPF `MediaPlayer` implementation from the desktop composition project into `TheRadioVault.Platform.Windows` as `WpfMediaPlaybackEngine`.
- Routed live open, play, pause, stop, seek, speed, volume, state-change and shutdown operations through the application playback session.
- Exposed certificate-pinned LAN progress synchronisation as an adapter capability so the presentation no longer stores or controls a concrete playback engine.
- Added playback-session, progress-protection and completion-evidence regression coverage and architecture guards against direct `MainWindow` playback-engine commands.
- Preserved database schema 45, LAN capability generation 14, API v1, pairing, cache identities and accepted Alpha 3 behaviour.

# 0.31.0-alpha3-windows-platform-abstraction

- Added platform-neutral contracts and request models for external launching, system appearance and virtual-screen bounds.
- Added Windows/WPF adapters for shell launching, Windows theme preference and virtual-screen intersection checks.
- Removed direct `Process.Start`, WPF Clipboard, Windows Registry and `SystemParameters` use from the WPF application project and made the architecture gate reject their return.
- Routed all existing clipboard actions through `IClipboardService`.
- Routed source-link opening, file reveal, data/model folder opening, crash-log opening and database restart through `IExternalLauncherService`.
- Routed common archive-folder, backup, metadata import/export and transcription model selection through `IFileSelectionService`.
- Routed system-theme detection and saved-window visibility checks through Windows platform adapters.
- Preserved database schema 45, LAN capability generation 14, API v1, pairing, cache identities and accepted Alpha 2 behaviour.

# 0.31.0-alpha2-application-service-extraction

- Added a platform-neutral application composition registry and a Windows composition module for WPF adapters.
- Extracted local-versus-server startup selection into `ApplicationStartupCoordinator`.
- Extracted ordered shutdown-step execution and failure isolation into `ApplicationShutdownCoordinator`.
- Extracted duplicate-safe local/remote window transitions into `ApplicationWindowTransitionCoordinator`.
- Routed the live WPF startup, mode-switch and shutdown paths through the new application services without changing library, playback, LAN or database behaviour.
- Added regression coverage and updated architecture/coupling evidence.

# 0.31.0-alpha1-architecture-baseline-buildfix2

- Completes the Alpha 1 namespace-collision repair after the second Windows build exposed eight additional `Application.Current` references resolving to `TheRadioVault.Application`.
- Fully qualifies every active WPF application access as `global::System.Windows.Application.Current`, covering Library Truth shutdown/recovery, local/remote window transitions, remote playback dispatch, playback-engine dispatch and theme resources.
- Updates the dispatcher regression marker and adds a whole-source guard that rejects any future unqualified `Application.Current` usage in the WPF and Windows-adapter projects.
- Preserves the Alpha 1 architecture boundaries, accepted v0.30.0 runtime behaviour, database schema 45, LAN capability generation 14, API v1 and all web/cache identities.

# 0.31.0-alpha1-architecture-baseline-buildfix1

- Fixes the Alpha 1 Windows adapter compilation failure caused by the identifier `Application` resolving to the sibling namespace `TheRadioVault.Application` instead of WPF's application type.
- Fully qualifies the shutdown call as `global::System.Windows.Application.Current.Shutdown(exitCode)`, resolving `CS0234` in `WpfApplicationLifetime.cs`; the missing `TheRadioVault.Platform.Windows.dll` error was a downstream cascade.
- Adds a source-validation regression guard for the namespace collision.
- Preserves the Alpha 1 architecture boundaries, accepted v0.30.0 behaviour, database schema 45, LAN capability generation 14, API v1 and all web/cache identities.

# 0.31.0-alpha1-architecture-baseline

- Starts the v0.31 Core Hardening milestone without changing user-visible application behaviour.
- Adds the platform-neutral `TheRadioVault.Application` boundary and contracts for dispatching, notifications, file selection, clipboard, navigation, window coordination and lifetime.
- Adds the deliberate `TheRadioVault.Platform.Windows` adapter boundary with initial WPF/Windows implementations.
- Adds executable architecture validation, a generated coupling report and a phased migration ledger.
- Preserves database schema 45, LAN capability generation 14, API v1 and all v0.30 web/cache identities.

# 0.30.0

- Promotes the successfully built and user-accepted RC1 implementation to the stable Multi-Device Library Access release.
- Introduces no feature behaviour, API route, LAN capability, cache identity or database-schema change from RC1.
- Finalizes build identity as `0.30.0` across VERSION.txt, WPF assembly metadata, LAN discovery regression fixtures, documentation and release packaging.
- Retains deterministic Release gating, compiled ProductVersion verification and clean self-contained `win-x64` packaging with `BUILD_INFO.json` and SHA-256 output.
- Corrects the source-validation marker for the two-argument ProductVersion `StartsWith` check so `release-gate.ps1` can validate its own compiled-version guard.
- Adds final upgrade, clean-package, parity, outage, mode-switching and shutdown acceptance guidance.
- Preserves database schema 45, LAN capability generation 14, API v1, web-shell generation 10, shell cache v33, IndexedDB v2 and v1 audio/artwork cache identities.

# 0.30.0-rc1-multi-device-release-candidate

- Promotes the accepted Beta 1 application behaviour unchanged into the first v0.30 release candidate.
- Freezes database schema 45, LAN capability generation 14, API v1, web-shell generation 10, shell cache v33, IndexedDB v2 and v1 audio/artwork cache identities.
- Updates build identity consistently through VERSION.txt, WPF assembly metadata, release documentation and LAN discovery regression fixtures.
- Strengthens the release gate with deterministic compilation flags and verification that the compiled WPF assembly ProductVersion matches VERSION.txt.
- Publishes self-contained win-x64 output into a clean artifacts directory and adds a repeatable package-release script producing BUILD_INFO.json, a distributable ZIP and SHA-256 checksum.
- Adds final clean-extraction, Beta 1 upgrade, local/remote parity, outage, mode-switching, repeated-shutdown and daily-driver acceptance coverage.
- Introduces no new feature area, database migration, LAN/API capability or cache reset requirement.

# 0.30.0-beta1-multi-device-hardening

- Promotes the accepted Alpha 9 feature set into the v0.30 Multi-Device Library Access beta; no new user-facing feature area, API route or database migration is introduced.
- Applies routine server deltas in place instead of rebuilding every remote-client broadcast row after progress, favourite, queue, listening-status or same-identity metadata changes.
- Reserves full remote-client Library reconstruction for server-session resets, additions, deletions, show moves and date moves, preserving deterministic collection and date facets while reducing daily-driver UI churn.
- Adds synchronization timing, delta/reset mode, changed-row counts, retry state and last-error detail to copied Connected Access parity diagnostics.
- Logs changed or unusually slow federation sync preparation on the server with bounded counts and elapsed time, without logging every healthy no-change poll.
- Extends the session guard to both local-library and remote-client modes and records the active shutdown stage, so any watchdog-forced exit identifies the component that had not completed.
- Keeps the bounded two-second final remote progress save and 12-second shutdown watchdog accepted in Alpha 9 Buildfix 2.
- Separates real process exit from in-app local-library/remote-client window transitions, preventing the outgoing window from arming a watchdog against the replacement window or stopping the replacement local web server.
- Freezes database schema 45, LAN capability generation 14, API v1, web-shell generation 10, shell cache v33, IndexedDB v2 and the v1 audio/artwork caches for beta soak testing.

# 0.30.0-alpha9-parity-audit-hardening-buildfix2

- Hardens application shutdown after Alpha 9 acceptance testing found an intermittent process hang while quitting.
- Stops playback/save timers and remote synchronization before performing the final listening-progress save, preventing new shutdown-time work from racing the close sequence.
- Bounds the remote-client final progress mutation to two seconds; the normal periodic server save remains authoritative if the final request cannot complete promptly.
- Adds per-step shutdown diagnostics so any future delay identifies the exact component that had begun but not completed.
- Adds a 12-second final shutdown watchdog which terminates only the already-closing process, preventing Radio Vault from remaining indefinitely in Task Manager.
- Preserves the Buildfix 1 JSON helper, database schema 45, LAN capability generation 14, API routes and all accepted Alpha 9 behaviour.

# 0.30.0-alpha9-parity-audit-hardening-buildfix1

- Restores the omitted shared `WriteJsonAsync<T>` response helper used by the new federation parity and Research workspace endpoints.
- Serializes those endpoint payloads with the established web JSON options and writes the same no-store JSON response used elsewhere in the local web server.
- Resolves the three `CS0103` errors in `LocalWebServer.FederationParity.cs` and `LocalWebServer.FederationResearchWorkspace.cs`; the missing `TheRadioVault.Web.dll` error is a downstream cascade.
- Preserves database schema 45, LAN capability generation 14, API routes and all Alpha 9 behaviour.

# 0.30.0-alpha9-parity-audit-hardening

- Adds a structured server parity endpoint covering the normal remote-client application surfaces and exposes its live availability in Connected Access Settings.
- Adds copyable parity diagnostics containing server identity, version, capability generation, Library revision, change sequence and per-surface access state.
- Brings Metadata Studio into remote-client mode using the existing UI and server-backed metadata mutations; prevents headline-review and other local-database write paths from running remotely.
- Downloads and caches server artwork per paired server for the normal Library, Broadcast Info and Metadata Studio views.
- Adds read parity for the normal Research workspace: overview counts, broadcast records, filters, source diagnostics, import history and record details are loaded from the server.
- Extends global search and Explore source results with the server Research workspace.
- Keeps advanced Research reconciliation, repair, rollback and storage-owned actions explicitly server-owned rather than applying them to the remote client's dormant local database.
- Adds route and regression coverage for the parity and Research workspace contracts.
- Advances LAN capability generation to 14 and advertises `lan.parity-audit` and `lan.research-workspace`; database schema remains 45.

# 0.30.0-alpha8-cache-resilience-sync

- Adds an encrypted, compressed, server-bound remote-client metadata cache that survives application restarts without using or modifying the dormant local-library database.
- Opens the normal Radio Vault shell immediately from cached broadcasts, dashboard data, queue, Moments, transcript summaries and the last server Settings snapshot, then transitions to live server state in place.
- Adds `/api/v1/federation/library-sync`, a bounded synchronization session and SHA-256 Library revision with retained change-journal gap detection, reset protection, changed-broadcast deltas and deleted-broadcast propagation.
- Polls lightweight revisions every six seconds, retries unavailable servers with bounded exponential backoff and preserves the active page, search, filters and selected broadcast during refresh.
- Keeps cached mode intentionally read-only for playback, queue, favourites, listening status, Moments, metadata and research pack operations; the remote client never silently switches to its local library.
- Adds cached search, broadcast-summary, Moment and transcript-summary browsing while the server is unavailable.
- Adds remote-client Settings controls for cache enablement, size, stored bytes, last synchronization, manual refresh and cache clearing.
- Adds automatic streamed-audio recovery with three bounded retries from the last observed position before presenting a final failure.
- Derives an opaque SHA-256 Library revision from the server synchronization session and monotonic change sequence, keeping six-second no-change polls lightweight while forcing a full reset after a server restart or journal gap.
- Advances LAN capability generation to 13 and advertises `lan.cache-sync`; database schema remains 45.

# 0.30.0-alpha7-buildfix1-dispatcher-server-terminology

- Fixes the false first-play failure where a manifest continuation read WPF `MediaPlayer` state from a worker thread.
- Captures the UI dispatcher owned by the remote playback controller and marshals media open, close, play, pause, seek, speed, volume, position, progress snapshots, event delivery and disposal through it.
- Keeps server I/O asynchronous while ensuring engine snapshots are constructed only on the UI dispatcher.
- Prevents the cross-thread exception from resetting the playback UI to Play after the audio stream has already opened.
- Standardises current server/remote-client UI, Settings, errors, diagnostics and v0.30 documentation; device-specific role language is removed.
- Preserves Alpha 7 loading and buffering UX, Alpha 6 Research/Settings parity, database schema 45 and LAN capability generation 12.

# 0.30.0-alpha7-remote-ux-playback-state

- Adds an animated skeleton surface inside the normal Radio Vault shell while a remote client performs its initial connection and library load.
- Adds native busy overlays for network-bound library refresh, global search, broadcast details, transcripts and server Settings.
- Replaces the play/pause glyph with a spinner while remote media manifests, HTTP range streams or WPF decoders are opening or buffering, and blocks duplicate playback commands while that work is pending.
- Makes the actual playback engine state authoritative for the mini-player, large player, Dashboard action labels and Library playback indicators.
- Waits for the real WPF `MediaOpened` event instead of treating completion of the remote manifest request as playable media.
- Tracks playback intent separately from observed stream progress and automatically reconciles paused, playing and buffering state every 500 ms.
- Fixes the remote play/pause icon drifting to Play while audio is still streaming, eliminating the two-click recovery behaviour.
- Preserves Alpha 6 remote Research/Settings parity, database schema 45, LAN capability generation 12 and the accepted Alpha 5 Buildfix 5 canonical scan promotion.

# 0.30.0-alpha6-remote-research-settings-parity

- Adds full server research-pack import/export through the existing Research workspace.
- Uploads client-selected packs for server-side validation, preview and atomic application; preserves the exact package hash, provenance, duplicate detection and pre-import safety snapshot; imports never touch the client's dormant local database.
- Adds server import cancellation, bounded/expiring preview sessions and cleanup of abandoned staged packages.
- Generates exports from the server database and downloads them to a client-selected path.
- Adds a mode-aware remote Settings experience showing server archive folders, health, storage, preservation and backup state.
- Synchronizes skip-back, skip-forward and completion-threshold playback preferences through the server.
- Replaces server-hosting/Radio Vault Anywhere controls with client connection, certificate, reconnect and local-fallback information.
- Hides Transcription, Advanced and misleading local maintenance controls in remote-client mode.
- Advances LAN capability generation to 12 and advertises `lan.research-packs` and `lan.settings-parity`.
- Preserves database schema 45, Alpha 5 Buildfix 5 post-scan canonical promotion and the identical local/remote `MainWindow`.

# 0.30.0-alpha5-buildfix5-post-cutover-scan-promotion

- Fixes successful post-cutover scans leaving newly added broadcasts invisible in both the server Library and paired clients.
- Adds incremental canonical promotion after every physical library scan.
- Groups trustworthy multipart additions into one canonical recording and appends new canonical broadcasts without rewriting the sealed adoption baseline.
- Attaches additions matching an existing show/date/slot as preserved recordings under that canonical broadcast.
- Keeps uncertain, undated, Unsorted and held/review identities outside automatic adoption.
- Extends canonical summary, Library, recording-option, playback and timeline queries to include incrementally appended rows.
- Repairs previously scanned-but-unmapped files on the next scan.
- Preserves database schema 45, LAN capability generation 11 and the existing-UI remote-client architecture.

# 0.30.0-alpha5-buildfix4-canonical-manifest-file-size

- Fixes the server's 500 Internal Server Error when a paired client requests a canonical media manifest.
- Corrects the schema-45 media-size query from the nonexistent `media_files.size_bytes` column to the established `media_files.file_size` column.
- Adds canonical download-manifest regression assertions covering multipart part sizes and aggregate size.
- Preserves Buildfix 3's launch guard, Buildfix 2's transcript contract and Buildfix 1's indistinguishable existing-UI remote-client architecture.
- Does not change database schema 45, LAN capability generation 11, API routes, media identities, playback timing or cache identities.

# 0.30.0-alpha5-buildfix3-startup-speed-guard

- Fixes the launch-time `NullReferenceException` in `SpeedCombo_SelectionChanged`.
- Ignores the XAML-initialisation selection event until `MainWindow` has completed loading, preventing access to the not-yet-created playback engine and database-backed player state.
- Preserves Buildfix 2's transcript contract and Buildfix 1's indistinguishable existing-UI remote-client architecture.
- Does not change database schema 45, LAN capability generation 11, API routes, playback semantics or cache identities.

# 0.30.0-alpha5-buildfix2-transcript-contract

- Fixes CS0246 in `LanFederationServices.cs` by adding the omitted public `LanRemoteTranscriptSummary` client contract consumed by `LoadTranscriptsAsync`.
- Keeps the Buildfix 1 existing-UI remote-client architecture unchanged.
- Does not change database schema 45, LAN capability generation 11, API routes, playback behaviour or Anywhere cache identities.

# 0.30.0-alpha5-buildfix1-ui-parity

- Removes the separate `RemoteLibraryWindow`, remote Broadcast Details window and remote Moment editor introduced by Alpha 5.
- Makes remote-client mode start the existing `MainWindow` and swap its playback, archive, queue and Moments services behind the established UI.
- Keeps Dashboard, Library, Search, Favourites, Now Playing, Moments, Transcripts, Broadcast Info, Research and Settings visually identical to local-library mode.
- Adds server transcript-list browsing to the existing Transcripts page.
- Adds Broadcast Info metadata write-through from the existing editor.
- Preserves certificate-pinned canonical multipart streaming, progress/favourite/listening-state/queue/Moment write-through and visible local fallback.
- Prevents remote-client shutdown from recording a local-library session or overwriting the remote client's local playback state.
- Keeps local archive maintenance owned by the server while retaining the normal Settings layout.
- Preserves Alpha 4 migration, schema 45, capability generation 11 and all Anywhere cache identities.

# 0.30.0-alpha5-authoritative-client-shell

- Adds startup shell selection so a remote client can open directly against the Radio Vault server without initialising its local database.
- Adds a native server Dashboard with canonical counts, Continue Listening, recent additions and On This Day.
- Expands the remote Library with bounded server-side search plus show, year and listening-state filters.
- Adds native favourites and shared-queue workspaces.
- Adds server broadcast information with people, topics, notes, research provenance, Moments and timed transcripts.
- Preserves certificate-pinned canonical multipart playback and write-through for progress, deliberate seeks, favourites, listened state and queue changes.
- Adds session-only local fallback, a persistent local-default recovery action and the `--local-library` launch override.
- Keeps the paired credential and local archive intact when changing shell mode.
- Advances LAN capability generation to 11 and advertises `lan.full-shell`.
- Preserves Alpha 4 personal-state migration, database schema 45, web-shell generation 10, Anywhere shell cache v33, IndexedDB v2 and v1 audio/artwork caches.

# 0.30.0-alpha4-laptop-state-migration

- Adds a one-time portable `.trvstate` export for listening progress, first/last-played history, play/completion counts, playback speed, favourites, queue state and Moments.
- Excludes audio files, archive paths, transcripts and research metadata from the migration pack.
- Seals `state.json` with SHA-256 in a versioned manifest and enforces bounded import limits.
- Matches against canonical keys first, then unique broadcast UIDs and an unambiguous show/date/slot fallback.
- Adds a preview window showing proposed merges, duplicate Moments/queue items, protected newer server progress and unmatched identities.
- Creates a full SQLite backup before applying the guarded transaction.
- Preserves completed or newer server playback state, merges conservative history counters, only adds favourites, deduplicates Moments and appends missing queue entries.
- Writes a provenance-rich post-import JSON report.
- Preserves Alpha 3 remote playback/write-through, database schema 45 and LAN capability generation 10.

# 0.30.0-alpha3-remote-playback-write-through-buildfix1

- Fixes CS9202 when building with the intended .NET 8 / C# 12 toolchain.
- Replaces the `Span<byte>` local inside the async loopback media-proxy request reader with a C# 12-compatible byte-array delimiter scan.
- Does not change LAN playback, write-through, API contracts, capability generation or database schema.

# v0.30.0 Alpha 3 — Remote Playback & Write-Through

- Adds certificate-pinned canonical playback from the server to a paired Radio Vault remote client.
- Adds a loopback-only authenticated media bridge so WPF MediaPlayer can use HTTP range requests without exposing the remote-client token in URLs.
- Supports canonical multipart transitions, seeking across the shared timeline, resume, ±30-second skip and playback speed.
- Synchronises active LAN-client progress, including deliberate backward seeks, to the server library.
- Adds favourites, listened/unlistened state, queue add, queue remove and queue clear write-through.
- Adds a compact title-bar server-status indicator with connected, connecting, offline and attention-required states.
- Preserves the remote client's local library and database as an untouched fallback.
- Database schema remains 45; capability generation advances to 10.

# 0.30.0-alpha3-remote-playback-write-through

- Promotes the accepted Alpha 2 read-only server Library browser into the first interactive LAN remote client.
- Retains all Alpha 1 discovery, pairing, trust, revocation and reconnect fixes and all Alpha 2 bounded catalogue paging.
- Preserves web-shell generation 10, Anywhere shell cache v33, IndexedDB v2 and v1 audio/artwork cache identities.

# v0.30.0 Alpha 2 — Remote Library Cutover

- Adds the first authenticated remote-client Library cutover against the paired Radio Vault server.
- Provides a native WPF server-Library browser with bounded 80-broadcast paging, search, refresh, listening status and progress.
- Uses the saved certificate pin and per-client credential for every catalogue request.
- Preserves the local library and database as a safe fallback; this pass is read-only and does not yet redirect playback or mutations.
- Retains all Alpha 1 discovery, pairing, restart, revocation and recovery fixes.
- Database schema remains 45; capability generation advances to 9.

# 0.30.0-alpha2-remote-library-cutover

- Fixes the HTTP 500 returned by the first authenticated remote-client verification after pairing.
- Adds `/api/v1/federation/bootstrap`, a lightweight authenticated trust snapshot containing server identity, canonical library counts and a bounded queue count.
- Changes the remote-client probe to use the federation bootstrap instead of the full Anywhere dashboard bootstrap.
- Adds structured API error envelopes with short diagnostic IDs for both federation and full bootstrap failures.
- Makes canonical web episode snapshots tolerate duplicate representative IDs rather than failing during dictionary construction.
- Adds a readable discovered-server `ToString()` fallback.
- Preserves Buildfix 1 discovery, Buildfix 2 pairing transport, schema 45, capability generation 8 and all Anywhere cache identities.

# 0.30.0-alpha1-lan-federation-buildfix2

- Fixes server HTTP 400 responses during remote-client pairing.
- Serialises the pairing request before sending it so the request has an explicit UTF-8 byte length.
- Adds standards-compliant chunked request-body decoding to the built-in web server for compatibility with .NET and future clients.
- Returns structured pairing errors with specific recovery guidance instead of only a numeric status code.
- Adds a raw TCP regression test proving chunked JSON mutations are decoded correctly.
- Preserves Buildfix 1 discovery and Connected Access preference fixes without changing schema 45 or capability generation 8.

# 0.30.0-alpha1-lan-federation-buildfix1

- Fixes Connected Access checkboxes reverting after leaving the page by saving each change immediately.
- Keeps HTTPS and server federation settings internally consistent: enabling federation enables HTTPS, while disabling HTTPS disables federation.
- Replaces default-route-only multicast with adapter-aware multicast on every active private IPv4 interface.
- Adds directed subnet broadcasts so discovery also works on home networks that suppress multicast.
- Extends discovery listening to eight seconds and reports the actual local adapters used when no server is found.
- Adds deterministic subnet-broadcast tests without changing schema 45, capability generation 8, or Anywhere cache identities.

# 0.30.0-alpha1-lan-federation-foundation

- Begins Multi-Device Library Access with one Radio Vault library server.
- Adds privacy-safe `radiovault-lan-v1` multicast discovery on a configurable UDP port.
- Adds short-lived six-digit pairing codes with expiry, attempt limits and one-time consumption.
- Issues separate persistent 256-bit credentials to paired remote clients.
- Adds header-token authentication without exposing remote-client credentials in URLs.
- Adds certificate-pinned HTTPS discovery, pairing and bootstrap verification on the remote client.
- Adds Connected access settings for server naming, discovery, pairing, revocation, saved connections and connection testing.
- Exposes authenticated federation status and marks LAN remote-client capabilities available when enabled.
- Preserves database schema 45, Anywhere API v1, web-shell generation 10, shell cache v33, IndexedDB v2 and v1 downloaded-media caches.
- Advances capability generation to 8.

# 0.29.0

- Promotes the successfully built and user-accepted RC1 implementation to the stable Radio Vault Anywhere release.
- Introduces no feature, API-contract or database-schema changes from RC1.
- Advances only the secure application-shell cache from v32 to v33 so installed clients replace release-candidate HTML without clearing downloaded audio, artwork, IndexedDB state or pending synchronisation changes.
- Adds final release notes, acceptance guidance and final-package validation rules.
- Preserves database schema 45, capability generation 7, web-shell generation 10, IndexedDB v2 and the v1 audio/artwork caches.

# 0.29.0-rc1-anywhere-release-candidate

- Freezes the accepted Beta 2 Anywhere feature set for release-candidate validation.
- Adds visible app-shell update readiness and a reload path that preserves downloaded audio, artwork, IndexedDB state and pending sync changes.
- Adds a scoped **Repair app shell** diagnostic action that removes only Radio Vault shell caches and service-worker registrations, never device media or offline journals.
- Expands privacy-safe diagnostics with service-worker control, shell-cache identity and viewport information.
- Adds a skip-to-content link, stronger focus-visible styling, dialog semantics, focus restoration and Escape-key dismissal for mobile overlays.
- Adds RC1 source validation, smoke tests, release notes and an explicit server/mobile acceptance checklist.
- Advances capability generation to 7 and the secure shell cache to v32 while preserving schema 45, IndexedDB v2 and the v1 audio/artwork caches.

# 0.29.0-beta2-soak-hardening-performance

- Pages the canonical Library in bounded 80-broadcast batches, returning total counts and a Load more action instead of rendering hundreds of cards in one frame.
- Adds retry-safe mutation identifiers and bounded server deduplication for offline favourites, listening status, queue additions and progress replay.
- Retains failed sync records with exponential backoff or an explicit blocked state instead of silently dropping them; the sync sheet can retry or discard blocked changes.
- Audits downloaded audio cache entries, rebuilds missing cache responses from IndexedDB, identifies downloads requiring re-download, and exposes a Check downloads action.
- Prevents duplicate queue additions, including retries that cross a server restart.
- Expands privacy-safe diagnostics with page, repair, pending and blocked-sync counts.
- Advances capability generation to 6 and the secure shell cache to v31 while preserving schema 45 and the v1 audio/artwork caches.

# 0.29.0-beta1-anywhere-integration-hardening

- Freezes major Anywhere feature development for integration hardening.
- Adds downloadable, structured, privacy-safe Anywhere diagnostics with connectivity, capability, navigation, sync, playback and bounded performance information.
- Preserves copyable diagnostics and reconnect actions while adding a one-tap JSON export for real-device fault reports.
- Advances capability generation to 5 and the secure application shell to v30 without changing audio or artwork caches.
- Retains database schema 45 and all accepted Alpha 4 functionality.

# 0.29.0-alpha4-moments-transcripts-web-parity

- Added versioned timed-transcript access for canonical broadcasts.
- Added transcript presentation in Broadcast Info with speaker labels and tap-to-seek timestamps.
- Added canonical Moment creation from the current playback position.
- Added Moment deletion contracts for future management UI.
- Advertised `moments.write` and `transcripts.read` capabilities.
- Advanced capability generation to 4 and the secure shell cache to v29.
- Preserved schema 45 and the v1 offline audio/artwork caches.
