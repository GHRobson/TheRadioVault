# Radio Vault v0.28.0-alpha8 buildfix2 — Conflict Forensics

Version: `0.28.0-alpha8-conflict-forensics-buildfix2`

## Compile correction

Visual Studio exposed a malformed multi-line interpolated raw string in `LibraryTruthWindow.xaml.cs`. The closing delimiter was placed inline with interpolated content, which caused CS8997 and the cascade of syntax errors beginning at line 246.

Buildfix2 replaces that display-only expression with ordinary interpolated-string concatenation. The same conflict evidence remains visible in the UI.

## Unchanged behaviour

- Database schema remains 44.
- `.trvtruth` export schema remains 6.
- Conflict classifications and deterministic policies are unchanged.
- Alpha7 backup, disposable-clone transaction, exact operation checks and mandatory rollback are unchanged.
- Live adoption remains disabled.
