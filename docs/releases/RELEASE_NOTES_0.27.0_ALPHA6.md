# Radio Vault v0.27.0-alpha6

Version: `0.27.0-alpha6-research-reconciliation-and-triage`  
Database schema: **38**

## Purpose

The larger desktop archive exposed a reconciliation design problem: Radio Vault produced thousands of possible file-level research matches and presented most of them as if they needed manual approval. Alpha6 changes the system from a candidate inbox into a decision workflow.

## What changed

### Automatic safe reconciliation

- Research already attached to an available broadcast no longer generates approval requests simply because other files or same-date broadcasts exist.
- Unattached research is applied automatically only when one deterministic show/date/broadcast-slot/part match exists.
- Cross-PC research whose old physical episode location is unavailable may relink to one exact local broadcast.
- Automatic merges add people, topics and Moments without deleting existing information.
- Scalar broadcast details are filled only when the destination field is blank; confirmed manual edits are protected.

### Grouped manual decisions

- Remaining ambiguity is grouped into one decision per researched broadcast rather than one row per candidate file.
- Each decision explains the problem, gives a recommended action and shows the relevant candidate recordings.
- A Research Library record with an unresolved match provides a direct **Resolve broadcast match…** action.
- Regular, Midday, OpieRadio and multipart identities remain distinct.
- Applying one candidate dismisses the other suggestions in that group.
- **Leave research unlinked** preserves the research for later without forcing a possibly wrong match.
- Existing-vs-incoming headline, summary and station conflicts can be resolved directly without editing the audio file.

### Clearer Research workspace

The Research navigation is organised around:

- **Research library**
- **Needs your decision**
- **Missing broadcasts**
- **Quality checks**

The decision window includes pending decisions, automatic activity and complete decision history. Metadata-conflict cards now include **Keep library value** and **Use research value** actions, so every surfaced conflict has a direct remedy. Archive Health and Research badges now count genuine grouped decisions and unresolved metadata conflicts rather than raw candidate rows.

### Audit and undo

Schema 38 records whether a candidate requires review, the reason category, Radio Vault's recommendation and whether a decision was automatic or manual. Approved matches can be undone. An undone automatic match moves to manual hold so it is not silently applied again on the next triage pass.

## Preserved foundations

- Alpha5 archive identity, parser, USB-storage detection and cross-PC restore fixes remain intact.
- Alpha4 transcription quality, compressed transcript packages, speaker identity and cross-broadcast voice memory remain intact.
- Missing-broadcast research remains preserved for recordings that may be added later.

## Safety

Alpha6 does not move, rename, quarantine or delete audio files. Duplicate cleanup remains future Archive Intelligence work.
