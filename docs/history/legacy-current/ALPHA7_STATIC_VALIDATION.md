# Alpha 7 static validation

- [x] Version metadata reports `0.30.0-alpha7-buildfix1-dispatcher-server-terminology`.
- [x] Database schema remains 45.
- [x] LAN capability generation remains 12.
- [x] Initial server loading uses `RemoteInitialSkeleton` inside the existing `MainWindow`.
- [x] Remote Library refresh, search, broadcast details, transcripts and Settings use a ref-counted busy overlay.
- [x] The play/pause button contains a dedicated loading spinner and blocks duplicate commands while busy.
- [x] Playback UI subscribes to `IPlaybackEngine.StateChanged` and no longer relies only on optimistic button state.
- [x] The LAN playback engine raises `MediaOpened` from the controller's real media-ready event, not from manifest completion.
- [x] The controller distinguishes requested playback, observed playing and buffering state.
- [x] A 500 ms progress monitor reconciles stalled and resumed streams.
- [x] Player-state property changes cannot overwrite the spinner with a stale Play icon.
- [x] Multipart transitions preserve requested playback state.
- [x] Alpha 6 Research and Settings parity markers remain present.
- [x] Main XAML and all other XAML files parse successfully.
- [x] Named event handlers, C# delimiter structure, source manifest and ZIP integrity are verified during packaging.
- [ ] Windows/.NET compilation and live two-machine acceptance remain to be performed in Visual Studio.
