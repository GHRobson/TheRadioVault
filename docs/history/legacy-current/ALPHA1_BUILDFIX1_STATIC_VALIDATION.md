# v0.30.0 Alpha 1 Buildfix 1 Static Validation

Validated target: `0.30.0-alpha1-lan-federation-buildfix1`

- Project, informational and package versions agree.
- Connected Access checkbox handlers persist changes immediately and suppress refresh-time event recursion.
- HTTPS/federation dependency rules are enforced in the UI.
- Private IPv4 adapters are enumerated without exposing adapter data in discovery payloads.
- The server sends multicast and directed-broadcast announcements per adapter.
- The remote client joins multicast per adapter and accepts directed broadcasts.
- Discovery remains `radiovault-lan-v1` and excludes credentials and archive content.
- Discovery timeout is eight seconds and failure text reports the listening adapters.
- Schema 45, capability generation 8 and all v0.29 Anywhere storage/cache identities are unchanged.
- XML/XAML parsing, JavaScript syntax, source-manifest verification and ZIP integrity pass.

Windows/Visual Studio compilation and the repeated server-to-remote-client discovery/pairing test remain the real-device acceptance gate.
