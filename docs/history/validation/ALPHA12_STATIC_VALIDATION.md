# Alpha 12 static validation

- Version advanced to `0.28.0-alpha12-canonical-media-manifests`.
- Canonical recording options derive from the completed, commit-verified Library Truth run.
- Explicit recording lookup still excludes review-required coverage.
- Download manifests fail closed when any segment has no non-missing source.
- Preferred playback remains backward compatible.
- Schema remains 45 and no destructive file operation was introduced.

The Linux packaging environment does not contain the .NET SDK, so compilation must be completed in Visual Studio on Windows using `build.ps1`.
