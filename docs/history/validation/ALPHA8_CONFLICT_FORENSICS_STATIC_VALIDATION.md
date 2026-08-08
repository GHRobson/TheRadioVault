# Alpha8 Conflict Forensics — static validation

This package was checked without executing the Windows/.NET application. The original alpha8 package exposed two conflict-service type-inference errors, corrected by buildfix1. Visual Studio then exposed one malformed conflict-details raw string, corrected by buildfix2. Source guards now cover all three compiler failures.

Validated statically:

- version alignment across `VERSION.txt`, project metadata and Library Truth parser marker;
- database schema 44 table/column definitions and migration guards;
- export schema 6 and forensic model wiring;
- conflict service and Library Truth window C# syntax and partial-class integration;
- UI XAML parsing, event-handler presence and explicit `Task.Run` lambdas for optional-parameter methods;
- 97 registered smoke tests with no duplicate names or missing method references;
- conflict policy regression registration and schema-43-to-44 migration coverage;
- no new live-adoption command or direct working-library adoption path;
- patch dry-run, ZIP integrity and extracted-tree reproduction.

Compilation, PowerShell release validation, smoke-test execution and the full 7,169-file rehearsal still require the user's Windows .NET 8 environment.
