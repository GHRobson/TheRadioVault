# Alpha 3 Buildfix 1 static validation

- [x] Version metadata reports `0.30.0-alpha3-remote-playback-write-through-buildfix1`.
- [x] `LanFederationMediaProxy.ReadRequestAsync` no longer declares `Span<T>` or another ref-struct local.
- [x] Header termination remains bounded to the existing 32 KiB request-header limit.
- [x] Alpha 3 LAN playback and write-through contracts are unchanged.
- [x] Database schema remains 45 and capability generation remains 10.
