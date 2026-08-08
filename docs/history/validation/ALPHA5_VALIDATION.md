# v0.27.0-alpha5 validation record

Version: `0.27.0-alpha5-archive-identity-and-parser-reliability`  
Database schema: **37**

## Completed in source review

- Parser coverage added for `2-6-2015`, `9-04-2014`, OpieRadio slot identity and explicit filename show detection.
- Duplicate grouping now includes slot and part boundaries and uses full hash for exact status.
- Cross-PC restore preserves destination roots when source roots differ.
- Cloud-only classification requires cloud recall flags plus a reparse point.
- No schema change and no destructive file operation were introduced.
- Whole-source lexical/XML validation passed across 173 C# files and 25 XAML files.
- Synthetic SQLite checks passed for full-hash duplicates, slot boundaries, legacy OpieRadio relinking and portable restore SQL.

## Environment limitation

The source-generation environment does not contain the .NET SDK or Visual Studio, so the full solution build and smoke-test executable could not be run here. `release-gate.ps1` must be run on the user's Windows build machine.

## User acceptance pending

- Visual Studio Release build.
- `release-gate.ps1`.
- Large USB archive rescan and new diagnostics.
- Cross-PC restore test on copied data.
- Alpha4 transcription regression spot-check.
