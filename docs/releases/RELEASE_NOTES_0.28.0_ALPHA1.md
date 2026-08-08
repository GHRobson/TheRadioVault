# Radio Vault v0.28.0-alpha1 — Library Truth Shadow Index

Version: `0.28.0-alpha1-library-truth-shadow-index`  
Database schema: **40**

## Added

- A standalone, evidence-bearing Parser V2 rather than another patch to the live filename parser.
- Folder-context learning for dominant show, year folders and date ordering.
- Robust recognition of ISO, variable-width, two-digit-year, named-month and compact RaF dates.
- Same-day slot parsing for Morning, Midday, Evening, Late and OpieRadio broadcasts.
- Multipart parsing for labelled parts, x-of-y forms, Roman numerals I–X and A/B suffixes.
- A canonical shadow model separating Broadcasts, Recording variants and Physical files.
- Provisional recording-family construction using preferred-file state, duration, partial hashes and full hashes.
- Exact-copy and strong-candidate evidence without offering destructive actions.
- A Settings entry and virtualized comparison window for files, recordings and broadcasts.
- `.trvtruth` export with parser evidence, warnings and all three canonical layers.
- Schema-40 shadow tables and indexes, retained independently from the live library.

## Preserved

- Alpha7 deep preservation, manifest comparison and responsive Settings/Preservation behaviour.
- Alpha6 research reconciliation and triage.
- Alpha4 transcription, portable transcript packages, speaker identity and voice memory.
- Playback progress, favourites, research, Moments and web access.

## Deliberately not included

- Adopting Parser V2 results into the live library.
- Moving, renaming, deleting or quarantining audio.
- Automatically selecting a preferred recording.
- Migrating listening state or research between identities.

Those actions remain blocked until the complete desktop shadow report has been inspected and Parser V2 has been hardened against the real archive corpus.
