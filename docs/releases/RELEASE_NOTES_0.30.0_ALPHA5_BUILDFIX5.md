# Radio Vault v0.30.0 Alpha 5 Buildfix 5 — post-cutover scan promotion

## Fixed

After the guarded Library Truth adoption, the ordinary scanner still wrote newly discovered files only to the legacy `episodes` and `media_files` tables. The canonical Library deliberately reads Broadcast/Recording/File identities instead of those physical rows, so a scan could report files as added while neither the server nor its clients displayed them.

Buildfix 5 adds a bounded incremental promotion step at the end of every successful scan:

- confidently dated new show/date/slot identities become appended canonical broadcasts;
- multipart files scanned together become one ordered canonical recording;
- files matching an existing canonical identity are attached as a preserved additional recording;
- the sealed Library Truth adoption run is not edited or re-run;
- undated, low-confidence, Unsorted and held/review identities remain unadopted for safety;
- previously scanned but unmapped files are repaired by running **Scan library** again.

Canonical Library counts, collection summaries, normal Library results, recording options, playback plans, media manifests and transcript timeline resolution now understand incrementally appended canonical rows.

## Compatibility

- Database schema: **45** (unchanged)
- LAN capability generation: **11** (unchanged)
- Existing local/server-backed `MainWindow`: unchanged
- Buildfix 4 canonical media-manifest size repair: preserved
- Alpha 4 `.trvstate` migration: preserved

## Validation focus

On the server, run **Scan library** after installing Buildfix 5. Confirm that files previously reported as added now appear as broadcasts, including a multipart broadcast as one Library row. Then refresh/reopen the remote client and confirm the same rows and playback are available there.
