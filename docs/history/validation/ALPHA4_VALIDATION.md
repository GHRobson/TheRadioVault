# v0.27.0-alpha4 validation record

Version: `0.27.0-alpha4-transcript-quality-and-portable-packages`  
Schema: `37`

## Static validation

- All project files and all WPF XAML files parse as XML.
- All XAML event-handler references resolve to code-behind methods.
- Changed C# files parse successfully with the C# syntax parser.
- Schema-37 transcription SQL executes successfully in SQLite and creates `content_kind` and `is_reviewed`.
- The source retains format-1/2 JSON import compatibility and emits format-3 compressed packages.
- Portable transcript metadata excludes executable paths, temporary directories and successful-worker log tails.
- The source package contains no build output, temporary model files or the user-supplied regression transcript.

## Bennington regression sample

The supplied 2 July 2026 full-episode transcript contains 1,212 raw display segments and occupies 5,081,768 bytes as format-2 JSON. The conservative alpha4 classifier recognises 33 explicitly music-marked segments and collapses them into nine timed music regions, reducing the viewer to 1,188 archival segments without changing the surrounding speech timestamps.

A simulated format-3 package of the same transcript occupies approximately 389 KB, about 7.6% of the uncompressed package size. Its local executable path and worker log tail are removed from portable metadata.

## Remaining Windows gate

A complete WPF compilation and the executable smoke-test suite still need to run through Visual Studio and `release-gate.ps1` on Windows.
