# Radio Vault source audit

Date: 11 August 2026
Last updated: 14 August 2026
Original branch audited: `ios/dashboard-library-refresh`
Original baseline commit: `6920c3f`

## Executive summary

Radio Vault has grown into a substantial cross-platform product: 408 C# files and approximately 112,000 lines of C# across the shared library, server, Windows/Mac/Linux desktop client, native iOS client, web client and research tools. The underlying architecture is sound enough to continue developing. The shared protocol, authoritative server, native platform boundaries, transactional handoff work and SQLite safety measures are strong foundations.

The main risk is no longer a missing platform. It is concentration of responsibility. Several files have become small applications in their own right, which makes apparently simple changes harder to reason about and easier to break. The next phase should combine user-facing improvements with deliberate extraction of these large components behind regression-tested interfaces.

No urgent rewrite is recommended. A rewrite would put working playback, pairing, offline sync and handoff at risk. The safer route is staged refactoring, one behavioural boundary at a time.

## What is in good shape

- The repository is the shared source of truth for every platform.
- The Core, Application, Presentation and Protocol projects provide useful boundaries rather than duplicating platform logic.
- The authoritative playback handoff is transactional and has regression coverage for repeated device moves, stale progress and physical source-stop acknowledgement.
- The database enables foreign keys, WAL mode and a busy timeout, and creates a pre-migration backup before schema changes.
- iOS uses native AVFoundation playback, audio-session interruption handling, headphone-removal handling, background audio, Lock Screen metadata and `MPRemoteCommandCenter` controls.
- Windows, macOS and Linux use the same Avalonia desktop application rather than separate client implementations.
- Windows, macOS and Linux all have appropriate installer formats in the build pipeline.
- The native mobile client uses pairing tokens and certificate-thumbprint validation.
- There are 275 registered shared regression/smoke checks plus a dedicated mobile offline and handoff runner.
- No `TODO`, `FIXME`, `HACK` or `NotImplementedException` markers were found in production C# source.

## Fix before a wider release

### 1. Keep all product versions authoritative

The audit found three user-visible iOS version values and a different desktop/server version. This can produce misleading About screens and incompatible release artifacts. `VERSION.txt` is now the release version and the local release gate verifies the desktop, server, iOS project and iOS plist before building.

Future improvement: generate all assembly and bundle versions from one version file rather than checking copied values.

### 2. Make local validation a first-class workflow

GitHub Actions is useful as a final independent check, but development must not stop when hosted minutes are unavailable. `local-release-gate.sh` covers the Mac client, Mac server, shared smoke suite, handoff regressions, mobile regressions and iOS simulator build. `package-macos-local.sh` creates local Client and Server ZIP and DMG installers without PowerShell.

Windows and Linux still require their own operating systems for final platform-specific validation. A successful Mac run must not be presented as proof that Windows or Linux installers were manually tested.

### 3. Harden iOS system-media updates

`IosNowPlayingService` previously decoded artwork outside the main thread and could queue a new main-thread update on every playback poll. It now coalesces updates, creates UIKit/MediaPlayer objects on the main thread, dispatches remote commands on the main thread, and only enables commands that apply to active local playback.

Verify on a physical phone: Lock Screen play/pause/seek, wired and Bluetooth controls, headphone removal, phone/Siri interruptions, repeated two-way handoff, and artwork changes between broadcasts.

### 4. Treat backup and restore as release features

The schema has reached version 48 and the database is one of the most important parts of the application. Migration backup and a verified daily scheduled-backup service now exist. Every candidate backup can be extracted under strict archive limits and rehearsed against an isolated SQLite copy with integrity, foreign-key, schema and content checks before live restore. Live restore retains a pre-restore database and rolls back if post-copy validation fails. A physical clean-server rehearsal remains part of release acceptance.

### 5. Improve release diagnostics

The server now exports a privacy-conscious diagnostic report containing version, platform, sync checkpoints, recent errors, backup state and database/storage/certificate/client/media health. Client identifiers are pseudonymized and tokens, profile paths, recordings and transcript contents are excluded. The next diagnostics work is to include a bounded playback-ownership transition trail without exposing library content.

### 6. Consolidate physical media only through a no-delete boundary

