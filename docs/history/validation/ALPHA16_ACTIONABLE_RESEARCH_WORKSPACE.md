# Radio Vault v0.28.0-alpha16 — Actionable Research Workspace

Version: `0.28.0-alpha16-actionable-research-workspace`  
Schema: `45`

## Purpose

Alpha 15 successfully combined Metadata Studio and Research, but it still exposed too much of the internal research model. Alpha 16 reduces the normal workspace to outcomes the listener can understand and act on.

## Everyday workspace

Only three primary destinations remain visible:

1. **Needs your decision**
2. **Broadcasts to find**
3. **Metadata editor**

The former Overview, Research library, Sources and Quality checks destinations are no longer primary navigation. Source evidence remains visible on individual broadcast leads, import history remains available beside the pack tools, and technical audit data is explicitly labelled as Advanced diagnostics.

## Needs your decision

The fixed, no-scroll conflict comparison from Alpha 15 remains the main design language. It still shows the current library value and saved research value side by side with immediate keyboard or mouse choices.

Alpha 16 also allows non-automatic quality findings to appear in the same destination as plain-language metadata issues. Each issue states what is wrong, why it matters and the suggested correction, with direct access to the relevant metadata editor or broadcast lead.

## Automatic quality housekeeping

Opening Research & Metadata starts a background audit without blocking the workspace. Findings marked safe by the existing quality engine are applied automatically through the same guarded repair service used by the old Quality checks page. Each repair:

- is transactional;
- is recorded in `research_quality_actions`;
- remains guarded by the existing undo checks;
- never overrides protected or user-modified fields when the repair service forbids it.

The audit is rerun after safe repairs. Remaining warning/error findings become actionable items under Needs your decision. Informational findings stay in Advanced diagnostics.

## Broadcasts to find

This is not an error page and does not claim that Radio Vault lost a file. It lists research-supported broadcasts whose audio is not currently attached to the archive. The page explicitly says that nothing has been removed or lost.

Labels use positive discovery language:

- Confirmed broadcast
- Strong research lead
- Broadcast lead

The view is always limited to unattached discovery leads. The confusing all-record Research library mode and status selector are removed from normal use. Users can inspect evidence, copy source links, export the research or attach it when they acquire the recording.

Archive Health uses the same language and treats these entries as suggestions or information rather than archive-loss warnings.

## Advanced utilities

Research-pack import/export remain permanently visible. Import history and Advanced diagnostics are retained underneath them for rollback, troubleshooting and forensic inspection, but they do not compete with the three everyday destinations.

## Safety boundary

- Database schema remains 45.
- Research-pack and metadata export formats are unchanged.
- No audio file is moved, renamed or deleted.
- Automatic repairs are limited to findings already classified as safe by the existing quality engine.
- Alpha 15 rapid conflict decisions and guarded undo remain unchanged.
- Alpha 14 canonical playback and Library Truth completion behaviour remain unchanged.
