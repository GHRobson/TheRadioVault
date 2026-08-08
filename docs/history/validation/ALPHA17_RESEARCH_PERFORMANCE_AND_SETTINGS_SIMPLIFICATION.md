# Radio Vault v0.28.0-alpha17 — Research Performance and Settings Consolidation

Version: `0.28.0-alpha17-research-performance-settings-consolidation`  
Schema: `45`

## Why this release exists

Laptop testing of Alpha 16 exposed two design mistakes:

1. A corpus-level repeated-summary signal was expanded into thousands of apparent errors and counted under Needs your decision, even though the decision screen could not resolve it.
2. Opening Research & Metadata synchronously loaded the complete research browser, and the background quality audit then performed a full detail query for every record.

Alpha 17 fixes the model rather than merely hiding the count.

## Research decision contract

Needs your decision now contains only:

- unresolved side-by-side metadata conflicts;
- quality findings attached to an in-library episode, with a supported route to the exact Metadata editor record.

A quality finding is not counted merely because its severity is Warning or Error. It must be explicitly marked `IsDecisionActionable`. The current editor-resolvable rules are generic summaries and contradictory person roles on attached broadcasts.

Items that require source research, classification work, a future research-record editor, or broad corpus review remain diagnostics and do not inflate the decision badge.

## Repeated summaries

Identical wording across dates can indicate boilerplate, but it can also be valid for recurring clips, shared source descriptions, multi-day stories or repeated archive notes. Alpha 17 therefore:

- groups identical normalised summaries;
- emits one `duplicate-summary-pattern` diagnostic per text pattern;
- shows an excerpt and the number of affected records;
- never creates one decision per affected broadcast;
- never counts the pattern in the Research navigation badge by itself.

Broadcast-specific generic wording can still create an actionable item when the exact attached episode can be opened and edited.

## Fast audit snapshot

The old desktop adapter loaded all research rows and then called `GetResearchLibraryRecordDetails` once per row. That produced thousands of SQLite connections and multiple follow-up queries per record.

Alpha 17 adds `GetResearchAuditRecords`, which constructs the same platform-neutral audit input using four ordered reads:

1. research broadcasts and show identity;
2. research people;
3. research topics;
4. research sources.

The quality engine remains in `TheRadioVault.Research` and still receives platform-neutral records.

## Lazy Research workspace

Opening Research & Metadata now loads only lightweight overview counts and unresolved conflicts. It always returns to Needs your decision for normal navigation.

The following data is loaded only when requested:

- all research rows for Broadcasts to find;
- import history;
- source summaries;
- full quality findings and repair history.

A full quality run is available from Advanced diagnostics or Recheck. Ordinary entry into Research does not start one automatically. Safe repairs remain guarded and logged whenever a requested maintenance run is performed.

## Consolidated Settings

The old Library, Library Truth, Storage, Preservation and Backup destinations represented overlapping parts of archive stewardship. The visible Settings navigation is now:

1. Archive
2. Playback
3. Appearance
4. Transcription
5. Web access
6. Advanced

Archive vertically combines:

- watched folders and scanning;
- archive health;
- storage availability;
- preservation evidence and cross-PC comparison;
- database backup and restore;
- library metadata package tools.

Advanced contains Library Truth shadow analysis and troubleshooting. No feature has been removed; only its information architecture has changed.

## Safety boundary

- Database schema remains 45.
- No migration is required.
- No audio file is moved, renamed or deleted.
- Export formats are unchanged.
- Canonical playback and held-group behaviour are unchanged.