The server now has a distinct `MediaConsolidationService` rather than extending ordinary scanning or tag synchronization with bulk moves. It consumes Library Truth, computes complete file hashes, ranks alternates by runtime then estimated bitrate, rehearses every path and storage requirement, creates a verified pre-change SQLite backup and journals an idempotent commit. Managed copies are verified before all originals are moved to a non-indexed quarantine; the product exposes no quarantine deletion command. Interrupted plans can be recovered after a Server restart.

The related identity audit found and removed an older scanner rule that treated partial hash plus byte length as sufficient to reattach a moved file. Full SHA-256 is now preferred; partial matches also require runtime and remain strong candidates rather than exact identities. Both are constrained to compatible collection/date/slot/part claims so exact audio with conflicting dates remains separate for review. Portable manifests retain their old machine-row `FileKey` for compatibility and add a path-independent `ContentKey`.

## Major refactoring targets

| Priority | Area | Evidence | Safe extraction path |
|---|---|---|---|
| P1 | Web server | `LocalWebServer.cs` began at 10,436 lines and is now 1,465 lines after gaining several later features. Player handoff/playback/queue, federation/administration, client API, media routing, trust/pairing policy and durable mutation acknowledgements live in focused components, while the web client, service worker and secure setup page are embedded resource files. General API recognition and request lifecycle ordering are pure resolvers; canonical Dashboard/archive discovery is a separate projection. Bounded request parsing and response framing are dedicated, compiled-testable HTTP components; large Research and Wiki uploads are staged to temporary files and streamed into their import services under renewable inactivity deadlines. | Preserve authentication order, routes, range handling and protocol models. Extract again only when a coherent policy or side-effect boundary can gain compiled tests. Keep large imports stream-based and move large exports to streamed responses before their package limits grow. |
| P1 | Mobile session | `MobileClientSession.cs` began at 3,118 lines and is now 2,601 lines after gaining offline metadata, Live Radio, saved collections and Knowledge features. Playback ownership, multipart timeline state, remote observation, committed source-stop acknowledgement, library synchronization, offline mutation replay, downloads, downloaded-progress authority, Explore, Knowledge, pairing, cache-first direct lookup, artwork hydration and Library query/projection rules are extracted. | Keep the session as the public high-level orchestrator; extract another boundary only where focused behaviour tests justify it. |
| P1 | Shared test runner | `TheRadioVault.Tests/Program.cs` began at 9,874 lines and is now 6,537 lines after later cross-platform capability checks. Intentional source/packaging inspections live in the dependency-free `TheRadioVault.SourceChecks` runner. Compiled Web, Mobile, Data, Services and Transcription suites own focused subsystem checks, and `TheRadioVault.LibraryTruth.Tests` owns parser, grouping, adoption and rehearsal behaviours. Every focused suite is release-gated and migrated checks remain in exactly one runner. | Extract the next coherent behavioral group into its owning subsystem; do not duplicate migrated checks or replace one monolith with another. |
| P2 | Desktop playback | `PlaybackViewModel.cs` began at 2,168 lines and coordinates UI commands, handoff and timing. Transport/handoff transitions now live in `DesktopPlaybackStateMachine`, remote heartbeat smoothing lives in `RemotePlaybackProgressInterpolator`, and shared startup supersession/readiness policy lives in `PlaybackStartupCoordinator`. | Keep Avalonia binding projection and decoder/network side effects in the view model while moving future pure transition or timing policy into focused shared services. |
| P2 | Database services | `DatabaseService.cs` began this pass at 2,153 lines and its partials still carry broad library, research and reconciliation responsibilities. Listener-controlled playback, completion and favourite writes now delegate to `PersonalStateRepository`; canonical members are read and updated inside one transaction, while `SqliteDatabase` remains the connection and migration owner. The façade is 2,026 lines. | Continue one bounded context at a time. Prefer an extracted repository only when it removes duplicate policy or creates a stronger transactional boundary with compiled rollback coverage. |
| P2 | Database schema | The schema-47 bootstrap remains intact in `SqliteDatabase.cs`. Schema 48 begins a contiguous numbered-migration catalog with a transactional runner and durable history ledger. | Keep each future change in one numbered object with order, idempotence, rollback and pre-upgrade-backup coverage. Do not rewrite the legacy bootstrap. |
| P2 | Explore/Knowledge | Wiki, research workspace and their view models remain large, but the navigation contract is unified. Typed links cover articles, people, shows, topics, images, timelines and broadcasts; Broadcast Info and Explore prose emit them, and desktop/iOS/Web resolve them. Transcript search carries a timed target into desktop and iOS playback. iOS Knowledge has offline/live coverage, month/day drill-down, broadcast navigation and swipe triage. | Complete device acceptance and resist new label-only navigation. Further extraction should target authoring/import workflows, not the reader contract. |
| P3 | iOS presentation | `RadioVaultCells.cs` is 1,516 lines and controllers manually repeat styling/layout decisions. | Extract a design-system layer and reusable cells after accessibility identifiers and snapshot baselines exist. |
| P3 | Documentation | There are 457 Markdown files. `docs/current` now contains only its living-document index; 203 superseded Alpha/Beta/RC reports were preserved under indexed history folders. | Keep new living guidance in the indexed guide/architecture/roadmap locations and immutable release evidence under `history`. |

