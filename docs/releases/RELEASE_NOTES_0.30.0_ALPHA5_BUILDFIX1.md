# Radio Vault v0.30.0 Alpha 5 Buildfix 1 — UI parity correction

Alpha 5 compiled, but its separate remote-client shell violated the intended remote-client design. Buildfix 1 removes that shell and makes a remote client run the normal Radio Vault interface against the server.

## Fixed

- Removed `RemoteLibraryWindow`, `RemoteBroadcastDetailsWindow` and `RemoteMomentEditorWindow`.
- Remote-client startup now opens the existing `MainWindow`.
- Local and remote-client modes use the same XAML, navigation, rows, cards, detail page and player.
- Local fallback also reopens the same `MainWindow` with a different backing-service selection.
- Prevented remote-client shutdown from touching local session-guard state.

## Added to the shared shell

- server Dashboard and canonical Library data;
- normal global search, favourites and queue workflows;
- normal Moments page;
- server transcript list and normal transcript viewer;
- normal Broadcast Info page and metadata editor write-through;
- certificate-pinned canonical multipart playback through `IPlaybackEngine`.

## Preserved

- Alpha 4 guarded `.trvstate` migration;
- database schema 45;
- LAN capability generation 11 and `lan.full-shell`;
- Anywhere API/cache generations and media caches;
- the remote client's untouched local archive as an explicit fallback.
