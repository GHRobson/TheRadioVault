# Avalonia migration boundary — v0.32 Alpha 6

## Current state

The v0.31 Core Hardening boundary is accepted. The Avalonia shell now owns the normal Dashboard, Library, local and remote playback, favourites, Queue, Moments, Research, Metadata Studio and Connected Access workflows. The WPF application remains independently buildable as the complete reference for settings, diagnostics, import/rescan, maintenance and specialised windows that have not yet migrated.

## Implemented foundation

- `TheRadioVault.Desktop.Avalonia` owns Avalonia startup, theme resources, application lifetime, views, playback adapter and Connected Access adapters.
- `TheRadioVault.Presentation` owns toolkit-neutral shell navigation, commands and view models.
- `ILibraryBrowseService`, `ILibraryActionService`, `ILocalPlaybackLibraryService`, `IQueueService`, `IMomentsService`, `IResearchWorkspaceService` and `IConnectedAccessService` keep local and remote ownership behind stable boundaries.
- `AvaloniaApplicationHost` chooses local or remote startup before composing the service graph; remote mode does not initialise the dormant local database.
- `RemoteLibrarySessionCoordinator` owns live/cached/reconnect state and the encrypted metadata cache remains server/certificate-bound.
- Remote media is exposed to NAudio only through the loopback credential proxy; server certificates and desktop tokens never leave the process.
- Normal remote metadata writes use server API v1, while advanced Research decisions remain authoritative-server operations.
- Global reduced-motion-aware elastic overscroll, the production design system and expandable per-show navigation remain shared across migrated pages.

## Reusable v0.31 seams

- `ApplicationStartupCoordinator`, `ApplicationShutdownCoordinator` and `ApplicationWindowTransitionCoordinator`
- `ApplicationServiceRegistry`
- platform contracts in `TheRadioVault.Application`
- `PlaybackSessionCoordinator`, progress/completion coordinators and `PlaybackSessionFactory`
- `ILocalPlaybackEngineFactory`
- `RemoteLibrarySessionCoordinator`
- existing Services, Research, Transcription, Web/LAN and Data projects

## Next work packages

1. **Alpha 7 — Settings, diagnostics and archive operations:** migrate normal settings, diagnostics, import/rescan, backup/restore entry points and archive-health surfaces without moving server-owned maintenance onto a remote client.
2. Migrate remaining specialised desktop windows behind the existing service contracts.
3. Perform a complete WPF-versus-Avalonia parity ledger and remove any normal-workflow dependence on the reference shell.
4. Begin Beta performance, accessibility, lifecycle, reconnect and daily-driver hardening.

## Compatibility contract

Database schema remains 45 and LAN capability generation remains 14. API v1, pairing credentials, certificates, encrypted remote-cache identity and web/offline cache generations are unchanged. Local and remote shells consume the established formats rather than creating parallel state.

## Executable evidence

- `tools/Test-Architecture.ps1`
- `tools/Test-WpfIndependence.ps1`
- `tools/Test-AvaloniaFoundation.ps1`
- the console regression suite
- the dual-shell Windows release gate

## Alpha 7 desktop parity

The Avalonia default shell now includes Library List/Grid modes, compact and full broadcast-information views, a dedicated Now Playing page, simplified navigation, and the first guarded Settings & Tools workspace. Remaining work is the full WPF-versus-Avalonia parity audit, migration of backup/cancellation-sensitive maintenance workflows, accessibility/performance hardening, and Beta acceptance.
