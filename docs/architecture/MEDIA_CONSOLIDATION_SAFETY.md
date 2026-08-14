# Media consolidation safety contract

Radio Vault can build one logically organised managed archive from the physical recordings represented by the latest completed Library Truth run. This operation is intentionally conservative. Its purpose is to make a verified copy and retain every original for review—not to free disk space automatically.

## Non-negotiable invariants

- The consolidation service has no API that deletes media or quarantine contents.
- Preview and rehearsal do not move or copy audio.
- Commit is unavailable while Radio Vault Server is running.
- Commit requires the exact phrase shown for the signed plan.
- Every source is verified with a complete SHA-256 digest before it enters a plan and again before commit.
- Every selected managed copy is flushed, re-read and verified before any original is moved.
- Every original, including the original that supplied the winning managed copy, is moved into the plan-specific quarantine and verified there.
- The database is updated only after all managed copies and quarantine moves have succeeded.
- A verified online SQLite backup is retained beside the plan before database rows change.
- A target that already contains different bytes is never overwritten.
- Symbolic-link media and symbolic-link destinations are rejected.
- Managed and quarantine folders must be separate, non-nested locations outside the existing Library roots.
- Held or review-required Library Truth broadcasts are not touched.

The only files the implementation may delete are its own incomplete `.partial` copies, manifest temporary files and write-test probes. It never calls a deletion operation for a source, managed recording or quarantine recording.

## Selection policy

Only broadcasts whose Library Truth adoption state is `Ready` or `Ready with recording choice` are eligible. Dates and recording structure must be known. When a broadcast has several recordings, every recording must have a known runtime.

Radio Vault then ranks complete recording variants in this order:

1. longest total runtime, compared in rounded whole-second buckets;
2. highest estimated average bitrate (`file bytes × 8 ÷ runtime`);
3. Library Truth preferred score;
4. a deterministic full-hash tie-break.

Multipart recordings remain multipart. One file is selected for each segment. Files with the same complete digest as the selected segment are labelled exact duplicates; other lower-ranked recordings are labelled alternates. If files grouped as one recording segment have different complete hashes, the entire broadcast is held for manual review rather than guessed.

## Folder layout

Managed files use this shape:

```text
Managed archive/
  Show/
    YYYY/
      YYYY-MM/
        YYYY-MM-DD - Show - Slot - Title - Part NN.ext
```

The part suffix appears only for actual multipart recordings. Every original goes beneath:

```text
Quarantine/
  RadioVault-Consolidation/
    plan-id/
      Selected Originals/
      Exact Duplicates/
      Alternate Recordings/
      plan.json
      journal.json
      database-before-consolidation.sqlite
      README - REVIEW BEFORE DELETING.txt
```

Quarantine is deliberately outside the indexed Library so rejected files do not reappear as broadcasts. Radio Vault marks rejected media rows as quarantined and unavailable but retains their paths and complete hashes for audit.

## Workflow and recovery

1. Complete a fresh Library Truth run and resolve anything that must not be held.
2. Choose a new managed folder and a separate quarantine folder in Server settings.
3. Prepare the preview. Review managed, rejected and held counts.
4. Run the no-move rehearsal. It re-hashes sources, checks containment, collisions, write access and free space, and writes the signed manifest.
5. Stop Radio Vault Server.
6. Enter the exact plan-specific confirmation phrase and commit.
7. Verify playback from the managed archive and keep an independent backup before considering any manual deletion from quarantine.

The journal is written after every verified phase. If the process or computer stops during commit, select the same quarantine folder after restarting the Server app. It discovers a non-completed journal, reloads the signed plan and requires another full rehearsal and exact confirmation. Existing managed and quarantine files are accepted only when their byte length and complete hash match the plan, making resume idempotent.

## File and broadcast identity audit

Radio Vault uses different identifiers for different jobs:

- `media_files.id` is a local database row identifier. It is not content identity.
- `media_files.path` is a current location. It is never identity.
- `episodes.broadcast_uid` is a legacy display/API identity. Its numeric collision suffix can reflect insertion order, so consolidation does not use it to decide equality.
- The Library Truth canonical key identifies a show/date/slot/part broadcast family.
- A complete SHA-256 digest identifies exact file bytes across paths and machines.
- Partial SHA-256 + byte length + duration is only a strong candidate key. It must not be described as exact identity.
- Archive manifests retain legacy `FileKey` (`machine id:row id`) for compatibility and now also publish `ContentKey`: full SHA-256 when available, otherwise the strong candidate key, otherwise an explicitly local fallback.

The scanner’s former partial-hash-and-size-only reattachment rule was removed during this audit. It now prefers an unambiguous full hash and requires matching duration for the partial-hash fallback. Hash candidates are constrained to a compatible collection/date/slot/part identity: identical bytes carrying conflicting explicit dates remain separate for Library Truth review. Ambiguous matches always create or retain separate episode rows for later reconciliation.

## Manual acceptance before deleting anything

- Keep the quarantine and database backup on independent storage.
- Compare item counts and hashes with `plan.json`.
- Scan the managed folder and confirm Library health.
- Play a sample from every show, year and file type.
- Inspect all `Alternate Recordings`, especially runtime differences.
- Retain quarantine through a normal-use soak period.
- Delete only manually, outside Radio Vault, after the operator is satisfied that the retained backup is restorable.
