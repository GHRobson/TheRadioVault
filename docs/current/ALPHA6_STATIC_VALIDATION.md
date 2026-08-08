# Alpha 6 static validation

- [x] Version metadata reports `0.30.0-alpha6-remote-research-settings-parity`.
- [x] Database schema remains 45.
- [x] LAN capability generation is 12.
- [x] `lan.research-packs` and `lan.settings-parity` are advertised and required by the client.
- [x] Research preview/apply/cancel/export routes are versioned under `/api/v1/federation`.
- [x] Remote import uses bounded upload, bounded/expiring preview sessions, preserved SHA-256 provenance, the normal pre-import snapshot and the server database transaction.
- [x] Remote export writes through an atomic temporary client file.
- [x] The existing Research picker, preview and operation windows are retained.
- [x] Remote Settings hides Transcription, Advanced, local server hosting and local archive-maintenance actions.
- [x] Server archive state and playback preferences flow through certificate-pinned APIs.
- [x] Main XAML parses and all named Alpha 6 event handlers are declared.
- [x] Source manifest and ZIP integrity are verified during packaging.
- [ ] Windows/.NET compilation and live two-machine acceptance remain to be performed in Visual Studio.
