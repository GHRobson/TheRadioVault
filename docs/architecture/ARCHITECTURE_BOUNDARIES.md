# Radio Vault architecture boundaries

## Dependency direction

`TheRadioVault.Core` is the innermost domain and policy layer. `TheRadioVault.Application` defines use-case and UI/platform ports and may depend only on Core. Data, Media and feature implementations remain outside those contracts. User interfaces and operating-system adapters depend inward; inner projects never depend on a UI toolkit.

## Projects

| Project | Role | Platform policy |
|---|---|---|
| `TheRadioVault.Core` | Domain models, parsing, identity and policy | Neutral |
| `TheRadioVault.Protocol` | Additive remote-client contracts shared by server and native clients | Neutral; Core only |
| `TheRadioVault.Application` | Use-case coordinators, composition primitives and UI/platform ports | Neutral; Core only |
| `TheRadioVault.Data` | SQLite persistence primitives | Neutral |
| `TheRadioVault.Media` | Media metadata and playback-neutral helpers | Neutral |
| `TheRadioVault.Services` | Application/service implementations | Neutral |
| `TheRadioVault.Research` | Research contracts and processing | Neutral |
| `TheRadioVault.Web` | Web/LAN contracts and server | Neutral |
| `TheRadioVault.Transcription` | Transcript services | Neutral |
| `TheRadioVault.Platform.Windows` | Windows/WPF implementations of application ports | Windows-only |
| `TheRadioVault` | Current WPF composition root and presentation | Windows-only |
| `TheRadioVault.Tests` | Cross-platform capability/regression console suite | Neutral |
| `TheRadioVault.Web.Tests` | Compiled Web query, HTTP/server, embedded-asset and playback-transfer behaviour | Neutral; Web only |
| `TheRadioVault.Data.Tests` | Compiled database seed, schema, upgrade and migration behaviour | Neutral; Core and Data only |
| `TheRadioVault.Transcription.Tests` | Compiled transcription download, timeout, cleanup and asset-installation behaviour | Neutral; Transcription only |
| `TheRadioVault.SourceChecks` | Dependency-free source, route-order and packaging boundary checks | Neutral |

## Rules

1. Neutral projects must not reference `System.Windows`, Windows Forms, `Microsoft.Win32`, PresentationFramework or WindowsBase.
2. Neutral projects must not reference either Windows project.
3. Application contracts must not expose WPF controls, windows, dispatcher types, database connections or concrete services.
4. Windows adapters may depend on Core/Application but not Data, Services, Research, Web or Transcription.
5. The current WPF project remains the composition root, but orchestration policy and ordered workflows must move into Application services before presentation is replaced.
6. New cross-platform behaviour belongs behind Application/Core contracts before it is connected to the current or future Avalonia UI.

These rules are executable in `tools/Test-Architecture.ps1` and are called by `validate-source.ps1` and `release-gate.ps1`.


## Alpha 2 live boundary

The WPF shell resolves startup, shutdown and replacement-window transition coordinators from the application composition registry. Concrete WPF cleanup actions remain at the presentation edge while sequencing and failure isolation are platform neutral.

## Alpha 3 live boundary

Windows-specific implementations of dispatcher, notifications, file selection, clipboard, external launching, system appearance, screen bounds and lifetime are registered only by `TheRadioVault.Platform.Windows`. The WPF project may still contain WPF presentation and specialised modal dialogs, but it may not directly call `Process.Start`, WPF Clipboard, Windows Registry or `SystemParameters`; `tools/Test-Architecture.ps1` enforces this. Common archive-folder, backup, metadata and transcription selection workflows now consume `IFileSelectionService`, and every current clipboard action consumes `IClipboardService`.


## Alpha 4 live playback boundary

The WPF composition root selects either the Windows local-media adapter or the certificate-pinned LAN adapter and gives it to `PlaybackSessionCoordinator`. From that point, `MainWindow` consumes playback state and commands only through the Application coordinator. `PlaybackProgressCoordinator` and `PlaybackCompletionCoordinator` own persistence-safety and natural-completion policy. The ordinary WPF `MediaPlayer` implementation is isolated in `TheRadioVault.Platform.Windows`; presentation may not reintroduce a concrete local playback engine or direct `_playback` command path.

## Alpha 5 live LAN/shared-session boundary

