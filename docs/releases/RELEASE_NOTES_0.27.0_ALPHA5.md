# Radio Vault v0.27.0-alpha5 — Archive Identity and Parser Reliability

Version: `0.27.0-alpha5-archive-identity-and-parser-reliability`  
Database schema: **37**

## Purpose

The second computer's much larger USB archive exposed parser confidence noise, same-day broadcast-slot mistakes, stale machine paths imported from backups and over-broad duplicate grouping. Alpha5 corrects those foundations before Radio Vault gains any file-moving duplicate workflow.

## User-visible changes

- Known US archive dates with one- or two-digit month/day components are high-confidence.
- OpieRadio recordings appear as their own same-day broadcast slot.
- Structural `Midday` and `OpieRadio Edition` headlines are removed when their filenames are rescanned.
- Explicit show names can correct files stored inside the wrong show's folder.
- Cross-PC restore keeps the destination machine's registered library folders and instructs the user to rescan.
- Archive Health separates exact hashes, strong candidates, alternate editions/encodes and tiny-file integrity warnings.

## Safety

This release does not move, rename, quarantine or delete audio. Duplicate findings are advisory. Imported paths from another computer are retained as unavailable historical locations until the local scan creates a current file location.

## Testing focus

Use the larger external-drive installation as the primary parser/health test and the laptop as the regression test. Export diagnostics from both after a full scan.

## Recovering the already-restored second PC

Alpha5 prevents future cross-PC restores from overwriting destination library roots. It cannot reconstruct a USB path that alpha4 already discarded. On the existing second-PC database, verify Settings first; remove/disable stale laptop OneDrive roots, add the external-drive roots, then perform a full scan. The new relinking rules preserve restored playback and research while attaching the local files.
