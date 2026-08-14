# Radio Vault release roadmap

This roadmap is ordered by dependency and risk rather than fixed dates. Release numbers describe useful product milestones; individual items may move when physical-device testing uncovers a more urgent reliability issue.

## 0.41 — Local development and daily-driver hardening

Goal: continue dependable development without relying on GitHub Actions minutes and make the current iOS/Mac experience safe to test every day.

- One-command Mac/iOS local release gate with saved logs.
- Local Mac Client and Server ZIP/DMG installers without PowerShell.
- One authoritative 0.41.0 product version across Client, Server and iOS bundles.
- Coalesced, main-thread-safe iOS Lock Screen metadata and remote commands.
- Physical-device checks for controls, interruptions, headset removal and repeated handoff.
- Regression coverage for the iPhone-to-Mac handoff and offline progress conflict.
- Keep work on the feature branch until phone and Mac acceptance testing passes.

Exit criteria: the local release gate passes, Mac installers open, a fresh iPhone build works online and offline, and repeated two-way handoff succeeds.

## 0.42 — iOS polish, accessibility and TestFlight readiness

Goal: make the phone client feel complete outside a development setup.

- Lock Screen, Control Centre, headset and Bluetooth acceptance matrix.
- Dynamic Type, VoiceOver order/labels, contrast, reduced motion and large-text layouts.
- iPad adaptive split-view layout.
- AirPlay route picker and clearer output-device state.
- Privacy-safe diagnostics export from Settings.
- Background reconnect and offline-download soak testing.
- TestFlight archive, signing, privacy text and tester release notes.
- Finish icon/design-token parity without replacing useful iOS conventions.

Exit criteria: no critical accessibility failures, a 24-hour offline/reconnect soak passes, and an external TestFlight build installs without a developer cable.

## 0.43 — Sync, progress and server resilience

Goal: make multi-device state boringly reliable.

Status: implementation complete, physical soak and clean-machine release validation pending. Offline favourite, listened/unlistened and Moment writes retain stable mutation IDs and captured decision times through mobile replay. The server durably records per-device acknowledgements and accepted decisions, rejects stale or future-skewed changes, and exposes device checkpoints. Scheduled backups now undergo an isolated restore rehearsal with archive limits, SQLite integrity, foreign-key and schema checks before live restore; failed live validation rolls back automatically. The server UI reports database, storage, media-root, certificate, client and backup health and exports a privacy-safe diagnostic report with paths, tokens and client identities redacted. A separate signed, rehearsed media-consolidation workflow can build a logical archive from Library Truth while retaining every original and rejected duplicate in a recoverable quarantine.

- Formal conflict rules for progress, listened/unlistened, favourites, Moments and queue changes.
- Durable mutation ids and acknowledgements for every offline write.
- Per-device sync checkpoint, pending-change count and retry reason.
- Smooth remote playhead interpolation without desktop wiggle.
- Scheduled backups, integrity checks and a guided restore rehearsal.
- Server health dashboard covering database, storage, certificates, clients and media roots.
- Structured handoff/sync diagnostics with secret redaction.
- Full-hash media consolidation with deterministic longest-runtime/highest-bitrate selection, no-delete quarantine, restart recovery and legacy file-identity audit.
- Multi-device tests for simultaneous online/offline edits and manual overrides.

Exit criteria: conflict suites pass, a backup restores onto a clean server, and stale progress cannot resurrect an old state.

## 0.44 — Architecture and testability

Goal: reduce the cost and risk of every later feature without rewriting working behaviour.

Status: in progress. Mobile playback ownership, handoff evidence, multipart timeline mapping, remote-playhead observation, committed source-stop acknowledgement, library metadata synchronization, offline mutation replay, download lifecycle, downloaded-progress reconciliation, Explore, Knowledge, pairing and Library query/projection rules have been extracted from `MobileClientSession` into focused, behaviour-tested components while retaining the session as the public façade. The web-server split now has focused player/playback/queue, authenticated federation/administration, client API and media route boundaries, with its web client, secure setup page and service worker moved into embedded resources. General API route recognition, identifier capture and method policy are now declarative and separate from the listener; bounded HTTP request reading and response framing are dedicated components with explicit timeout, malformed-framing and size-limit behavior. Desktop transport state and remote playhead smoothing now live in focused, platform-neutral Application services. The dependency-free source-check runner owns eleven intentional source/packaging checks, the compiled Web runner owns 93 focused checks, the Data runner owns eight seed, schema, upgrade, backup and numbered-migration checks, and the Transcription runner owns twelve official-download, worker-activity, inactivity-timeout, process-tree, cancellation, cleanup and retry checks. Schema 48 establishes the transactional numbered-migration boundary without rewriting the historical schema-47 bootstrap.

