# v0.26.0 final preparation record

## Confirmed before promotion

- The user built v0.26.0 RC1 successfully in Visual Studio.
- The user completed normal RC1 soak testing and reported no problems.
- The detailed mobile regression pass confirmed repeated PC↔phone transfer, synchronized state, paused ownership, seeking, speed, resume, background playback, browser refresh, Wi-Fi recovery, server restart and second-phone ownership.

## Final promotion scope

The final source is a metadata-only promotion of the accepted RC1 implementation. Changes are limited to version strings, final release documentation, validation markers and the secure offline shell cache name (`v23` → `v24`) so installed mobile clients replace RC shell content. Database schema remains 33.

## Preparation checks run in the packaging environment

- Version metadata consistency.
- Project/XML and XAML well-formedness.
- Embedded web-client and service-worker JavaScript syntax.
- Source-package hygiene and archive integrity.
- Absence of stale hard-coded buildfix diagnostic versions.

The packaging environment does not contain the Windows .NET 8/WPF or PowerShell toolchain. Build the final source once in Visual Studio and run `release-gate.ps1` before distributing a compiled binary.
