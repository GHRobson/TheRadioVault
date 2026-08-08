# v0.27.0-alpha3 buildfix1 validation record

Version: `0.27.0-alpha3-buildfix1-model-download-finalisation`  
Schema: **36**

## Source checks completed

- All project files and XAML parse as XML.
- All changed C# files parse without syntax errors using the C# syntax parser.
- Every WPF event handler referenced by XAML resolves to a code-behind method.
- Schema 36 includes durable language, range, diarization, VAD and replacement fields on transcription jobs.
- The transcription assembly remains independent of WPF.
- The whisper.cpp worker is behind `ITranscriptionEngine`; no native executable or model is bundled into the source package.
- Model downloads use temporary files, cancellation, minimum-size validation and atomic replacement.
- Full and partial job options, retry seams, transcript phrase grouping and speaker-turn metadata are present.
- Source validation was updated for schema 36, the real worker, Settings UI and alpha3 smoke tests.
- Package hygiene and archive integrity are checked after ZIP creation.

## Local acceptance required

The packaging environment does not include the Windows .NET/WPF toolchain or a native whisper.cpp worker. In Visual Studio:

1. Build the complete solution.
2. Run `release-gate.ps1`.
3. Configure `whisper-cli.exe` and download the Base English model.
4. Transcribe a 10-minute sample and confirm progress, cancellation, retry, timed text and seek-from-transcript.
5. Run a full broadcast.
6. With a compatible `*-tdrz` model, confirm anonymous speaker clusters appear and can be assigned in **Speakers…**.

## Packaging-environment result

The automated source pass completed successfully for version matching, schema 36, changed C# syntax, all project/XAML XML, WPF event-handler resolution, PowerShell syntax, transcription-schema execution in SQLite, worker/configuration markers, alpha3 smoke-test coverage and source hygiene.

A full WPF compile was not possible in this Linux packaging environment because the .NET Windows desktop toolchain is not installed. The first acceptance action remains the Visual Studio build and `release-gate.ps1` run on Windows.
