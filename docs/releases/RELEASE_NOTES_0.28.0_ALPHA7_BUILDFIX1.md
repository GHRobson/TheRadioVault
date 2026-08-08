# Radio Vault v0.28.0-alpha7 buildfix1

Version: `0.28.0-alpha7-transactional-rehearsal-buildfix1`

This buildfix corrects the Visual Studio compiler error CS1503 in `LibraryTruthWindow.xaml.cs`. `LibraryTruthAdoptionRehearsalService.GetLatestItems` accepts an optional `limit` parameter, so C# cannot infer it as a zero-argument `Task.Run` delegate when supplied as a bare method group. The UI loader now invokes it through `Task.Run(() => _rehearsal.GetLatestItems())`.

No Library Truth, adoption-preview, backup, transaction, rollback, state-preservation, schema or export behaviour changed.
