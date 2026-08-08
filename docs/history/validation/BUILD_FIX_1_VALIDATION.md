# Radio Vault v0.27.0-alpha1 buildfix1 validation

## Reported compiler failure

The transcription project failed with CS0019 because the null-coalescing operator combined two incompatible concrete collection types:

- left operand: `List<TranscriptWord>`
- right operand: `TranscriptWord[]`

The main WPF project then reported CS0006 because the failed transcription project did not produce `TheRadioVault.Transcription.dll`.

## Correction

`SqliteTranscriptRepository` now uses a `List<TranscriptWord>` fallback after JSON deserialization. The result remains assignable to the declared `IReadOnlyList<TranscriptWord>` variable without mixing incompatible operands.

The repository smoke test now persists and reloads timed words so this deserialization path is covered.

## Static checks completed

- All project and XAML XML files parse successfully.
- The solution and both consumers reference the transcription project.
- No remaining `Deserialize<List<...>>() ?? Array.Empty<...>()` pattern is present.
- Version metadata is consistent at `0.27.0-alpha1-buildfix1-transcription-foundation`.
- ZIP integrity was checked after packaging.

A complete WPF/.NET build still requires Visual Studio on Windows.
