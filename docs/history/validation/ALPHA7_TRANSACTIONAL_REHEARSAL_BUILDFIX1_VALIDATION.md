# v0.28.0-alpha7 transactional rehearsal buildfix1 validation

- Corrected CS1503 at `LibraryTruthWindow.xaml.cs` line 70.
- Replaced the optional-parameter method group with an explicit zero-argument lambda.
- Added a source-validation guard against reintroducing the compile failure.
- Database schema remains 43.
- Library Truth export schema remains 5.
- No live adoption command or live-library mutation route was added.
- Full compilation and smoke tests require Windows with the .NET 8 SDK.
