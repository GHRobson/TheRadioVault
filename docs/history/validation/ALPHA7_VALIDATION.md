# v0.27.0-alpha7 validation

## Build and migration

1. Build the complete solution in Visual Studio 2022, Release / Any CPU.
2. Open the accepted alpha6-buildfix2 database.
3. Confirm the automatic schema-39 upgrade completes and creates a pre-schema-39 database backup.
4. Confirm playback, research, transcripts, speaker identity, voice memory, favourites and Moments remain unchanged.

## Deep preservation scan

1. Open **Settings → Preservation**.
2. Confirm the laptop reports its previously unfingerprinted recordings under **Missing evidence**.
3. Start the recommended scan with missing evidence, retry errors and full hashes for strong candidates enabled.
4. Confirm the app remains responsive and the task can be cancelled from the main status bar.
5. Cancel partway through, reopen the scan and confirm completed files are skipped.
6. Complete the scan and confirm Missing evidence falls to zero or only genuine inspection errors remain.
7. Confirm ordinary **Scan library** remains fast and does not repeat the deep preservation pass.

## Manifest and comparison

1. Complete a deep preservation scan on the laptop and desktop.
2. Export a `.trvmanifest` from each computer.
3. On the desktop, choose **Compare another PC manifest…** and select the laptop manifest.
4. Confirm exact copies require matching full SHA-256 values.
5. Confirm matching full hashes with different date/slot/part identities appear under identity conflicts.
6. Confirm different-duration files with the same logical broadcast appear as possible partial/different coverage.
7. Confirm unique-to-laptop and unique-to-desktop recordings are visible separately.
8. Confirm filtering and report export work with the full archives.
9. Confirm importing a manifest exported from the same installation is rejected.

## Safety regression

- No audio path changes.
- No file moves, renames, quarantine or deletion.
- Alpha6 research triage remains responsive and retains its accepted small manual queue.
- Alpha5 parser, OpieRadio, multipart and external-drive behaviour remain intact.
- Alpha4 transcription packages and voice-memory behaviour remain intact.