`RemoteLibrarySessionCoordinator` is the platform-neutral owner of remote Library synchronization state. It owns the cursor, duplicate-request suppression, request timeout/cancellation lifetime, reconnect backoff, live/cache/unavailable state and copied diagnostics. The WPF shell still applies returned server data to current controls and the transport remains certificate-pinned in the LAN adapter, but presentation no longer owns a separate semaphore, cancellation source, cursor, retry schedule or failure counter. Cached startup, timer polling, manual reconnect and orderly shutdown all use the same session lifecycle.


## Alpha 6 composition boundary

The composition root must register every required Application and platform service before the first window opens, create a composition report, and freeze the registry. Runtime registration mutation, duplicate service registration and cyclic factory resolution are rejected. `TheRadioVault` may select between local and LAN playback at the outer composition edge, but local media engines must come from `ILocalPlaybackEngineFactory` and application playback sessions must come from `PlaybackSessionFactory`. The WPF project may not directly construct `WpfMediaPlaybackEngine` or `PlaybackSessionCoordinator`. Shared remote Library sessions must participate in the ordered shutdown lifecycle.

## Beta 1 WPF-independence proof

Beta 1 freezes the Alpha 6 Buildfix 1 runtime feature set and treats the current WPF project as an intentional, replaceable composition/presentation boundary. `tools/Test-WpfIndependence.ps1` proves that neutral projects remain Windows-free; Application depends only on Core; startup, shutdown, mode transitions, playback, progress/completion policy, remote synchronization and platform operations remain behind reusable seams; and presentation code cannot reintroduce concrete local media construction, direct playback-engine commands, legacy remote synchronization ownership or direct shell/clipboard/Registry access.

The proof does not claim that WPF views have disappeared. Remaining windows, XAML, dispatcher usage, visual resources, navigation and feature-specific presentation orchestration are explicitly recorded as Avalonia implementation work. They are acceptable only inside the two intentional Windows boundaries and must not leak inward.

## 0.44 mobile playback ownership boundary

`MobileClientSession` remains the mobile client façade and owns UI projection plus high-level workflow sequencing. `MobilePlaybackOwnershipCoordinator` owns the pure rules that interpret a shared playback session: active ownership, committed moves, source-stop evidence and the two-sample safeguard for legacy foreign owners. `MobilePlaybackTimeline` owns the side-effect-free mapping between multipart media, the logical broadcast playhead and decoder-relative positions, including settling protection and completion tolerance. `MobilePlaybackSynchronizationCoordinator` owns remote-summary observation, deterministic remote-playhead projection and the decoder-stop/server-acknowledgement side effects required after a committed move. Its narrow transport and playback-engine boundaries allow those workflows to be tested without a live server or device. These rules must not be duplicated in the iOS view layer or folded back into the session façade.

`MobileMetadataSynchronizationCoordinator` owns the serialized server change-feed workflow: cursor/revision requests, complete-library reset paging, changed/deleted broadcast resolution, durable cache application, synchronization activity and successful-sync time. The session supplies the ordered post-cache reconciliation callback for offline progress, downloads, Explore and UI projection; that callback runs inside the same serialization boundary. Library queries, Explore and download policy live in their own coordinators.

`MobileOfflineMutationSynchronizationCoordinator` owns durable queued changes for favourites, listened state and Moments. It serializes replay, preserves queue order, accepts duplicate Moments as successful idempotent writes, recognizes changes that the server already applied after an interrupted response, records the first unresolved failure and leaves later changes pending. `MobileClientSession` projects accepted canonical summaries into its cached UI and downloaded-broadcast state; it must not duplicate replay, retry or conflict-detection policy.

`MobileDownloadCoordinator` owns transfer lifecycle and presentation state for downloads, including Wi-Fi eligibility, automatic candidate selection, pause/resume/cancel, storage-limit trimming, completed-item cleanup, repair, protected active media and projection of the durable download index. `MobileDownloadService` remains the filesystem implementation behind its narrow store boundary. The mobile session decides whether the wider paired/playback workflow permits an operation and delegates the download operation itself.

`MobileDownloadedProgressSynchronizationCoordinator` owns the server-authority rules for progress captured while playing downloaded media. It advances the server only from newer offline evidence, preserves conflicts, acknowledges pending play counts only after accepted writes and reconciles the returned canonical summary into the download index. These rules must not be recreated in playback polling or iOS controllers.

