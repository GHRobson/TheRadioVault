# Alpha 14 static validation

- Source version advanced to `0.28.0-alpha14-library-truth-completion`.
- Canonical playback plans now pass a shared completeness guard before desktop, web or offline consumers receive them.
- Adopted and held selection paths are distinguishable through `ExplainRecordingSelection`.
- `GetAuditSnapshot` provides one migration-completion report without writing to the database.
- Existing canonical cutover smoke coverage now verifies adopted and held explanations plus audit counts.
- Schema remains 45; no migration is required.

The build must still be compiled and exercised on Windows against the desktop and laptop library states.
