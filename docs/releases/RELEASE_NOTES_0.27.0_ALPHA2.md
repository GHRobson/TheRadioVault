# Radio Vault v0.27.0-alpha2 — Speaker Identity and Voice Memory

Version: `0.27.0-alpha2-speaker-identity-and-voice-memory`  
Database schema: **35**  
Base: `v0.27.0-alpha1-buildfix1-transcription-foundation`

## What is new

- Added stable anonymous speaker keys to transcript segments and timed words.
- Added durable speaker clusters with **Unassigned**, **Suggested** and **Confirmed** states.
- Added a transcript **Speakers…** window for mapping clusters to hosts, guests, callers, mentioned people or a newly entered person.
- Added local cross-broadcast voice memory: confirmed clusters can queue several clean sample ranges, multiple broadcasts accumulate evidence, and model-compatible samples build revisioned person profiles.
- Added confidence-ranked voice matching. Strong matches can create a suggestion, but cannot silently confirm or overwrite a person.
- Clearing a mistaken assignment rejects its samples and rebuilds the affected profile from remaining evidence.
- Added voice-profile and matching contracts behind a replaceable local engine boundary.
- Advanced `.trvtranscript` packages to format 2, preserving speaker clusters and assignments while remaining compatible with format 1 imports.
- Added speaker counts to transcript lists and identity-aware labels/search in the timed transcript viewer.
- Expanded smoke tests for schema 35, multi-broadcast evidence accumulation, profile correction and speaker-aware package round trips.

## Deliberate limitation

This build does **not** yet install or run a speech, diarization or voice-embedding model. Imported diarized transcripts can use all manual assignment features immediately. Confirmed learning samples remain safely queued until the local worker is connected in the next alpha.

## Upgrade note

Use a copied database for alpha testing. The normal Radio Vault pre-schema backup runs before upgrading a schema-34 database to schema 35. The stable v0.26 playback, research, mobile/offline and PC↔phone ownership behaviour is unchanged.
