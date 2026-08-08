# v0.28.0-alpha1 validation

## Build

- [ ] Build `TheRadioVault.sln` in Visual Studio 2022 Release / Any CPU.
- [ ] Run `release-gate.ps1`.
- [ ] Confirm version `0.28.0-alpha1-library-truth-shadow-index`.

## Migration and regression

- [ ] Launch the accepted alpha7-buildfix1 database.
- [ ] Confirm a pre-schema-40 backup is created and the app opens normally.
- [ ] Confirm Library, Research, Playback, Transcription, Web access and Preservation still work.
- [ ] Confirm Settings and Preservation remain responsive on the desktop archive.

## Shadow analysis

- [ ] Open **Settings → Library truth** and build the shadow library.
- [ ] Confirm the operation runs as a cancellable background job and does not freeze navigation.
- [ ] Cancel once, verify the live library remains unchanged, then run again to completion.
- [ ] Confirm the summary reports physical files, current broadcasts, proposed broadcasts, recovered dates, unknown dates and review count.
- [ ] Open all three tabs: Physical files, Recording variants and Canonical broadcasts.
- [ ] Inspect examples of variable-width dates, AFRO two-digit years, OpieRadio/OR, AM/PM, Roman multipart sets and genuinely unknown dates.
- [ ] Confirm multipart files share one canonical broadcast and one provisional recording family where appropriate.
- [ ] Confirm exact duplicate physical files do not create additional canonical broadcasts.
- [ ] Confirm no adoption, move, rename, quarantine or delete action exists.

## Export

- [ ] Export a `.trvtruth` report.
- [ ] Confirm it contains summary, files, recordings and broadcasts but no audio content.
- [ ] Provide the desktop export for corpus-level parser review.