`MobileExploreQueryCoordinator` owns cache-first Explore dashboard and article reads, serialized catalogue warming, document refresh and image hydration. Its transport boundary contains every Explore server query while `MobileMetadataCache` remains the durable store. The mobile session may sequence a refresh and project status, but it must not rebuild Explore dashboards, fetch page images or introduce a second cache-warming gate.

`MobileKnowledgeQueryCoordinator` owns live and cached Knowledge snapshots, deterministic Library-derived fallback coverage and date-review decisions. It preserves usable Knowledge and coverage when the server is offline or predates the Knowledge service, and only sends a proposed date for an explicit acceptance decision. iOS controllers and the session façade consume these results; they must not reimplement Knowledge fallback scoring or triage request rules.

`MobilePairingCoordinator` owns discovered-server state and the success/failure transitions for discovery, trusted pairing, manual pairing and forgetting a relationship. `MobileServerClient` remains the TLS-pinning and credential-store implementation behind the transport. The session owns global busy presentation, post-pair Library refresh, tab navigation and teardown of playback, downloads and cached UI state.

`MobileLibraryQueryCoordinator` owns deterministic catalogue projection from `MobileMetadataCache`, cache-first filtering, normalized duplicate-show identities, archive-period aggregation and the equivalent live browse/facet/suggestion contracts. It returns data and status without selecting a screen or mutating UI busy state. Library controllers and the session must not recreate collection-name normalization, progress aggregation or separate cached/live query branches.

## 0.44 web playback and queue route boundary

`LocalWebServer` remains the HTTP listener and request-lifecycle coordinator. It owns authentication, pairing-before-authentication, secure setup/static-shell handling and shared response helpers. `LocalWebServer.ApiRoutes.cs` owns the ordered relationship between authenticated route families and the final API/media fallbacks. `LocalWebServer.PlaybackQueue.cs` owns the contiguous player-transfer, playback-command, player-state and queue route family, including its HTTP method checks, queue action matcher and response handlers. The authenticated dispatcher reaches this family through exactly one `TryHandlePlaybackQueueRouteAsync` call.

Route order and observable protocol behaviour are compatibility boundaries. Moving a handler must not rename a route, widen an allowed method, change a status code or reorder the family relative to broadcast details, archive health, Moments, artwork and audio. Playback or queue routes must not be added back inline to `LocalWebServer.cs`; extend the focused partial instead.

## 0.44 federation administration route boundary

`LocalWebServer.FederationAdministration.cs` owns the authenticated federation status/bootstrap, Library synchronization and scanning, parity, settings, playback preferences, Research workspace/coverage/date review, Research-pack administration and Wiki-pack administration route family. The main dispatcher reaches this ordered family through exactly one `TryHandleFederationAdministrationRouteAsync` call.

Desktop pairing remains before authorization in the main dispatcher, because it establishes the credential used by the federation family. The normal client bootstrap and client-operation APIs remain after the federation boundary. Moving or extending this route family must preserve that security order, every HTTP method rule, status code and handler contract.

## 0.44 web asset, client and media boundaries

The web client, secure setup page and service worker live as embedded resources under `TheRadioVault.Web/Assets`. `LocalWebServer.WebAssets.cs` is the single loader for those resources. Keeping them embedded preserves the self-contained server deployment while allowing HTML, CSS and JavaScript to be inspected and edited without expanding the HTTP coordinator. Their served content is preserved from the former literals, and a compiled-resource regression test verifies that all three are present and loadable.

`LocalWebServer.ClientRoutes.cs` owns the complete client-facing bootstrap, Research, transcription, Wiki and Library query route family. The authenticated dispatcher reaches it through one ordered `TryHandleClientRouteAsync` call. `LocalWebServer.Media.cs` owns artwork, canonical multipart media, positioned WAV, legacy media and HTTP range streaming. It exposes separate canonical-media and artwork/audio boundary calls because those route groups occupy different compatibility positions in the authenticated dispatcher.

The client boundary call and two media boundary calls must retain their relative dispatcher order. Routes, method checks, status codes, range semantics and handler bodies remain protocol contracts. New client or media routing belongs in the focused partials, and large web assets must not be embedded back into `LocalWebServer.cs`.

## 0.44 general Web API dispatch boundary

`WebApiRouteResolver` is the pure, declarative owner of general API route recognition, legacy aliases, captured broadcast/Moment/job identifiers and route-specific HTTP method policy. `LocalWebServer.ApiRoutes.cs` applies those matches to the existing handlers and coordinates the authenticated federation, client, general API, canonical-media, playback/queue and artwork/audio boundaries. `LocalWebServer.cs` reaches that surface through exactly one `TryHandleAuthorizedRouteAsync` call after pairing, authorization, secure setup and static-shell handling.

