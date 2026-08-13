# Beta 1 static validation

Target: `0.30.0-beta1-multi-device-hardening`

## Verified in the source package

- Version identity is consistent across `VERSION.txt`, the WPF project and root documentation.
- Database schema remains 45 and LAN capability generation remains 14.
- Alpha 9 parity, Research workspace, artwork, cache, synchronization and local-database isolation contracts remain present.
- Routine remote-client deltas use `ApplyAuthoritativeLibraryDelta` and update existing `EpisodeListItem` objects through an all-properties notification.
- Full presentation rebuild remains required for reset, deletion, addition, show-change and date-change conditions.
- Connected Access copied diagnostics include synchronization mode, duration, changed-row count, failure count, retry time and last error.
- The server logs changed or unusually slow federation synchronization preparation with elapsed time and bounded counts.
- The session guard records local-library/remote-client mode and the current shutdown stage.
- Shutdown still stops timers and synchronization before the bounded final remote progress save and retains the 12-second watchdog.
- Local-library/remote-client window replacement is explicitly marked as an application-window transition: it does not arm the process-exit watchdog, does not complete the replacement session marker, and prevents an outgoing remote window from stopping the replacement local web server.
- Beta 1 release notes and soak checklist are present.
- XML/XAML parse checks, event-handler reference checks, source-manifest verification and ZIP integrity checks are run during packaging.

## Not verified in this environment

The packaging environment does not include the .NET 8 SDK, WindowsDesktop SDK, WPF runtime or the user's archive. It cannot prove:

- a real Visual Studio `Release | x64` compilation;
- live HTTPS pairing/certificate behaviour on the user's LAN;
- WPF responsiveness with the full archive;
- media streaming/decoder recovery;
- actual process disappearance and progress preservation across repeated Windows shutdown tests.

Those checks are covered by `V0.30.0_BETA1_SOAK_CHECKLIST.md`.
