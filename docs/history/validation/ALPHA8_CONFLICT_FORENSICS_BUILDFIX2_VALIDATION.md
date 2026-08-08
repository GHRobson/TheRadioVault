# Alpha8 Conflict Forensics buildfix2 validation

Target version: `0.28.0-alpha8-conflict-forensics-buildfix2`

## Reported compiler failure

Visual Studio reported CS8997 (`Unterminated raw string literal`) at `LibraryTruthWindow.xaml.cs` line 246, followed by 29 cascading parser errors through line 286.

## Correction

The conflict-forensics selection details now use conventional interpolated-string concatenation rather than a multi-line raw string. This is a display-only correction and retains all evidence fields.

## Static checks

- The malformed `$"""{item.Evidence}` form is absent.
- Candidate values, provenance and preserved-alternate output remain present.
- Version metadata is aligned at buildfix2.
- Database schema remains 44 and export schema remains 6.
- XML/XAML/project files parse successfully.
- The buildfix patch applies cleanly to buildfix1.
- ZIP integrity and extracted-tree reproduction are verified.

Compilation and runtime rehearsal validation require the Windows .NET 8 environment.