The resolver must not consume specialised client, playback, queue or media paths. Adding or changing a general route requires focused resolver coverage plus a live HTTP compatibility check where observable behaviour changes. Method policy, status text and legacy `/api/episodes` and `/api/shows` aliases are protocol contracts; they must not be recreated as inline conditionals in the listener.

## 0.44 desktop playback state boundary

`DesktopPlaybackStateMachine` is the platform-neutral owner of loaded, playing, busy, remote-owner, pending-transport, desired-playback and user-intent transitions for desktop playback. `PlaybackViewModel` projects that state into Avalonia bindings and remains responsible for decoder, persistence, dispatcher and handoff-service side effects; it must not recreate parallel transport flags.

`RemotePlaybackProgressInterpolator` owns the monotonic projection rule between authoritative remote heartbeats. Corrections of up to three seconds within the same broadcast, owner and ownership generation are treated as network lag; a larger backwards correction, a new owner, a new generation or a new broadcast establishes a fresh baseline so real seeks remain visible.

`PlaybackStartupCoordinator` is the shared desktop/mobile startup boundary. Every explicit selection supersedes the previous pending selection, but decoder access remains serialised until the older attempt has released it. Readiness has a bounded deadline and reports caller cancellation, supersession, unavailable media and decoder timeout as different outcomes. Desktop startup invalidates late decoder work by generation. Mobile handoff must observe native `ReadyToPlay` before it reports the target ready or commits ownership; a failed or timed-out target cancels the transfer so the source remains authoritative.

## 0.44 test-runner boundary

`TheRadioVault.Tests` remains the broad capability and regression runner while those tests are migrated by subsystem. `TheRadioVault.Web.Tests` references only `TheRadioVault.Web` and owns Web query/model contracts, declarative route resolution, canonical routes, live HTTP behavior, server lifecycle, embedded assets and transactional playback integration. Its 93 checks use focused API, routing, infrastructure, playback, shell and media groups plus reusable server, source-tree and archive-provider fixtures. `TheRadioVault.Data.Tests` references only Core and Data and owns database seed, latest-schema, legacy-upgrade, pre-upgrade backup and numbered-migration behavior through eight compiled checks with reusable declarative schema assertions. `TheRadioVault.Transcription.Tests` references the Transcription subsystem and owns official-asset installation, network progress, worker activity, timeout, cancellation, process-tree termination, cleanup and retry behavior through twelve compiled checks. All three focused suites are release-gated. Migrated tests must be removed from the broad runner rather than duplicated.

## 0.44 SQLite migration boundary

`SqliteDatabase` remains the sole database connection and initialization owner. Its existing schema-47 bootstrap is preserved for every historical database. After that baseline, `SqliteMigrationCatalog` is the only registry for forward schema changes: versions must be contiguous, and `SqliteMigrationRunner` applies each `ISqliteMigration` in its own transaction together with the migration-history row and `PRAGMA user_version` update. A failed migration therefore leaves neither its schema writes nor its version marker behind. Databases created by a newer Radio Vault build are rejected before legacy initialization writes. Schema 48 creates the durable `schema_migrations` ledger and establishes this boundary for all future changes.

`TheRadioVault.SourceChecks` is the dependency-free home for deliberate source-text, route-order, packaging and presentation-marker inspections. A test that can exercise compiled behavior must not be added to SourceChecks merely because text inspection is easier.

## 0.44 transcription download boundary

`WhisperDownloadService` keeps the `HttpClient` total timeout disabled because official model downloads can legitimately take a long time. `WhisperDownloadPolicy` instead sets a renewable inactivity deadline for response headers and every streamed read. Any received data starts a fresh deadline, so an active download may run for hours without being mistaken for a timeout. A stalled transfer raises `WhisperDownloadTimeoutException` with retry guidance, while caller cancellation remains `OperationCanceledException`. Temporary worker archives and model files must remain hidden behind `.download` names and be deleted after cancellation, timeout or verification failure.

The dedicated Transcription runner proves that long active transfers succeed, header and body stalls time out, caller cancellation stays distinct, incomplete files are removed, retries succeed and official assets still install safely. This policy must be reused rather than replaced with an arbitrary whole-download timeout.

