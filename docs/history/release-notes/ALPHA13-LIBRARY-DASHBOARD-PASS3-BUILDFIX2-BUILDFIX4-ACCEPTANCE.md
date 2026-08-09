# Alpha 13 Pass 3 Buildfix 2 Buildfix 4 — All-Show Date Review

## Build

1. Extract the package into a fresh folder.
2. Run `BUILD-AND-RUN.cmd`.
3. Confirm the console title says `Pass 3 Buildfix 2 Buildfix 4 All-Show Date Review`.
4. Confirm the Avalonia project builds and Radio Vault opens.

## Date-review coverage

Open **Research → Date review** and check the show filter. The guarded workflow must be available for:

- Ron & Fez
- Bennington
- Opie & Anthony
- The Ron & Ron Show
- Unmasked
- Ron Bennington Interviews

`Unsorted` is intentionally excluded because it is a holding collection rather than a curated show chronology.

## What should appear

A linked Research record should appear when any of the following is true:

- the Library date is missing;
- the date confidence is not settled (`Unknown`, `Ambiguous`, `Probable`, `Low`, or another non-authoritative value);
- Research proposes a date that conflicts with the current Library date;
- recording or release/archive evidence conflicts with the current Library date;
- an earlier Research build adopted a date automatically and it has not yet been explicitly reviewed;
- the research pack explicitly marks the date as pending or reopened.

A broadcast with a matching `High`, `Confirmed`, or `Manual` Library date should remain out of the pending queue unless an explicit or conflicting date decision exists.

## Decisions

For one item from a normal daily show and one catalogue-style show, test:

- **Approve as Library date** — exact date updates Library grouping and canonical projections.
- **Keep as recording date** — evidence remains in Research without silently changing a trusted Library date.
- **Keep as release/archive date** — evidence remains in Research without silently changing a trusted Library date.
- **Leave Library item undated** — date is removed deliberately and the decision is preserved.
- **Reopen this decision** — restores the date state that existed before the decision.
- **Custom exact date** — the date picker can be used even when no proposed exact date exists.

## Pack round-trip

1. Resolve a date decision for Ron & Fez, Bennington, or Opie & Anthony.
2. Export a schema-5 research pack.
3. Import it into a fresh test library containing the matching broadcast.
4. Confirm the decision status, proposed date, prior Library state, and provenance survive.
5. Confirm an unresolved conflicting date is shown for approval rather than silently adopted.

## Regression checks

- RBI, Unmasked, and Ron & Ron retain their extra caution around release, recording, and archive dates.
- Partial month/year and year-only clues remain partial and do not invent a day.
- Local playback and progress still work.
- Radio Vault Anywhere still starts and streams.
- The sidebar activity panel still reports long-running work.
- The Library mini panel remains simplified for RBI, Unmasked, and Ron & Ron.
