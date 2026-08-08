# Project Phoenix — v0.27 alpha3

Alpha3 keeps native speech execution behind the platform-neutral Transcription boundary. WPF may configure an external worker and models, but no WPF type leaks into transcript parsing, durable jobs, speaker storage, profile aggregation or engine contracts.

Project Phoenix now includes a platform-neutral transcription boundary. `TheRadioVault.Transcription` depends on Core/Data/Services but not WPF or Web; the desktop shell composes it and renders transcript workflows. This preserves the future Avalonia/native-client seam while allowing local engine implementations to be replaced independently. No existing Phoenix playback or research boundary is changed in alpha1.

# v0.26.0 final Phoenix status

The Phoenix boundaries required by v0.26 are complete and now form the stable release baseline. v0.26.0 ships the platform-neutral research/web contracts, typed coordination, background jobs, guarded mutations, rollback/provenance and unified playback ownership without beginning another extraction or rewrite.

## Phoenix 5 Safari range correction

Live iPhone playback now receives isolated, non-cacheable HTTP byte-range sessions while retaining the existing Phoenix ownership state machine.

## Phoenix 5 transfer polish (0.26.0-beta1-preview6-buildfix18-phone-transfer-prime-retry-fix-archivist)

Closes the PC-to-phone takeover race and polishes the unified playback ownership controls without changing the database schema.

## Phoenix 5 unified playback ownership (0.26.0-beta1-preview6-buildfix13-unified-playback-ownership-archivist)

Phoenix 5 now models playback as a platform-neutral session with explicit output ownership. The WPF decoder and browser decoder remain separate implementations behind the same owner/generation contract. Each client can render active or inactive controls without reaching across the boundary to manipulate another client's UI. This is the architectural seam needed for later native clients and LAN federation while preserving the stable WPF media engine.

## Phoenix 5 ownership reliability fix (0.26.0-beta1-preview6-buildfix12-phone-handoff-deadlock-fix-archivist)

Automatic ownership now respects the platform boundary without holding shared web state across WPF dispatcher calls. This preserves the one-session/no-handoff-button model while preventing the desktop shell from becoming blocked during PC-to-phone transfer.

## Phoenix 5 automatic ownership slice (0.26.0-beta1-preview6-buildfix11-automatic-playback-ownership-archivist)

The guarded remote-control boundary now coordinates one shared playback owner. Desktop ownership is a typed Core event; phone ownership is a short provider-level claim completed by the first browser playback heartbeat. This prevents delayed Safari updates from stealing playback back while keeping the WPF decoder as the only desktop media authority. The web bootstrap and offline storage boundaries are unchanged.

## Phoenix 5 offline listening slice

The branded phone client now supports explicit local downloads without expanding the desktop job system into a download manager. This keeps the requested boundary clear: foreground, user-initiated downloads only; no automatic or background media acquisition.

# Project Phoenix

Project Phoenix incrementally separates Radio Vault's platform-neutral capabilities from the WPF desktop shell. Every slice must keep the solution buildable and preserve existing databases and user-visible behaviour unless a change is explicitly documented.

## Completed slices

### Phoenix 1 — Release gate

- Independent smoke-test executable.
- Full-solution release build gate.
- XAML, binding, handler, version and source-hygiene validation.

### Phoenix 2 — Research and Web boundaries

- `TheRadioVault.Research` owns the platform-neutral research-quality rules and audit contracts.
- `TheRadioVault.Web` owns HTTP routing, token validation, LAN restrictions, web episode queries, artwork delivery and byte-range audio streaming.
- The WPF project now supplies thin database, preferences and diagnostic adapters.
- Smoke tests cover the new Research and Web assemblies.

## Phoenix 3

The embedded web host now exposes a typed, read-only `/api/v1` contract and the mobile client is an API consumer rather than a collection of direct archive query routes. Research Quality actions are preview-first and reversible, with persistence owned by the desktop database adapter and audit rules remaining platform-neutral.

## Phoenix 4

Shared coordination is now established. Core owns typed application events and atomic playback state; Services owns bounded background jobs with progress/history/cancellation. The WPF shell publishes domain changes and coalesces refreshes. The web adapter converts the same events into an authenticated bounded change feed and exposes narrow favourite, listened-state and job-cancellation mutations.

Research Quality adds filtering, batch preview/application and actionable repair history without moving database writes into the platform-neutral rules assembly.

## Next Phoenix slice

Phoenix 5 should focus on durable research-pack rollback, database/import integration fixtures, and further movement of orchestration out of WPF partial classes. Live remote playback control remains a Radio Vault Anywhere milestone rather than part of the Phoenix 4 coordination foundation.

## Phoenix 5

Phoenix 5 turns the live-state foundation into guarded two-way playback. `TheRadioVault.Web` defines transport/queue contracts and serves the branded client, while the WPF shell implements `IWebPlaybackController` and remains the sole owner of the desktop playback engine. Desktop commands are revision-checked; phone sessions are client-leased; both paths publish the existing typed events.

The next Phoenix slice should focus on pack-level transactions and rollback, import/database integration fixtures, and moving more reconciliation orchestration out of WPF.

## Phoenix 5 secure offline slice

The manual phone-download capability now has a trusted HTTPS transport and a cached application shell. Certificate creation remains a desktop platform concern, while TLS hosting, onboarding endpoints and Service Worker delivery remain inside `TheRadioVault.Web`. The change preserves the Phoenix rule that media acquisition is explicit: the Service Worker cannot fetch or pre-cache broadcasts.
