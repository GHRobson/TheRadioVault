# v0.27.0-alpha2 validation record

Version: `0.27.0-alpha2-speaker-identity-and-voice-memory`  
Database schema: **35**  
Transcript package format: **2**

## Completed in the packaging environment

- `VERSION.txt`, project `Version` and `InformationalVersion` are identical.
- All **32** XAML/project XML files parsed successfully.
- **165** C# files, including every new or changed transcription/speaker file, passed tree-sitter syntax validation. The unchanged embedded web-host source retains its previously known parser-only issue around a JavaScript object property named `partial`; its extracted JavaScript was validated separately.
- All **4** PowerShell scripts passed PowerShell grammar parsing.
- All **310** XAML event references resolve to C# handlers.
- Schema-35 transcription SQL executed successfully against SQLite and created the nine expected transcript/speaker/voice-memory tables.
- `has_speaker_diarization` and `speaker_key` migration columns were verified.
- The rejected/failed voice-sample reactivation upsert was executed successfully against SQLite.
- The embedded mobile-player JavaScript and Service Worker both passed `node --check`.
- Source-package hygiene passed: no `bin`, `obj`, temporary, backup or editor-generated files are present.
- Source markers verify speaker identity contracts, local voice-engine abstraction, profile matching, correction safeguards, format-2 exchange and the new smoke tests.

## Behaviour covered by source smoke tests

- schema 35 creates speaker and voice-memory storage;
- timed transcript words still round-trip through SQLite;
- invalid/overlapping transcript packages are rejected;
- transcript package identity protection remains intact;
- confirmations for one person across multiple broadcasts accumulate samples and profile evidence;
- a high-confidence match becomes a suggestion rather than a silent confirmation;
- directly correcting a person rejects the old evidence and rebuilds both affected profiles;
- speaker assignments survive format-2 export/import.

## Local acceptance still required

The Linux packaging environment does not contain the Windows .NET/WPF toolchain. Build the complete solution in Visual Studio, then run `release-gate.ps1`. Use a copied schema-34 database and test the **Speakers…** workflow with a diarized `.trvtranscript` before accepting alpha2 as the development baseline.
