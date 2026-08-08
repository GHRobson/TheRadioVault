# Radio Vault v0.28.0-alpha2 — Library Truth Corpus Corrections

Version: `0.28.0-alpha2-library-truth-corpus-corrections`

This remains a shadow-only Library Truth release. It does not modify the live library, playback state, research, transcripts, or audio files.

## Why this release exists

The first desktop `.trvtruth` export covered 7,169 physical files. It proved that the Broadcast → Recording → Physical File model worked, but also exposed several corpus-level problems:

- 2,326 canonical broadcasts were incorrectly marked as needing attention merely because they had more than one recording variant.
- 291 already dated recordings became unknown in Parser V2.
- four indexed named-month filenames were assigned impossible future dates because a source-track number was mistaken for a day.
- compact `P1`/`P2` and trailing numeric segments were not consistently recognised.
- thousands of proposed headlines consisted only of the show name because underscores prevented alias cleanup.
- AFRO was treated as a collection even when an explicit parent show was present.
- PM identities could be reported as changed because the comparison removed the wrong `|P` token.
- exported evidence and warning text could render as blank bullets.

## Corrections

- Multiple audio families under one show/date/slot are now normal recording variants and do not require manual review by themselves.
- Year hints are learned from labelled folders such as `Ron & Fez 2003`, not only folders named exactly `2003`.
- Compact `RaF1003`-style month/day filenames use that folder year.
- Indexed named-month files such as `36 _1st Oct, 2009.m4a` parse correctly.
- Leading source indices no longer become dates or multipart numbers.
- Named-month parsing requires a real four-digit year, preventing `27 March 16, 2010` from becoming 27 March 2016.
- Indexed short dates such as `41 _07_30_10.m4a` are recognised.
- Compact `P1`/`P2` and date-following `.1`, `-2`, or ` 2` multipart markers are recognised.
- `take 2`, `copy 2`, and version labels remain recording variants rather than multipart segments.
- Semantic parsing normalises underscores, dots, and separators before show/slot/headline analysis.
- Base show names are removed cleanly from headline candidates.
- AFRO remains a programme-format signal when Ron & Fez, Opie & Anthony, or Bennington is explicitly named; dedicated AFRO-only folders can still use the AFRO collection identity.
- `Benningotn` and `AFRO Sho` archive spelling variants are recognised.
- PM/current identity comparison no longer truncates at the `PM` token.
- Needs-attention filters include only genuinely actionable records.
- Exported evidence and warnings deserialize case-insensitively and render correctly.

## Expected desktop result

Against the supplied alpha1 report, the corrected date rules account for every Parser V2 date regression:

- 291 previously known dates should be recovered.
- only the four genuinely unresolved recordings should remain undated.
- the three exact-audio/conflicting-date cases should remain protected for review.
- ordinary alternate recordings should no longer inflate the review count.

The exact canonical-broadcast count will change because the recovered 2003 multipart files can now be grouped by their real dates and slots.