## Platform findings

### Server

The server remains authoritative for library metadata, playback ownership and conflict resolution. Database/storage/certificate/client/media health, per-device sync state, scheduled backup, isolated restore rehearsal, redacted diagnostics and rehearsed no-delete media consolidation are now implemented. The next operational work is bounded structured logging, background-service installation on every desktop platform, maintenance mode for migration/restore, and rate limits and expiry around pairing.

The server should not be exposed directly to the public internet. Remote access needs a designed relay or private-network approach with short-lived credentials, not port forwarding.

### Windows desktop

The shared Avalonia client is the correct direction. Before 1.0, verify installer upgrades/downgrades, media keys, taskbar metadata, high-DPI layouts, screen readers and audio-device changes. Public installers should be code-signed.

### macOS desktop

The native window chrome, menu and AVFoundation playback are appropriate. Remaining work includes Developer ID signing, notarisation, hardened runtime, media-key/Now Playing integration, lifecycle testing and an update strategy. Local packaging no longer needs PowerShell.

### Linux desktop

Test the Debian and portable packages on current Ubuntu and a non-Ubuntu distribution. Make mpv/audio-backend failures visible in-app. Add systemd service integration for the server before considering extra package formats.

### iPhone and iPad

The native UIKit client is a real daily-use client. Highest-value work is background/offline resilience, accessibility, an adaptive iPad layout, diagnostics, TestFlight signing, AirPlay route selection and robust system-media behaviour. CarPlay should come later with a safety-focused interface.

### Web client

Extract static assets and route handlers from the server monolith. Stop placing tokens in resource URLs where they may appear in history or logs; prefer authenticated requests, short-lived resource tickets or secure cookies appropriate to the local trust model.

## Cross-cutting reliability work

1. Define one conflict policy for progress, favourite, listened/unlistened, Moments and queue changes.
2. Attach a mutation id, client id, logical timestamp and acknowledgement to every offline write.
3. Test two devices editing the same broadcast while offline.
4. Smooth playback locally and reconcile only when authoritative drift exceeds a threshold.
5. Add fixtures for small, large, corrupt, upgraded and restored libraries.
6. Put timeouts and cancellation on every network, media-probe and external-process boundary.
7. Replace operationally significant silent catches with structured, rate-limited diagnostics.
8. Add performance budgets for cold start, cached Library navigation, search, scrolling and first audio.
9. Add accessibility identifiers and navigation checks before further large iOS layout work.
10. Keep icons, colours, terminology and states in shared design tokens where platform conventions allow.

## Feature opportunities

- Smart collections such as unheard interviews and user-defined show/topic rules.
- Saved queues and playlists alongside transient Up Next.
- Scheduled archive health scans with duplicate and missing-file repair.
- Transcript search that jumps directly to the spoken moment.
- Exportable Moment citations with timestamp and note, not copyrighted audio by default.
- An iPad split view for Library/Explore and Now Playing.
- AirPlay selection and richer Bluetooth-route feedback.
- A headless server mode with service controls and a browser health dashboard.
- Optional secure away-from-home access after the LAN security model is stable.
- Per-device download expiry and storage policies.
- An import preview explaining proposed show/date/part matching before changes are committed.

Generative AI is not required inside the app for these features. The current privacy promise—that the product itself contains no generative-AI assistant—should remain unless there is a deliberate future decision to change it.

## Recommended engineering rule

Every release should include a visible listener improvement, a reliability or accessibility improvement, and one bounded reduction in technical debt. That keeps refactoring connected to product progress without allowing the large coordination files to grow indefinitely.
