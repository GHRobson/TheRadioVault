# Alpha 5 Buildfix 1 static validation

Date: 2026-07-23

- [x] Version metadata reports `0.30.0-alpha5-buildfix1-ui-parity`.
- [x] `App` always constructs `MainWindow`; remote-client mode is a backing-service selection.
- [x] Separate remote Library, Broadcast Details and Moment editor XAML/code files are absent.
- [x] Normal Dashboard, Library, Search, Favourites, queue, Moments, Transcripts and Broadcast Info controls remain in `MainWindow.xaml`.
- [x] Certificate-pinned multipart streaming remains behind `IPlaybackEngine`.
- [x] Archive, queue and Moments remote adapters implement the existing application-service contracts.
- [x] Server transcript listing and Broadcast Info metadata mutation routes are bounded and authenticated.
- [x] Shutdown progress flush avoids UI-context deadlock and does not complete a local-library session guard.
- [x] XAML/project XML parses and XAML event handlers resolve.
- [x] Schema 45, capability generation 11 and Anywhere cache identities remain unchanged.

The packaging environment does not include the .NET SDK or PowerShell, so Windows compilation and two-machine runtime acceptance remain required.
