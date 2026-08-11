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

- Formal conflict rules for progress, listened/unlistened, favourites, Moments and queue changes.
- Durable mutation ids and acknowledgements for every offline write.
- Per-device sync checkpoint, pending-change count and retry reason.
- Smooth remote playhead interpolation without desktop wiggle.
- Scheduled backups, integrity checks and a guided restore rehearsal.
- Server health dashboard covering database, storage, certificates, clients and media roots.
- Structured handoff/sync diagnostics with secret redaction.
- Multi-device tests for simultaneous online/offline edits and manual overrides.

Exit criteria: conflict suites pass, a backup restores onto a clean server, and stale progress cannot resurrect an old state.

## 0.44 — Architecture and testability

Goal: reduce the cost and risk of every later feature without rewriting working behaviour.

Status: in progress on `refactor/mobile-session-coordinators`. Mobile playback ownership, handoff evidence, multipart timeline mapping, remote-playhead observation, committed source-stop acknowledgement, library metadata synchronization, offline mutation replay, download lifecycle, downloaded-progress reconciliation, Explore, Knowledge, pairing and Library query/projection rules have been extracted from `MobileClientSession` into focused, behaviour-tested components while retaining the session as the public façade.

- Keep `MobileClientSession` as the public high-level orchestrator; pairing, Library, sync, playback, download, Explore and Knowledge policy boundaries are now extracted.
- Split `LocalWebServer` into static assets and grouped route handlers.
- Introduce an explicit desktop playback state machine and progress interpolator.
- Move behavioural checks from the 9,000-line smoke runner into subsystem test projects.
- Separate future SQLite migrations into numbered migration objects with fixtures.
- Add cancellation/timeout policy at external-process, network and media boundaries.
- Archive superseded current documents and establish a small living documentation index.

Exit criteria: protocol and behaviour remain compatible, the largest coordinators shrink materially, and extracted components have focused tests.

## 0.45 — Explore, Knowledge and archive discovery

Goal: turn the collection into a genuinely connected radio encyclopaedia.

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
- Per-device automatic-download and expiry policies.
- Exportable Moment citations and notes.
- Windows signed installers and upgrade/downgrade validation.
- macOS Developer ID signing, notarisation and hardened-runtime validation.
- Linux systemd server service and wider distribution testing.
- Server start-at-login/service controls on each platform.
- Safe update notifications with explicit user control.

Exit criteria: installers are signed where appropriate, upgrades preserve data, and saved collections/queues work across devices.

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
