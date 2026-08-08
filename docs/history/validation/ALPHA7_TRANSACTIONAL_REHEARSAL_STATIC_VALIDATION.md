# Alpha7 transactional-rehearsal static validation

Target version: `0.28.0-alpha7-transactional-rehearsal-buildfix1`

The source package was checked structurally in the packaging environment:

- schema 43 creates the rehearsal run and item ledgers;
- export schema 5 includes rehearsal summary and per-broadcast evidence;
- the new service creates a true pre-rehearsal SQLite online backup before writing its live shadow ledger;
- the retained backup is reopened and must pass SQLite integrity checking;
- the disposable clone uses an explicit transaction, foreign-key checking, integrity checking, mandatory rollback and logical fingerprint comparison;
- eligible preview membership, survivor identity and expected operation counts are validated before and during rehearsal;
- a synthetic reconstruction of the accepted 7,169-file alpha6 export rehearsed all 4,305 eligible plans with exactly 4,305 canonical, 6,685 recording, 7,035 segment, 7,035 coverage, 2,728 file-reassignment and 2,728 alias-retirement operations;
- the synthetic full-corpus rehearsal produced zero operation mismatches, zero foreign-key violations, SQLite integrity `ok`, and returned all 7,091 episode rows to their pre-transaction state after rollback;
- the WPF Rehearsal Results tab and command handlers are present and XAML parses;
- 96 registered smoke-test names are unique and resolve to test methods;
- the alpha7 regression test asserts the working test database remains unchanged after the disposable rehearsal;
- no live adoption command exists.

The packaging environment does not contain the .NET 8 SDK or PowerShell. Compilation, `validate-source.ps1`, the smoke tests and the complete desktop rehearsal remain Windows validation requirements.
