# Metadata System

## Metadata packages

Radio Vault exports versioned `.trvmetadata` packages for whole-library metadata exchange. Packages include structured broadcast, people, topic, research, archive and Moment information while leaving audio files and playback history untouched.

## Metadata source priority

Library scans treat embedded audio tags as a low-priority source. Performers and genres may seed a newly discovered broadcast only when its corresponding guest/topic list is empty and the broadcast has not been imported or manually edited. A later scan must not replace structured research-pack people, topics, headlines or summaries.


## Research-pack integrity

Research-pack exports include saved source URLs and Moments as well as the structured broadcast, people, topic and confidence fields. Replacement imports treat a missing replacement headline or summary as an intentional clear, allowing a corrected pack to remove stale generic text. Guest and topic values are imported as structured arrays, so commas inside a name or topic are preserved.

## Structured fields

The metadata model supports:

- Broadcast identity: show, date, title, summary, station, slot, variant, era and episode type.
- People: hosts, guests, callers and mentioned people.
- Discovery: topics and tags.
- Research: confidence, reasoning and sources.
- Archive information: original filename, notes and stable identifiers.
- Moments linked to broadcasts.

Broadcast Info only renders populated people categories and hides the complete People section when none exist.

## Matching and import safety

Imports attempt matches in this order:

1. Stable broadcast UID.
2. Original filename.
3. Conservative show, date and part matching.

Unmatched or ambiguous records are skipped. Import does not overwrite playback position, completion state, favourites or file paths.

## Metadata cleanup

The cleanup engine can audit external packages or the live library. It can:

- Remove generic or technical boilerplate from visible summaries and headlines.
- Remove show names incorrectly stored as guests.
- Normalise station and network names.
- Move overloaded values into era, variant or episode type where confidence is high.
- Trim, deduplicate and sort people, topics, tags and sources.
- Infer a limited set of conservative eras and episode types.

Live audits create a timestamped backup and audit report before applying changes and can be undone using the most recent pre-audit package.

Current explicit cleanup rules include removal of the headline `Faction Talk archive broadcast` and removal of `Bennington` from Bennington guest lists when it represents the programme rather than a person.

## Presentation

A shared presentation service chooses meaningful titles, summaries, people lines, topic chips and fallbacks. Generic show-and-date titles and boilerplate research text should not dominate Library or Home views.


## Research Pack Schema 2

Research packs now use structured `research.broadcast`, `research.people`, and `research.quality` objects. Legacy `research.edition` and `research.guests` properties remain accepted on import. New packs should classify people as hosts, guests, callers, or mentioned people and should populate station, slot, variant, era, and episode type only when supported by sources.

## Durable research records and Broadcasts to find

Schema 28 separates research identity from media availability. `research_broadcasts` is the durable record; its optional `episode_id` attaches it to playable audio without making the audio file the owner of the research. Structured sources, people, topics, Moments and aliases are stored in child tables. Raw schema-v3 research JSON is also retained for compatible export and audit.

The legacy `missing_broadcast_research` ledger remains as a compatibility and revision-history layer for the current browser. Existing ledger records migrate automatically into the durable research store, and status or manual-attachment actions are synchronised between both layers. The normal UI presents unattached records as Broadcasts to find; they never become fake playable episodes or imply that Radio Vault lost a file.

Existence classification is conservative: a record is `confirmed_missing` only when it has at least one source and confidence of 85 or more; sourced records from 60–84 are `probable_missing`; everything else remains an `unknown_gap`.

Post-scan reconciliation scores stable broadcast IDs, show/collection, exact date, slot, part and aliases. Scores of 65 or more are retained for explicit review; scores of 90 or more are labelled strong matches but do not attach automatically. The review workflow protects `user_modified` metadata, requires confirmation before replacing populated scalar fields and records an undo change set for every approval.


## Reconciliation policy (v0.26.0-alpha4)

A library scan may create a reconciliation candidate when show identity, broadcast date, part, slot, stable broadcast ID or a retained filename alias supports a match. A candidate never modifies episode metadata by itself. The review window lets the user attach provenance while independently selecting scalar metadata, people, topics and Moments. Existing manual edits are visible and are not preselected for replacement.

## Research-quality repair metadata

Quality findings may now identify a `SafeFixKind` and value. These are suggestions only in this build; imports, scans and audits do not automatically alter user or researched metadata.

## Research quality repair history (schema 31)

Deterministic quality repairs are recorded with before/after snapshots. A repair can be undone only if the affected record has not changed since it was applied.
