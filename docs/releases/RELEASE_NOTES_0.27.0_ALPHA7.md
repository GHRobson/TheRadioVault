# Radio Vault v0.27.0-alpha7 — Deep Preservation and Cross-PC Comparison

Version: `0.27.0-alpha7-deep-preservation-and-cross-pc-comparison`  
Database schema: **39**

## Purpose

Alpha7 gathers reliable technical evidence from already indexed recordings and compares two Radio Vault computers without copying or changing their audio. It follows the user-verified alpha6-buildfix2 Research baseline.

## Deep preservation scan

- Adds an explicit resumable scan separate from the normal fast library refresh.
- Fills missing media durations and partial SHA-256 fingerprints for locally available files.
- Retries prior inspection errors when requested.
- Can rebuild evidence for every local recording when explicitly selected.
- Calculates complete SHA-256 hashes only for strong duplicate candidates whose size and partial fingerprint already match.
- Saves evidence after each file, so cancellation or an interrupted app does not discard completed work.
- Records scan history, options, progress, errors and completion state in schema 39.
- Never hydrates cloud-only files automatically.

## Portable archive manifests

- Exports `.trvmanifest` files containing machine identity, library roots, relative paths, logical broadcast identity, duration, size, storage state, partial fingerprints and available full hashes.
- Does not include audio.
- Stores the installation identity outside the database and outside normal backups, preventing a restored laptop database from impersonating the desktop computer.

## Cross-PC comparison

- Compares the current computer directly with a manifest exported from another Radio Vault installation.
- Separates confirmed exact copies, full-hash identity conflicts, strong partial-fingerprint candidates, alternate encodes, possible partial/different coverage, identity-only matches and files unique to either computer.
- Shows evidence and a safe recommendation for every row.
- Exports a portable JSON comparison report.
- Uses show + date + broadcast slot + part as the logical identity boundary.
- Treats full SHA-256 as the only confirmation of a byte-for-byte duplicate.

## Archive Health

Exact full-hash groups that claim different dates, slots, parts or shows are now reported as identity conflicts rather than safe quarantine candidates.

## Safety

Alpha7 does not move, rename, quarantine or delete any recording. Preferred-copy selection and reversible quarantine remain later Archive Intelligence work.
