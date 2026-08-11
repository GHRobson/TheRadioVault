# Radio Vault architecture boundaries

## Dependency direction

`TheRadioVault.Core` is the innermost domain and policy layer. `TheRadioVault.Application` defines use-case and UI/platform ports and may depend only on Core. Data, Media and feature implementations remain outside those contracts. User interfaces and operating-system adapters depend inward; inner projects never depend on a UI toolkit.

## Projects

| Project | Role | Platform policy |
|---|---|---|
| `TheRadioVault.Core` | Domain models, parsing, identity and policy | Neutral |
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

`MobileClientSession` remains the mobile client façade and retains network and decoder side effects. `MobilePlaybackOwnershipCoordinator` owns the pure rules that interpret a shared playback session: active ownership, committed moves, source-stop acknowledgement and the two-sample safeguard for legacy foreign owners. `MobilePlaybackTimeline` owns the side-effect-free mapping between multipart media, the logical broadcast playhead and decoder-relative positions, including settling protection and completion tolerance. New ownership or timeline rules belong in those components with focused tests; they must not be duplicated in the iOS view layer or folded back into the session façade.
