# Alpha 15 static validation

## Completed in the packaging environment

- All XAML files parse as XML.
- Every event handler referenced by `MainWindow.xaml` resolves to a method in the MainWindow partial class.
- No duplicate `x:Name` values were introduced in `MainWindow.xaml`.
- The former `MetadataNavButton` and `MetadataStudioPanel` controls are absent.
- The unified `ResearchMetadataSection` and `ResearchDecisionsSection` controls are present.
- Rapid-decision keep/use/skip/undo handlers are present.
- Research conflict enumeration and guarded undo database methods are present.
- The source root contains exactly three Markdown documents: `README.md`, `BUILDING.md` and `CHANGELOG.md`.
- Database schema remains 45.

## Windows build and acceptance

1. Run `./release-gate.ps1` or build the full solution in Visual Studio 2022.
2. Open Research & Metadata and confirm there is no separate Metadata Studio navigation item.
3. Import and export a research pack from the unified navigation.
4. Open Metadata editor and save a harmless edit.
5. Open Rapid decisions and confirm both competing values fit without page scrolling.
6. Resolve one item with the mouse and one with keys `1` or `2`; neither should ask for confirmation.
7. Press `S` and confirm the item advances without changing data.
8. Resolve another item, press `Z`, and confirm the conflict reappears.
9. Open a full record from Rapid decisions and verify the same conflict and provenance are visible.
10. If a broadcast-match decision exists, apply or dismiss it and confirm the next item appears without a second confirmation prompt.
11. Restart and run a normal playback, transcript, web-transfer and Library Truth audit smoke pass.

The packaging environment does not provide the .NET SDK, so compilation remains a Windows validation step.
