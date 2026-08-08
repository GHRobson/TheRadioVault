# Radio Vault v0.30.0 Alpha 5 Buildfix 4 — canonical media-manifest size repair

Buildfix 3 launched successfully and the remote client could browse the server library, but attempting playback returned HTTP 500 while loading the canonical media manifest.

The manifest builder queried `media_files.size_bytes`. Radio Vault schema 45 has always stored the physical byte count in `media_files.file_size`, so SQLite raised `no such column: size_bytes` on the server before the first media part could be streamed.

Buildfix 4 corrects that query to `file_size` and extends the canonical cutover integration scenario to verify multipart part sizes and the aggregate manifest size. The repair is server-side; installing the same build on the server and remote client remains recommended, but the server is the machine that must contain this fix for playback to start.

Preserved invariants:

- the ordinary `MainWindow` remains the only remote-client shell in both local and remote-client modes;
- canonical multipart streaming and certificate-pinned authentication are unchanged;
- Buildfix 3's launch-time speed-selector guard remains present;
- Buildfix 2's transcript contract remains present;
- database schema remains 45;
- LAN capability generation remains 11;
- web-shell generation remains 10;
- Anywhere shell/audio/artwork cache identities remain unchanged;
- Alpha 4 `.trvstate` migration remains unchanged.
