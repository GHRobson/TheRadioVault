# v0.27.0-alpha3 buildfix1 validation record

Version: `0.27.0-alpha3-buildfix1-model-download-finalisation`  
Database schema: **36**

## Reported failure

The curated model download reached completion, then Windows reported that the temporary model file was in use by another process. The alpha3 downloader called `File.Move` while its `FileStream` was still inside an active `await using` declaration, so Windows correctly refused the rename.

## Correction

- Scoped the response stream and output stream so both are disposed before validation and final promotion.
- Changed progress reporting to reserve 100% for a successfully promoted final model file.
- Added unique per-download temporary filenames.
- Added cleanup of the fixed-name `.download` file created by alpha3 when possible.
- Added eight short asynchronous finalisation retries for transient antivirus, search-indexing, OneDrive or other scanner handles.
- Added a clearer persistent-lock error while preserving cancellation and temporary-file cleanup.

## Static validation

- All project files and XAML files parse as XML.
- Version metadata is consistent across `VERSION.txt` and the WPF project.
- `WhisperModelDownloadService` contains an explicit nested stream-disposal scope before `PromoteTemporaryFileAsync`.
- The final promotion retry is cancellation-aware and never reports 100% before `File.Move` succeeds.
- Database schema remains 36.
- No playback, research, web/mobile ownership or transcript-storage code was changed.

A complete WPF build still requires Visual Studio with the .NET 8 Windows desktop workload.
