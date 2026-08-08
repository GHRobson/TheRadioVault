# Core Hardening migration ledger

Status values: **Baseline**, **Planned**, **In progress**, **Extracted**, **Verified**.

| Area | Current owner/coupling | Target boundary | Status | Planned phase |
|---|---|---|---|---|
| Startup mode selection | Saved LAN preferences interpreted inside `MainWindow` | `ApplicationStartupCoordinator` | Extracted | Alpha 2 |
| Navigation and page selection | `MainWindow` partials and WPF controls | Application navigation coordinator | Baseline | Alpha 4 |
| Notifications and confirmations | Direct `MessageBox` calls remain | `IUserNotificationService` | In progress | Alpha 3–4 |
| Open/save/folder selection | Common archive, backup, metadata and transcription paths use the port; specialised dialogs remain | `IFileSelectionService` | In progress | Alpha 3–4 |
| Clipboard | All current clipboard actions use the port | `IClipboardService` | Extracted | Alpha 3 |
| UI dispatch | Direct WPF Dispatcher access | `IUiDispatcher` at service/UI seams | Baseline | Alpha 3–4 |
| Window/dialog lifecycle | Direct window construction remains; mode replacement and saved-window visibility are coordinated | `IWindowCoordinator` plus presentation adapters | In progress | Alpha 2–4 |
| Application shutdown | WPF supplies cleanup actions; application service owns ordering and failure isolation | Application lifetime + shutdown coordinator | Extracted | Alpha 2 |
| External shell/system integration | External launch, system theme and virtual-screen checks use platform ports | Application platform contracts | Extracted | Alpha 3 |
| Database orchestration | Desktop `DatabaseService` use | Application use cases and repositories | Baseline | Alpha 4 |
| Playback orchestration | `PlaybackSessionCoordinator` owns commands/state; WPF still owns presentation and canonical source selection | Application playback session | In progress | Alpha 4–5 |
| LAN/web session orchestration | Application coordinator owns cursor, single-flight synchronization, timeout/cancellation, retry state and diagnostics; WPF still applies data and transport adapters still issue requests | Shared application session services | In progress | Alpha 5–6 |
| Dependency composition | Frozen validated registry, explicit lifetimes, platform registration, playback factories and composition diagnostics; feature-specific service construction still remains at the WPF edge | Explicit composition root/DI modules | Verified | Alpha 2–6 |
| Avalonia presentation | Not started | New Avalonia project against stable ports | Planned | After Core Hardening |

Alpha 5 adds the first shared LAN session boundary. `RemoteLibrarySessionCoordinator` now owns synchronization cursor advancement, duplicate suppression, request cancellation/timeouts, retry scheduling, live/cache/unavailable state and diagnostics. The WPF shell still maps server DTOs into current presentation models, and the certificate-pinned transport remains in the existing LAN adapter. Playback presentation, queue/database integration, specialised dialogs and broader feature-service composition remain explicit Alpha 6 work.


## Alpha 6 closure

Alpha 6 freezes and validates the application dependency graph before the first window opens. Local playback-engine creation now belongs to the Windows adapter project and playback-session creation belongs to the Application layer. Duplicate registrations, missing required services and cyclic factories fail at the composition edge, and the remote Library session is explicitly disposed during the ordered shutdown lifecycle.

Remaining work is deliberately deferred to the Core Hardening beta: broad behaviour soak testing, removal of defects found by that testing, and documenting the exact presentation/service seams the Avalonia shell will consume. Specialised modal windows, feature-specific database orchestration and visual navigation remain WPF-owned until the Avalonia implementation replaces them; Alpha 6 does not attempt a risky screen-by-screen extraction.

## RC1 proof freeze

RC1 promotes the accepted Beta 1 implementation after successful Windows build, complete release gate and live runtime acceptance. It introduces no new feature behavior.

## Beta 1 proof and freeze

Beta 1 introduces no new feature behavior. The accepted Alpha 6 Buildfix 1 service boundaries are frozen and verified by both the architecture gate and the WPF-independence proof. The proof reports zero hard-boundary violations and separates the remaining work into Avalonia shell/navigation, window/dialog replacement, dispatcher binding, feature view-model extraction and theme/chrome work.

These work packages are not evidence that the backend is still WPF-owned: they are the visible presentation surface that the Avalonia rebuild is intended to replace. New feature expansion remains frozen until the v0.31 beta/RC acceptance path is complete.

## Stable v0.31 closure

v0.31.0 promotes the accepted RC1 implementation without runtime changes. Core Hardening is complete: the reusable application/platform/playback/session boundaries are frozen, the executable architecture and WPF-independence gates pass, and the remaining WPF surface is explicitly assigned to the v0.32 Avalonia presentation rebuild.
