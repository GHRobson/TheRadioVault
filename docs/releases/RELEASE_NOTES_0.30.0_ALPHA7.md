# Radio Vault v0.30.0 Alpha 7

## Structured remote-client startup

The remote client now presents the ordinary Radio Vault Dashboard immediately while remote data is being prepared:

- Animated skeleton headings, cards and broadcast rows occupy the real content area during the initial connection and catalogue load.
- The loading caption updates as bounded Library pages arrive from the server.
- The skeleton is part of `MainWindow`; it is not a separate remote shell and does not change local-mode navigation or layout.
- Connection failures leave the normal recovery and Settings paths available after the loading surface is dismissed.

## Network-bound busy feedback

Remote work that cannot complete immediately now has an explicit visual state:

- Library refresh and post-import refresh
- Global archive search
- Broadcast information and transcript loading
- The Transcripts workspace
- Server Settings refresh
- Initial media-manifest, HTTP range-stream and decoder opening
- Mid-stream buffering and canonical multipart transitions

The affected area blocks duplicate interaction while the operation is pending, then restores the established Radio Vault controls when it completes.

## Playback-state reconciliation

Alpha 7 replaces optimistic play/pause UI updates with observed player state:

- The LAN playback engine waits for WPF's real `MediaOpened` event before reporting media ready.
- Requested playback is tracked separately from observed stream progress.
- A 500 ms monitor detects forward progress, temporary stalls and recovery.
- A stall becomes a buffering state with a spinner rather than incorrectly displaying Play.
- The mini-player, large player, Dashboard action label and Library playback indicators are updated from the playback-engine snapshot.
- Duplicate play/pause clicks are ignored while opening or buffering.
- Multipart transitions preserve playback intent while each next part opens.

This fixes the condition where audio continued streaming while the button changed to Play and required two clicks to recover.

## Compatibility

- Database schema: 45 (unchanged)
- LAN capability generation: 12 (unchanged)
- Existing local/remote `MainWindow`: preserved
- Alpha 6 remote Research and Settings parity: preserved
- Alpha 5 Buildfix 5 post-scan canonical promotion: preserved
