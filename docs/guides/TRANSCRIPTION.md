# Radio Vault local transcription — v0.33 alpha3

## Boundary

`TheRadioVault.Transcription` remains platform-neutral. It owns transcript models, SQLite repositories, import/export, durable job coordination, speaker identity, voice-memory evidence and engine contracts. The Avalonia application now composes those services and provides configuration and the Transcripts workspace.

## Avalonia workflow

Open Settings → Transcription, choose a speech model and select **Install recommended setup**. Radio Vault downloads the latest stable official Windows x64 worker, the speech model, Silero VAD and the two ONNX models used for multi-speaker analysis. Separate download buttons remain available.

Then play or select a broadcast, open Transcripts and start either a five-minute sample from the current playhead or a full-broadcast job. Jobs continue in the background. Completed timed phrases can be selected to resume playback at their exact timestamp.

The worker archive is checked against the SHA-256 digest published by the official GitHub release. Archive extraction rejects paths outside Radio Vault's transcription directory, and model downloads are committed only after completion.

Queued or running jobs found after an application restart are marked Interrupted and can be retried.

## Local worker

Alpha3 implements `WhisperCppTranscriptionEngine`, an adapter around an externally installed `whisper-cli.exe`. Radio Vault owns the job lifecycle and launches the native process with explicit argument-list entries, redirected output, progress parsing and process-tree cancellation. Results are read from whisper.cpp full JSON.

The native executable is intentionally not bundled. Models are stored under the Radio Vault application-data Transcription/Models folder. The Settings page can download selected official whisper.cpp model files or point to an existing model.

## Timestamp handling

The worker requests word-level output. Radio Vault retains each timed word, but groups adjacent words into readable phrases using speaker changes, silence gaps, punctuation and maximum phrase length. Double-clicking a phrase starts playback at its exact start time.

## Ranges and durability

A job can cover the full file, a short sample from the current playhead or a custom range. The alpha3 schema-36 job options remain persisted:

- language;
- start and duration;
- diarization request;
- VAD request;
- replacement permission.

Partial results are stored as Draft transcripts. Failed, cancelled and restart-interrupted jobs can be retried with their original options.

## Speakers

When **Identify and separate multiple speakers automatically** is enabled, Radio Vault runs a second local pass after transcription. A Pyannote segmentation model finds speech turns and a NeMo voice-embedding model clusters voices. The speaker count is discovered automatically, so the result can contain Speaker 1, Speaker 2, Speaker 3 and further speakers as needed.

These are anonymous voice clusters, not recognised names. Radio Vault keeps its existing confirmation and voice-memory evidence so a later slice can attach researched people to those clusters safely.

## Privacy

Transcription and model execution are local. Transcript package exports preserve text, timings, anonymous speaker labels and confirmed assignments, but do not export private voice vectors.


## Alpha4 transcript quality layer

Alpha4 adds a deterministic post-processing stage between raw speech recognition and archival storage. It classifies explicit music/silence markers, collapses contiguous non-speech runs, preserves confidence for review, and allows user corrections to be marked as reviewed. Format-3 transcript exchange uses ZIP compression and strips private machine paths from portable metadata. Show metadata and researched people/topics are supplied to whisper.cpp as an initial vocabulary prompt when enabled.
