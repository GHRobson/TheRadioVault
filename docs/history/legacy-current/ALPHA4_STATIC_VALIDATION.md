# Alpha 4 static validation

- [x] Version metadata reports `0.30.0-alpha4-laptop-state-migration`.
- [x] Database schema remains 45 and LAN capability generation remains 10.
- [x] The `.trvstate` format contains a versioned manifest and SHA-256-sealed state payload.
- [x] Import bounds package size and record count.
- [x] Canonical, broadcast-UID and unambiguous fallback matching are present.
- [x] A complete SQLite backup is created before the import transaction.
- [x] Newer/completed server progress protection, conservative history merge, favourite-only addition, Moment deduplication and queue append rules are present.
- [x] Preview, cancellation, unmatched reporting and post-import provenance reporting are present.
- [x] XML/XAML parsing, C# lexical checks, source-manifest verification and ZIP integrity are included in packaging validation.
- [ ] Windows/Visual Studio compilation and real remote-client-to-server migration testing remain pending.
