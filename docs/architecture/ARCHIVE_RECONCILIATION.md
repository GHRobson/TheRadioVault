# Archive reconciliation

Archive Reconciliation is the Radio Vault Server's read-only understanding of the physical archive. It replaces “Library Truth” as the product and UI boundary without discarding the mature parsing, evidence and recording-ranking rules that already have extensive regression coverage.

## Ownership and visibility

- The feature is owned by Radio Vault Server.
- It is not exposed in the desktop, iPhone or Web clients.
- The Server dashboard shows only the latest status and actionable counts.
- Detailed evidence, a fresh-analysis command and media consolidation live on the dedicated **Archive Reconciliation** screen.

## Service boundary

`ArchiveReconciliationService` is the stable orchestration boundary. It returns an `ArchiveReconciliationSnapshot` containing physical-file coverage, canonical broadcast counts, proposed corrections, duplicate evidence and readiness totals.

The existing `LibraryTruthEngine` is currently an implementation detail behind that service. This allows the older monolithic engine to be split incrementally into inventory reading, interpretation, recording analysis and shadow-index persistence without forcing another Server UI rewrite or exposing legacy terminology to new code.

## Safety model

A normal reconciliation:

1. reads the authoritative `media_files` inventory;
2. interprets filenames and folder context;
3. constructs canonical broadcast and recording evidence;
4. writes only versioned shadow-index tables;
5. reports review and blocked work.

It does not rename, move, merge or delete files. It does not alter the live client library or listening state. A cancelled or failed run leaves the last completed snapshot available.

Media consolidation consumes a fresh completed reconciliation but remains a separate, explicitly confirmed workflow with its own rehearsal, signed plan, verified backup, journal and quarantine contract.

## Planned internal decomposition

The compatibility engine should be reduced behind the façade in small tested stages:

- `ArchiveInventoryReader`: authoritative physical file rows and coverage signature;
- `ArchiveInterpretationService`: parser plus folder-context analysis;
- `RecordingEvidenceAnalyzer`: multipart, alternate, duplicate and quality ranking;
- `ReconciliationIndexStore`: transactional shadow-run persistence and queries;
- `ArchiveReconciliationService`: cancellation, progress and stable server status.

Parser rules and identity/ranking policies must keep their existing behavioural tests during each extraction. No internal refactor may weaken the complete-inventory equation required by consolidation.
