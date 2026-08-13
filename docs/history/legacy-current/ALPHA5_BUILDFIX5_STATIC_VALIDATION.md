# Alpha 5 Buildfix 5 static validation

- [x] Version metadata reports `0.30.0-alpha5-buildfix5-post-cutover-scan-promotion`.
- [x] Physical scanning invokes incremental canonical promotion before Library reload.
- [x] Promotion runs only after a verified guarded adoption and never edits the sealed truth run.
- [x] Trustworthy new show/date/slot identities append canonical Broadcast/Recording/Segment/Coverage/Map rows transactionally.
- [x] Multipart candidates are grouped by canonical identity and ordered part number.
- [x] Existing canonical identities receive an additional recording rather than a duplicate Library broadcast.
- [x] Undated, non-high-confidence, Unsorted and held/review identities are not silently adopted.
- [x] Canonical summary and Library queries include appended broadcasts.
- [x] Adopted recording choices and explicit playback plans include scan-appended recordings.
- [x] A regression scenario verifies one newly scanned multipart broadcast appears, plays as two segments and is idempotent on a second promotion pass.
- [x] Buildfix 4 continues to read media size from `media_files.file_size`.
- [x] Database schema remains 45 and LAN capability generation remains 11.
- [ ] Windows `Release | x64` compile and server/remote-client acceptance pass (user environment).
