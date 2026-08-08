# Alpha 16 static validation

## Completed in the packaging environment

- All XAML files parse as XML.
- Every event handler referenced by `MainWindow.xaml` resolves to a method in the MainWindow partial class.
- No duplicate `x:Name` values exist in `MainWindow.xaml`.
- The Research sidebar exposes only Needs your decision, Broadcasts to find and Metadata editor as primary destinations.
- No visible Research library, Missing broadcasts or Quality checks navigation remains.
- Broadcasts to find is hard-filtered to unattached discovery leads.
- Automatic research maintenance uses the existing guarded quality-repair service and records changes through its normal action ledger.
- Non-automatic warning/error findings have a fixed actionable surface under Needs your decision.
- Research-pack import/export, import history and advanced diagnostics remain accessible.
- Source root documentation is limited to README.md, BUILDING.md and CHANGELOG.md.
- Database schema remains 45.

## Windows build and acceptance

1. Run `./release-gate.ps1` or build the full solution in Visual Studio 2022.
2. Open Research & Metadata and confirm it starts on Needs your decision.
3. Confirm the only primary section buttons are Needs your decision, Broadcasts to find and Metadata editor.
4. Confirm no all-research-record browser or Quality checks tab is visible.
5. Leave the workspace open while the automatic check completes; the interface must remain responsive.
6. If safe issues are found, confirm the status reports how many were fixed and Advanced diagnostics records them in repair history.
7. Resolve a normal library-versus-research conflict with keys `1` and `2`, then test `S` and `Z`.
8. If a manual quality issue appears, confirm it uses plain language and opens the relevant Metadata editor record or Broadcasts to find lead.
9. Open Broadcasts to find and confirm every item lacks attached audio, the explanatory copy says nothing was lost, and the status pills use discovery language.
10. Attach one known lead to an archive broadcast and confirm it disappears from Broadcasts to find without losing its research.
11. Import and export a research pack; inspect Import history from the advanced utilities.
12. Run a normal playback, transcript, web-transfer and Library Truth audit smoke pass on desktop and laptop.

The packaging environment does not provide the .NET SDK, so compilation remains a Windows validation step.
