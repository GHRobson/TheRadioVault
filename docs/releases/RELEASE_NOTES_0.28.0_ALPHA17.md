# Radio Vault v0.28.0-alpha17 — Research Performance and Settings Consolidation

Alpha 17 fixes the false decision flood and Research-tab slowdown found during Alpha 16 laptop testing, then simplifies the overlapping archive-related Settings pages.

## Fixed

- Repeated summaries no longer create one Error and one decision for every affected date.
- Needs your decision no longer shows quality warnings that have no supported resolution path.
- Opening Research no longer reads the entire research library or launches a full quality audit.
- Quality auditing no longer performs one full details load per research record.
- Hidden diagnostics no longer receive and render a large findings collection during normal navigation.

## Changed

- Repeated-summary reuse is one grouped diagnostic per distinct pattern.
- Quality findings must be editor-resolvable before appearing in Needs your decision.
- Broadcasts to find, import history, source diagnostics and quality diagnostics load on demand.
- Settings now has Archive, Playback, Appearance, Transcription, Web access and Advanced.
- Archive combines library folders, availability, preservation, cross-PC comparison, backup and metadata packages.
- Library Truth is retained under Advanced archive analysis.

## Preserved

- Rapid side-by-side research conflict decisions and keyboard controls.
- Guarded safe quality repairs and undo history.
- Every former Settings operation.
- Schema 45 and all export formats.
- Canonical multipart playback, transcripts, web transfer and held-group safeguards.
