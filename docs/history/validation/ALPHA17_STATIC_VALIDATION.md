# Alpha 17 static validation

## Completed in the packaging environment

- All XAML files parse as XML.
- Every event handler referenced by `MainWindow.xaml` resolves to a MainWindow partial method.
- No duplicate `x:Name` values exist in `MainWindow.xaml`.
- Repeated-summary patterns produce one grouped diagnostic per distinct summary rather than one error per record.
- The smoke-test suite contains a 25-record repeated-summary aggregation test.
- `ResearchQualityAuditService` uses `GetResearchAuditRecords` and no longer calls `GetResearchLibraryRecordDetails` in a loop.
- The audit snapshot is built from four bulk queries.
- Normal Research navigation does not start a full quality audit.
- Needs your decision filters quality output through `IsDecisionActionable`.
- Broadcasts to find and advanced research utilities are lazy-loaded.
- Settings exposes six destinations and no longer exposes standalone Library Truth, Storage, Preservation or Backup tabs.
- Archive displays folders, availability, preservation and backup tools together.
- Advanced displays Library Truth analysis and troubleshooting together.
- Source root documentation remains limited to README.md, BUILDING.md and CHANGELOG.md.
- Database schema remains 45.

## Windows build and acceptance

1. Run `./release-gate.ps1` or build the full solution in Visual Studio 2022.
2. On the laptop, open Research & Metadata and verify that it appears immediately on Needs your decision without a multi-second freeze.
3. Confirm the previous 2,408 repeated-summary count is gone from the decision badge.
4. Open Advanced diagnostics and run the audit. Repeated summary wording should appear as grouped patterns, not thousands of Errors or decisions.
5. Confirm each quality item shown under Needs your decision opens the exact affected Metadata editor broadcast.
6. Correct one generic summary, save and press Recheck; confirm the item disappears.
7. Open Broadcasts to find and confirm it loads only when selected.
8. Open Settings and confirm the visible destinations are Archive, Playback, Appearance, Transcription, Web access and Advanced.
9. Under Archive, test folder management, storage refresh, preservation summary, manifest export/compare, backup/restore entry points and metadata package entry points.
10. Under Advanced, open the Library Truth shadow analysis and the normal troubleshooting actions.
11. Repeat the Research and Settings responsiveness pass on the larger desktop library.
12. Run playback, transcripts, web handoff, offline download and canonical Library Truth smoke tests.

The packaging environment does not provide the .NET SDK, so compilation remains a Windows validation step.
