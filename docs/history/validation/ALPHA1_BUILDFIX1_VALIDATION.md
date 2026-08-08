# v0.28.0-alpha1-buildfix1 validation

- Confirmed `GlobalUsings.cs` no longer globally imports `TheRadioVault.Services.Services`.
- Confirmed `LibraryTruthEngine` is available through a precise global alias.
- Confirmed `MainWindow` therefore resolves `PlaybackCoordinator` to `TheRadioVault.Coordination.PlaybackCoordinator`.
- Confirmed version metadata is consistent.
- Confirmed all project and XAML files are well-formed XML.
- Confirmed XAML event handlers resolve to code-behind methods.
- Database schema remains 40.

A .NET SDK is unavailable in the packaging environment, so Visual Studio remains the compilation gate.
