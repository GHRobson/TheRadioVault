# Alpha 8 static validation

The source package is checked without modifying a user library.

Validation covers:

- XML/XAML parsing and event-handler references.
- C# lexical delimiter balance.
- Version, schema and capability-generation constants.
- Presence of the cache synchronization route and `lan.cache-sync` capability.
- AES-GCM encryption, gzip compression, size bounds, server/certificate binding and atomic cache replacement.
- Cached startup, delta/deletion application, background synchronization and explicit cached read-only guards.
- Cached Moments and transcript-summary integration.
- Bounded stream recovery and cancellation.
- Server/remote-client terminology in current user-facing source.
- Source-manifest hashes and ZIP integrity.

A Windows `Release | x64` build and the two-machine acceptance checklist are still required because this package environment does not include the .NET SDK or WPF runtime.
