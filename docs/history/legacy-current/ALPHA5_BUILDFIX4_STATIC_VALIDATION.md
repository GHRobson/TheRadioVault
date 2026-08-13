# Alpha 5 Buildfix 4 static validation

- [x] Version metadata reports `0.30.0-alpha5-buildfix4-canonical-manifest-file-size`.
- [x] `CanonicalLibraryQueryService.GetDownloadManifest` reads `media_files.file_size`, the schema-45 physical-size column.
- [x] No canonical manifest query references the nonexistent `media_files.size_bytes` column.
- [x] The canonical cutover integration scenario asserts both multipart part sizes and `TotalSizeBytes`.
- [x] Buildfix 3's `MainWindow.IsLoaded` speed-selector startup guard remains present.
- [x] Buildfix 2's `LanRemoteTranscriptSummary` declaration remains present.
- [x] Buildfix 1 shared-shell and removed-window guards remain present.
- [x] Database schema remains 45 and LAN capability generation remains 11.
- [x] No web API route, payload DTO, media identity, playback timing rule or cache identity changed.

Windows/.NET server and remote-client playback testing remains the definitive validation for the reported HTTP 500 repair.
