# Radio Vault v0.27.0-alpha6-buildfix1

Version: `0.27.0-alpha6-buildfix1-multipart-reconciliation`

## Purpose

This buildfix addresses the thousands of misleading Research decisions exposed by the larger desktop archive.

## Changes

- Identity-only and source-only rows exported for broadcasts with no episode-applicable research payload are resolved automatically and no longer require manual review.
- Current candidate evidence is recalculated when the Research decisions window opens, removing the old false confidence boost from date-label aliases.
- Bare Roman multipart suffixes such as `I`, `II`, `III` and `IV` are recognised as parts and removed from generated headlines.
- Research candidates for a different multipart segment are dismissed automatically when a matching part exists.
- PM, afternoon, evening and PM clock-range labels compare as the same later-day slot; AM remains distinct.
- When an explicit slot match exists, unspecified or conflicting same-day slots no longer remain in the decision.
- Multiple files and multipart segments with the same show, date and normalised slot are treated as one logical broadcast family; an exact part is preferred when available, but stale synthetic part numbering no longer forces a decision.
- Broadcast-level research without timed Moments is applied once to a sensible representative of that family; other recordings are recorded as automatically handled variants.
- Research containing timed Moments remains reviewable when several captures could have different timelines.
- Candidate cards now show the candidate recording's actual slot, part and duration instead of repeating the saved research identity for every option.

## Safety

- No audio file is moved, renamed, quarantined or deleted.
- Existing manual decisions and undo holds remain manual.
- Manually edited episode fields remain protected.
- Timed Moments are not silently copied between ambiguous recording timelines.
