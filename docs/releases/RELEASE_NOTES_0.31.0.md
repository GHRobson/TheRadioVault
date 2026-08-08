# Release Notes — Radio Vault v0.31.0

Version: `0.31.0`

Radio Vault v0.31.0 completes **Core Hardening**, preparing the mature backend for a new Avalonia desktop shell without disrupting the existing WPF application or archive.

## Highlights

- Platform-neutral application contracts and explicit Windows adapters.
- Validated, frozen service composition before UI startup.
- Shared startup, shutdown and local/remote window-transition coordination.
- Application-owned playback commands, progress protection and completion evidence.
- Replaceable local media playback behind an application factory boundary.
- Shared authoritative remote Library session state, retries, cancellation and diagnostics.
- Authoritative remote Now Playing metadata and artwork parity.
- Executable architecture validation and WPF-independence proof.
- A concrete Avalonia handoff describing reusable seams and remaining presentation work.

## Stable promotion

The stable release preserves the successfully built, release-gated and runtime-accepted RC1 implementation. There is no runtime feature change, database migration, API route, LAN capability, pairing change or cache reset. Changes are limited to stable identity, release validation, documentation and packaging metadata.

## Compatibility

- Database schema: **45**.
- LAN capability generation: **14**.
- API: **v1**.
- Web-shell generation: **10**.
- Anywhere shell cache: **v33**.
- IndexedDB: **v2**.
- Audio/artwork caches: **v1**.

Existing v0.30, Beta 1 and RC1 installations should open directly without re-adoption, re-pairing or cache clearing.
