# Radio Vault v0.27.0-alpha4 — Transcript Quality and Portable Packages

Version: `0.27.0-alpha4-transcript-quality-and-portable-packages`  
Database schema: `37`

Alpha4 uses the first completed full Bennington transcript as a practical quality baseline. The local worker remains functional; this update concentrates on turning raw speech-recognition output into safer, smaller and more reviewable archival data.

## Added

- Compressed format-3 `.trvtranscript` packages, while retaining format-1 and format-2 JSON imports.
- Privacy-safe portable metadata that excludes local executable paths, temporary directories and successful-worker diagnostic logs.
- Metadata-informed Whisper vocabulary context built from show, title, people, guests, callers, topics and research terms.
- Conservative music, silence and non-speech classification with contiguous bumper collapse.
- Low-confidence review filtering, confidence labels and reviewed-state tracking.
- Manual segment correction and content classification.
- One-click quality cleanup for transcripts created by alpha3.
- Safe worker performance metadata: clean worker version, model filename, actual backend, processing time, audio duration and real-time speed multiplier.

## Database and package compatibility

- Schema 36 upgrades to schema 37 through the normal pre-migration backup path.
- Existing transcript segments default to Speech and Unreviewed; no transcript text is discarded.
- Format-3 packages use ZIP compression with `transcript.json` inside.
- Format-1 and format-2 uncompressed packages remain importable.
- Speaker clusters and confirmed person assignments remain portable; private voice vectors remain local and are never exported.

## Deliberate boundary

This build cleans the data that future diarization and voice identification will learn from. Full multi-speaker acoustic diarization and automatic cross-broadcast voice matching remain the next worker milestone. TinyDiarize continues to be labelled as experimental two-speaker turn detection rather than person recognition.
