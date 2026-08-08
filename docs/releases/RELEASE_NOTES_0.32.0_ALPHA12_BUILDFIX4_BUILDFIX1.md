# Radio Vault v0.32.0 Alpha 12 Buildfix 4 Buildfix 1

## Avalonia provider linkage correction

The original Buildfix 4 source added the transactional handoff methods in the partial class file `TheRadioVault/Services/WebArchiveProvider.PlaybackTransfers.cs`. The WPF reference project included the file through normal SDK source discovery, but the Avalonia default shell links selected WPF-era service sources explicitly and did not link that new partial file.

The Avalonia build therefore saw `WebArchiveProvider` implementing `IWebArchiveProvider` without the five transactional method bodies and failed with CS0535.

This buildfix explicitly links the partial provider file into `TheRadioVault.Desktop.Avalonia.csproj` and adds a validation guard for both required provider source files. Runtime handoff behaviour is unchanged from Buildfix 4.
