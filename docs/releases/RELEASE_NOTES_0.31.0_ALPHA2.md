# Radio Vault v0.31.0 Alpha 2 — Application-Service Extraction

Version: `0.31.0-alpha2-application-service-extraction`

Alpha 2 begins live Core Hardening extraction while preserving all accepted Radio Vault behaviour.

## Changes

- Added a small platform-neutral application service registry and explicit WPF/Windows registration module.
- Moved local-library versus remote-client startup policy into `ApplicationStartupCoordinator`.
- Moved ordered cleanup execution and per-step failure isolation into `ApplicationShutdownCoordinator`.
- Moved duplicate prevention and source/target mode tracking for replacement-window transitions into `ApplicationWindowTransitionCoordinator`.
- Routed the existing WPF startup, local/remote switching and shutdown paths through the new services.
- Added regression coverage for startup selection, composition lifetimes, shutdown continuation after a failed step and once-only window transitions.
- Updated the Core Hardening migration ledger and architecture report.

## Compatibility

No database, library, pairing, LAN protocol, cache or UI migration is introduced. Schema remains 45 and LAN capability generation remains 14.
