# v0.28.0-alpha3 validation

## Build

- [ ] Open `TheRadioVault.sln` in Visual Studio 2022.
- [ ] Build **Release / Any CPU** with no errors.
- [ ] Run `release-gate.ps1`.
- [ ] Confirm application version `0.28.0-alpha3-recording-structure-and-merge-audit`.

## Migration and safety

- [ ] Launch with the accepted alpha2 desktop database.
- [ ] Confirm a pre-schema-41 backup is created.
- [ ] Confirm the existing Library, playback, favourites, research, transcripts, Moments and Preservation data remain intact.
- [ ] Confirm there is no action to adopt the shadow model or modify audio files.

## Shadow build

- [ ] Open **Settings → Library truth** and build a fresh shadow library.
- [ ] Confirm the application remains responsive and the scan completes against the 7,000+ file desktop archive.
- [ ] Confirm the expected four genuinely undated files remain unknown rather than receiving invented dates.
- [ ] Confirm only genuine parser/identity conflicts are blocked.

## Recording roles

- [ ] Confirm full standalone captures are not absorbed into multipart assemblies.
- [ ] Confirm complete multipart sets display their combined duration and segment count.
- [ ] Confirm multipart sets substantially shorter than a full capture are labelled likely-complete or partial rather than automatically complete.
- [ ] Confirm the 8.9-second and 38-second files are classified as fragments/truncated and are not preferred.
- [ ] Confirm exactly one preferred candidate is ranked for each broadcast with recordings.
- [ ] Confirm all alternate, partial and fragment recordings remain preserved.

## Merge and conflict audit

- [ ] Inspect **Suspicious merges** and confirm the queue is small and understandable.
- [ ] Check dates with combined/long recordings and same-day specials, particularly 4 September 2014.
- [ ] Confirm exact-audio conflicting dates remain separate and blocked.
- [ ] Confirm the 22/23 November 2012 strong audio match appears in the conflict audit.
- [ ] Confirm ordinary alternate encodes do not require manual review.

## Adoption audit

- [ ] Confirm readiness cards populate for Ready, Review recommended and Blocked.
- [ ] Confirm year-by-year live and shadow totals are displayed.
- [ ] Confirm filters apply consistently to files, recordings and broadcasts.
- [ ] Export a `.trvtruth` report and confirm schema version 2 includes `adoption`, `years`, `conflicts`, recording roles and preferred scores.

## Regression

- [ ] Settings and Preservation open without the alpha7 performance regression.
- [ ] Library scan and Research reconciliation still complete on the desktop archive.
- [ ] Playback, resume, transcription and web-server functions still launch normally.

## Source-package validation performed in the generation environment

- Parsed 191 C# files structurally; no new syntax errors were found.
- Parsed all XAML and project XML successfully.
- Verified every XAML event handler resolves somewhere in the WPF partial class source.
- Executed the schema-41 Library Truth SQL against synthetic SQLite data.
- Simulated an existing schema-40 Library Truth database upgrading to schema 41, including delayed creation of the recording-role index after the new columns exist.
- Executed representative adoption-summary, year and conflict queries.
- Verified version/project metadata, required alpha3 capabilities and source-package hygiene.

The generation environment does not contain the .NET SDK, so Visual Studio remains the authoritative compilation and runtime test.
