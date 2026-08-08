# Radio Vault v0.27.0-alpha1 — Transcription Foundation

This is the first development build after v0.26 Archivist. It creates the durable transcript architecture before a specific local speech model is integrated.

## Included

- New platform-neutral `TheRadioVault.Transcription` project.
- Engine, repository, coordinator and exchange contracts.
- Database schema 34 with transcripts, timed segments, durable jobs and import provenance.
- Background transcription job plumbing with cooperative progress and cancellation.
- Versioned `.trvtranscript` import/export format with strict timing validation, broadcast-identity protection and SHA-256 import provenance.
- New desktop Transcripts workspace showing stored transcripts and job history.
- Inline transcript viewer with local search and double-click playback seeking.
- Transcript status, import, export and future transcription controls in Broadcast Info.
- Existing v0.26 research, playback, mobile ownership and offline systems preserved unchanged.

## Deliberately not included yet

- No bundled transcription model or native runtime.
- The Transcribe control remains unavailable until a concrete local engine is connected.
- No speaker diarization, transcript correction editor, full-library transcript search or Archive Intelligence yet.

## Migration

Opening this build upgrades the copied application database from schema 33 to schema 34. Radio Vault's normal pre-schema backup mechanism runs before migration. Test this alpha against a copied library/database until the new transcription branch is accepted.
