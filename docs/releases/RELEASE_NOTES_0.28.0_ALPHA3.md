# Radio Vault v0.28.0-alpha3 — Recording Structure & Merge Audit

Alpha3 remains a non-destructive Library Truth shadow release. It does not alter the live library or audio files.

## Recording structure

- Separates complete standalone captures from multipart assemblies beneath the same canonical broadcast.
- Groups exact and strong-copy physical locations into one recording family.
- Classifies complete captures, alternate captures, multipart sets, likely-complete captures, partials, fragments, truncated files and unknown-duration recordings.
- Compares combined multipart duration with the longest preserved capture.
- Identifies incomplete or unexpectedly short multipart assemblies without discarding them.

## Preferred recording evidence

- Ranks one advisory preferred recording candidate per canonical broadcast.
- Scores completeness, duration coverage, preservation evidence, current preferred state and damage/fragment penalties.
- Preserves every alternate, partial, clip and physical copy under the canonical broadcast.
- Explains the score and structural role in the Recording roles view and export.

## Merge and identity audit

- Propagates identical complete hashes across conflicting show/date/slot claims.
- Propagates strong partial-fingerprint + size + duration matches across conflicting identities, including the known 22/23 November 2012 case.
- Blocks conflicting identities from future automatic adoption.
- Flags a focused suspicious-merge set where two substantial standalone captures have unusually different coverage.
- Adds clear evidence explaining why all files were grouped beneath each proposed broadcast.

## Adoption readiness

- Classifies each canonical broadcast as Ready, Ready with recording choice, Review recommended or Blocked.
- Adds year-by-year live-versus-shadow counts, merge groups, split groups and readiness totals.
- Adds a dedicated cross-identity conflict table.
- Exports adoption, year and conflict data in `.trvtruth` schema version 2.

## Interface

- Rebuilds the Library Truth comparison around Physical files, Recording roles, Canonical broadcasts and Adoption audit.
- Adds filters for blocked groups, review recommendations, adoption-ready groups and suspicious merges.
- Loads the large audit views in the background with virtualized tables.

## Database and safety

- Advances SQLite from schema 40 to schema 41 with a pre-upgrade backup.
- Adds recording-role/preference fields, broadcast adoption fields, year summaries and conflict tables.
- Does not modify `episodes`, playback state, research, transcripts, Moments or audio files.
- Provides no adopt, move, rename, quarantine or delete command.
