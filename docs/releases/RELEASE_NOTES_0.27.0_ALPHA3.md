# Radio Vault v0.27.0-alpha3 buildfix1 — Model Download Finalisation Fix

Version: `0.27.0-alpha3-buildfix1-model-download-finalisation`  
Database schema: **36**

## Buildfix1 correction

- Closes the completed model download stream before renaming the temporary file.
- Uses per-download temporary files so a stale alpha3 `.download` file cannot block another attempt.
- Retries finalisation for short-lived Windows antivirus, indexing and cloud-sync locks.
- No database, transcript, playback, research or mobile behaviour changes.

## What this alpha adds

- Connects the durable transcription job system to a real, local `whisper.cpp` command-line worker.
- Adds **Settings → Transcription** for selecting `whisper-cli.exe`, choosing a local model, configuring language, CPU threads, GPU use, VAD and TinyDiarize.
- Adds direct downloads for a small curated set of official whisper.cpp model files. The native executable itself is not bundled.
- Enables full-broadcast transcription, a 10-minute test from the current playhead, and custom time ranges.
- Persists language, range, diarization, VAD and replacement options with every durable job.
- Adds retry for failed, cancelled and restart-interrupted jobs.
- Produces word-level timestamps, then groups them into readable time-linked phrases for the transcript viewer.
- Supports experimental anonymous two-speaker turn labels with a compatible `*-tdrz` TinyDiarize model.
- Preserves alpha2 speaker assignment and cross-broadcast voice-memory evidence. Acoustic voice embeddings and broader multi-speaker diarization remain the next engine slice.

## Configuration

1. Obtain a Windows build of `whisper.cpp` containing `whisper-cli.exe`.
2. Open **Settings → Transcription** and select the executable.
3. Download or select a model. **Base English** is the recommended first test; **Small English TinyDiarize** enables experimental speaker-turn detection.
4. Press **Save and test configuration**.
5. Open Broadcast Info and press **Transcribe**.

All processing is local. Radio Vault sends no audio or transcript text to an online service.

## Upgrade notes

Opening a copied alpha2/schema-35 database creates the normal pre-schema backup and upgrades it to schema 36. Playback, research, mobile/offline and PC↔phone ownership behaviour are unchanged from the accepted v0.26 baseline.
