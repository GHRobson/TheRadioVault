# v0.27 alpha1 preparation validation

## Static validation completed

- Version metadata is consistent at `0.27.0-alpha1-buildfix1-transcription-foundation`.
- The solution includes the new Transcription project and both WPF/tests reference it.
- All project files and all 22 XAML files parse as XML.
- All 17 new or modified C# source files parse successfully with the C# syntax grammar; the full source tree was also checked with the one known inherited contextual-keyword parser exception accounted for.
- All 190 MainWindow XAML event handlers resolve to C# methods and MainWindow named controls are unique.
- Schema 34 transcription SQL executes successfully against SQLite and creates all four required tables and indexes.
- Existing schema-33 research rollback/provenance structures remain present.
- Transcript package, repository, identity-protection, duplicate-provenance and schema smoke tests were added.
- Transcript package import reads and hashes one locked file snapshot, rejects mismatched broadcasts and validates segment/word timing and confidence data.
- Background transcription jobs have a race-safe background-job mapping, throttled durable progress and cancellation seams.
- Source package contains no build output, database, certificate or temporary files.

## Local acceptance still required

The container does not contain the .NET 8/Windows WPF toolchain. Build `TheRadioVault.sln` in Visual Studio and run `release-gate.ps1` locally.

After building, verify:

1. An existing copied v0.26 database opens and reports schema 34 without losing library/research/playback data.
2. Transcripts appears in the sidebar and opens without errors.
3. Broadcast Info reports that no transcript exists and the Transcribe button clearly explains that no engine is configured.
4. A valid matching `.trvtranscript` imports, appears in the workspace, opens, searches and seeks playback from a segment.
5. A package for another broadcast is rejected rather than attached incorrectly.
6. The imported transcript exports and can be re-imported as a new revision after confirmation.
7. A selected queued/running job can request cancellation once a concrete engine is connected.
8. Normal desktop playback and PC↔phone transfer remain unchanged.


## Buildfix1 compile correction

- Corrected the incompatible `List<TranscriptWord> ?? TranscriptWord[]` null-coalescing expression.
- The fallback now remains a `List<TranscriptWord>`, which is assignable to the declared `IReadOnlyList<TranscriptWord>` target.
- Expanded the transcript repository smoke test to persist and reload timed words.
- The downstream missing `TheRadioVault.Transcription.dll` error was a consequence of the project compile failure and requires no separate change.
