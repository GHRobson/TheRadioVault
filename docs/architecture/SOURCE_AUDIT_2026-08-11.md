# Radio Vault source audit

Date: 11 August 2026
Branch audited: `ios/dashboard-library-refresh`
Baseline commit: `6920c3f`

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

The schema has reached version 47 and the database is one of the most important parts of the application. Migration backup exists, but a user-facing scheduled backup, restore wizard and automated restore rehearsal are still needed before 1.0. A backup is not proven until a clean server can restore it and pass an integrity check.

### 5. Improve release diagnostics

When a real device fails, debugging currently depends heavily on live development access. Add a privacy-conscious diagnostics export containing application version, platform, sync checkpoints, playback ownership transitions, recent errors and server health—never tokens, recordings or transcript contents.

## Major refactoring targets

| Priority | Area | Evidence | Safe extraction path |
|---|---|---|---|
| P1 | Web server | `LocalWebServer.cs` began at 10,436 lines and still combines authentication, HTML, CSS, JavaScript, media and many API families. Player handoff/playback/queue routing is isolated in `LocalWebServer.PlaybackQueue.cs`, and authenticated federation/administration routing is isolated in `LocalWebServer.FederationAdministration.cs`, reducing the main file to 9,942 lines. | Continue moving static web assets into resource files and splitting the remaining client, knowledge and media route groups. Preserve dispatcher order, routes and protocol models. |
| P1 | Mobile session | `MobileClientSession.cs` began at 3,118 lines. Playback ownership, multipart timeline state, remote observation, committed source-stop acknowledgement, library synchronization, offline mutation replay, downloads, downloaded-progress authority, Explore, Knowledge, pairing and Library query/projection rules are now extracted, reducing the façade to 2,090 lines. | Keep the session as the public high-level orchestrator; extract another boundary only where focused behaviour tests justify it. |
| P1 | Shared test runner | `TheRadioVault.Tests/Program.cs` has fallen from 9,874 to 9,754 lines. Four intentional route, Knowledge-integration and presentation source inspections now live in the dependency-free 238-line `TheRadioVault.SourceChecks` runner and execute through the release gates. | Continue moving source-only checks into SourceChecks and behavioral tests into normal subsystem projects; do not merely redistribute one monolith into another. |
| P2 | Desktop playback | `PlaybackViewModel.cs` began at 2,168 lines and coordinates UI commands, handoff and timing. Transport/handoff transitions now live in `DesktopPlaybackStateMachine`, and remote heartbeat smoothing lives in `RemotePlaybackProgressInterpolator`, reducing the view model to 2,142 lines. | Keep Avalonia binding projection and decoder/network side effects in the view model while moving future pure transition or timing policy into the focused Application services. |
| P2 | Database services | `DatabaseService.cs` and its partials carry broad library, research and reconciliation responsibilities. | Split repositories and transaction services by bounded context while retaining one connection/migration owner. |
| P2 | Database schema | `SqliteDatabase.cs` is 1,713 lines with schema creation and many migrations. | Put each future migration in a numbered migration object with an idempotence test and restore fixture. Do not rewrite old migrations. |
| P2 | Explore/Knowledge | Wiki, research workspace and their view models are large and partially overlapping. | Establish one link/entity model for people, shows, topics, images, timelines and broadcasts, then expose the same query contracts everywhere. |
| P3 | iOS presentation | `RadioVaultCells.cs` is 1,516 lines and controllers manually repeat styling/layout decisions. | Extract a design-system layer and reusable cells after accessibility identifiers and snapshot baselines exist. |
| P3 | Documentation | There are 462 Markdown files and `docs/current` contains superseded Alpha/RC reports. | Keep only living documents in `current`; move immutable release evidence into `history` with an index. |

## Platform findings

### Server

The server should remain authoritative for library metadata, playback ownership and conflict resolution. Its next work should focus on operations: a database/storage/certificate/client health page; scheduled backup and verified restore; bounded structured logs; visible per-device sync state; background-service installation on every desktop platform; maintenance mode for migration/restore; and rate limits and expiry around pairing.

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