- Keep `MobileClientSession` as the public high-level orchestrator; pairing, Library, sync, playback, download, Explore and Knowledge policy boundaries are now extracted.
- Continue splitting `LocalWebServer`; playback/queue, federation/administration, client and media route groups, static web assets, HTTP request/response infrastructure, and general API dispatch are extracted. The next extraction should target a coherent request-lifecycle or handler family backed by compiled behaviour tests, not merely move methods into another file.
- Keep the explicit desktop playback state machine and remote-progress interpolator as the single pure-policy owners while the view model retains UI and side effects.
- Continue moving coherent behavioral groups from the remaining 7,033-line smoke runner into subsystem test projects; Web HTTP/server behavior and the first database seed/schema/upgrade group are now independently release-gated, and each migrated check must remain in exactly one runner.
- Keep every future SQLite change in the schema-48 numbered-migration boundary with order, idempotence, rollback and restorable pre-upgrade backup coverage; do not add new schema changes to the legacy bootstrap.
- Continue cancellation/timeout policy at remaining network and media boundaries; transcription downloads and the external Whisper worker now use renewable activity deadlines with distinct caller cancellation, process-tree termination and partial-workspace cleanup.
- Desktop and iOS playback startup now share a superseding, serialised request boundary with bounded decoder readiness. Rapid selection changes cancel stale preparation, iOS waits for native AVPlayer readiness before handoff commit, and unavailable media remains distinct from a decoder timeout.
- Archive superseded current documents and establish a small living documentation index.

Exit criteria: protocol and behaviour remain compatible, the largest coordinators shrink materially, and extracted components have focused tests.

## 0.45 — Explore, Knowledge and archive discovery

Goal: turn the collection into a genuinely connected radio encyclopaedia.

Status: in progress. Core owns one typed entity link and navigation policy for articles, shows, broadcasts, people, topics, images and timelines, including stable identity, an actionable target and a deterministic Radio Vault deep link. Broadcast Info emits these links locally and remotely; desktop and iOS metadata pills consume them while retaining a compatibility fallback for older servers. Library transcript search returns the first matching timed phrase and desktop playback opens directly at that time. Explore prose, complete Knowledge coverage and mobile transcript-search presentation still need to adopt the same contract.

- Consistent Wikipedia-inspired pages on desktop, iOS and web.
- One link model for shows, broadcasts, people, guests, topics, images and timelines.
- Reliable inline links from article prose to entities and broadcasts.
- Coverage heat maps and useful Knowledge triage on iOS.
- Image cache and layout stability across sync refreshes.
- Full-text transcript search with jump-to-time results.
- Show timelines and On This Day from one canonical query layer.
- Import preview and better tools for uncertain dates, duplicates and multi-part recordings.

Exit criteria: the same entity opens consistently from Library, Explore, Knowledge and transcript search on every client.

## 0.46 — Collections, queues and distribution parity

Goal: improve long-term listening and make every desktop platform straightforward to install.

- Smart collections and saved playlists/queues.
- Radio Vault Live: one server-clock station that schedules archive broadcasts by date and historical show slot without changing personal listening progress.
- Server RSS Archive Inbox for automatically collecting new broadcasts from private or public feeds.
- Per-device automatic-download and expiry policies.
- Exportable Moment citations and notes.
- Windows signed installers and upgrade/downgrade validation.
- macOS Developer ID signing, notarisation and hardened-runtime validation.
- Linux systemd server service and wider distribution testing.
- Server start-at-login/service controls on each platform.
- Safe update notifications with explicit user control.

Exit criteria: installers are signed where appropriate, upgrades preserve data, and saved collections/queues work across devices.

Progress: the server-owned, revisioned saved-collection model is implemented through schema 49, with ordered manual playlists, live smart collections, queue snapshots, conflict-safe mutations, desktop and iPhone surfaces, and offline iPhone reads. Schema 50 adds the Server RSS Archive Inbox with encrypted private-feed details, safe first-run baselining, scheduled conditional checks, atomic downloads and duplicate prevention. Schema 51 adds a persistent Radio Vault Live schedule: the server selects context-aware archive programmes once, publishes one clock to every client, and keeps station listening isolated from played status, progress, play counts, queues and handoff ownership. iPhone and desktop clients now have device-local new-broadcast watermarks, automatic downloads, completed/age-based cleanup, least-recently-used storage limits and active-playback protection. Signing, service installation and update notifications remain before 0.46 is complete.

## 0.47 — Secure away-from-home listening

Goal: make remote listening possible without weakening the private-library design.

- Threat model and privacy review before implementation.
- Short-lived device credentials and revocation.
- A secure relay or supported private-network approach, never direct unprotected port forwarding.
- Network-change recovery and download fallback.
- Clear local, remote, syncing and offline states.
- Bandwidth limits and server-owner controls.

Exit criteria: independent security review, credential revocation tests and no pairing token or long-lived media credential in URLs or logs.

## 1.0 — Stable personal archive release

Goal: a release that can be trusted with a real long-lived collection.

- Migration and restore matrix from supported earlier databases.
- Large-library performance budgets and week-long playback/sync soak.
- Complete accessibility pass on every client.
- Security, privacy and dependency review.
- Installer, upgrade, rollback and uninstall verification on Windows, macOS and Linux.
- TestFlight/App Store distribution decision for iOS.
- Accurate user, server administration and disaster-recovery guides.
- No known critical data-loss, playback-ownership or stale-progress defect.

Exit criteria: a release candidate passes on physical Windows, Mac, Linux and iOS hardware, followed by a clean backup/restore rehearsal.

## Parking lot after 1.0

- CarPlay with a deliberately limited, safety-first interface.
- Household profiles if a real multi-user need emerges.
- Additional Linux packaging such as Flatpak.
- Optional external catalogue/metadata integrations.
- Public extension points for importers and library reports.

These come after backup, sync correctness, accessibility and distribution. Radio Vault’s value comes from protecting and making old radio collections enjoyable, not from accumulating integrations.
