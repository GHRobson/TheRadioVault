# v0.27.0-alpha6-buildfix2 validation

## Static validation completed

- Application version advanced to `0.27.0-alpha6-buildfix2-reconciliation-performance`.
- Database schema remains 38; no migration was added.
- All XML project and XAML files parse successfully.
- All event-handler names referenced by `ResearchReconciliationWindow.xaml` resolve in code-behind.
- C# delimiter and raw-string structure checks pass across the source tree.
- The parser version remains buildfix1 because this buildfix does not alter filename parsing and must not force another parser migration.

## Reconciliation performance checks

- Scan completion no longer calls `TriageResearchReconciliationCandidates`.
- Main Research page loading no longer calls triage.
- Research decisions launches triage through `Task.Run` and loads grouped decisions through a background task.
- A per-database triage gate prevents concurrent passes.
- Candidate detail lookup limits the SQL query to the requested candidate ID.
- The pending decision view does not load completed reconciliation history.
- Existing attached candidates are dismissed in set-based SQL.
- Identity/source-only groups are linked and resolved in one SQLite transaction.

## Windows build/test still required

- Build the complete solution in Visual Studio 2022 Release configuration.
- Rescan the 7,169-file desktop archive and confirm it completes rather than appearing to stop at the 7,150 progress update.
- Open the main Research page and confirm it paints and remains interactive.
- Open Research decisions and confirm the window remains responsive while background analysis runs.
- Confirm the former `Untitled saved research` bulk queue falls substantially.
- Recheck the laptop archive for regressions.
