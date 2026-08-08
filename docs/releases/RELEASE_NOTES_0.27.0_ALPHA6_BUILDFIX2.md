# Radio Vault v0.27.0-alpha6-buildfix2

Version: `0.27.0-alpha6-buildfix2-reconciliation-performance`

## Purpose

Buildfix2 addresses the desktop-only freeze seen with the 7,169-file USB archive. The audio scan was not stuck on file 7,150: after the last visible progress update, Radio Vault synchronously ran the entire restored research-reconciliation queue. The same expensive pass also ran on the WPF UI thread whenever the Research workspace or Research decisions window opened.

## Changes

- Library scans now finish after media enumeration and database reconciliation. Research triage is no longer run as part of scan completion.
- The main Research workspace no longer starts automatic triage synchronously.
- The Research decisions window performs triage and grouped-query loading on background threads and remains responsive while analysis is running.
- Reconciliation passes are serialised so scan, workspace and decision-window actions cannot contend for SQLite simultaneously.
- Already-attached research suggestions are cleared with one set-based database operation.
- Identity/source-only portable records are linked to a canonical logical broadcast in one transaction rather than thousands of full metadata applications.
- Structured station, slot, variant, era and episode-type values are filled only when the local field is empty and not manually protected.
- Candidate details are queried directly by candidate ID instead of rereading the complete candidate table for every match.
- The Needs your decision view loads pending candidates only; completed history is loaded only when requested.
- Scan-completion wording now explains that grouped research matches are queued for background analysis.

## Preserved behaviour

- Buildfix1 Roman multipart parsing and AM/PM/evening slot normalisation remain unchanged.
- Timed Moments still require review when recording timelines may differ.
- Manual holds and unresolved metadata conflicts remain manual.
- No audio is moved, renamed, quarantined or deleted.
- Database schema remains 38.