`TranscriptionWorkerProcessRunner` is the sole owner of the live `whisper.cpp` process lifecycle. Standard-output and standard-error lines renew `WhisperWorkerPolicy`'s ten-minute inactivity deadline, while the absence of activity stops the entire process tree and raises `WhisperWorkerTimeoutException`. Caller cancellation uses the same process-tree cleanup but remains `OperationCanceledException`. The runner reports the process id to `WhisperCppTranscriptionEngine` so existing pause/resume behavior remains available only while that operation is active. The engine owns command arguments, progress interpretation, diagnostic tails, transcript parsing and workspace cleanup; it must not reintroduce direct process waiting or cancellation registration.

## 0.44 web HTTP infrastructure boundary

`WebHttpRequestReader` owns HTTP/1.x request framing, bounded header and body reads, fixed-length and chunked transfer decoding, framing validation and request timeouts. `LocalWebServer` supplies the route-sensitive body-size and timeout policy, then translates the reader's explicit malformed, timeout, header-limit and body-limit outcomes into HTTP responses. The reader rejects ambiguous `Content-Length` plus `Transfer-Encoding` requests and oversized payloads before allocating their declared body size.

`WebHttpResponseWriter` owns common response framing, security headers, HEAD behavior and redirect sanitisation. `LocalWebServer` retains small forwarding helpers so focused route partials do not depend directly on infrastructure implementation details. Request parsing or common response framing must not be added back to the server coordinator. Live-server fixtures bind port zero and read the OS-assigned port from `LocalWebServer.Port`; tests must not reintroduce a probe-and-release free-port race.

Large Research and Wiki package routes opt into staged request bodies. `WebHttpRequestReader` copies fixed-length and chunked bodies directly to a uniquely named temporary file, renews the inactivity deadline after every network read, and gives the resulting request sole cleanup ownership. Route handlers open a read-only stream over that file; archive providers must hash and import that stream without recreating a whole-package `byte[]`. A timeout, malformed chunk, size-limit failure, handler completion or handler failure must remove the staged file. Ordinary small JSON requests continue to use bounded in-memory bodies.

## 0.44 personal-state persistence boundary

`DatabaseService` remains a compatibility façade for desktop and server callers, but it does not own playback-state or favourite SQL. It resolves the episode members that share one canonical broadcast and delegates them to `PersonalStateRepository`. The repository reads the aggregate playback state and writes every canonical member inside the same SQLite transaction, including the matching `episodes.status` projection. Completion, explicit reset, play/completion counts, playback speed and favourite mutations must not be copied back into the façade.

`SqliteDatabase` remains the only connection, initialization and migration owner. `PersonalStateRepository` may open connections through it, but it may not initialize or migrate a database. A failure on any canonical member must roll back playback rows and episode projections for all members; the cross-platform smoke suite exercises that forced-failure path.

All four shared runners are required by the complete Windows release gate. The local macOS gate and hosted macOS and Linux jobs run the complete Data, Transcription and Web suites; the iOS job runs the portable transactional subset alongside its device architecture checks. Each runner supports optional name filters so platform workflows can select an intentional subset without coupling source inspection back to product dependencies.

## 0.43 durable sync and backup boundary

`MobileOfflineMutationStore` owns stable IDs for queued favourite, listening-state and Moment decisions. Those IDs must survive restarts and must be sent unchanged on every retry. `WebMutationLedger` is the server acknowledgement boundary: it combines device identity with mutation identity, persists the bounded acknowledgement ledger atomically and exposes per-device counts and latest acknowledgement time. A retried mutation must not be interpreted as a new decision.

`ScheduledBackupService` owns the daily eligibility check, single-flight execution and archive verification policy. Timer callbacks may report errors but must not escape unobserved failures. Server administration observes its immutable status contract; it must not start another backup path or infer health from filesystem timestamps independently.

## 0.45 archive entity-link boundary

`ArchiveEntityLink` is the neutral identity and navigation contract for articles, shows, broadcasts, people, topics, images and timelines. `EntityId` is canonical identity, `TargetId` is the actionable platform value, `Relationship` describes context such as host or guest, and `Route` is the deterministic `radiovault://entity/...` representation. Labels are presentation only and must never become identity.

Broadcast Info and Explore documents expose this contract additively while preserving existing protocol fields. New Library, Explore, Knowledge and transcript-search navigation must consume these links or extend the factory; it must not introduce another string-only deep-link format.
