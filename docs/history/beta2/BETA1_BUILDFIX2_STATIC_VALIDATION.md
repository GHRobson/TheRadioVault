# Beta 1 buildfix 2 — static validation and Windows acceptance

Version: `0.28.0-beta1-buildfix2`

## Static gates

- [x] Version markers agree.
- [x] Database schema remains 45.
- [x] Moment duplicate policy is platform-neutral and covered by a smoke test.
- [x] Shared Moments service repairs existing duplicate rows once per process.
- [x] Manual saves are canonical-broadcast scoped and idempotent.
- [x] Legacy metadata-package and research-reconciliation insertion routes use the same guard.
- [x] Canonical representative IDs are returned for playback.
- [x] Existing progress-persistence buildfix remains present.
- [x] Source root documentation layout remains tidy.
- [x] ZIP uses a short internal root path.

## Windows acceptance pass

1. Build the full solution in Visual Studio.
2. Launch the existing library and open **Moments**.
3. Confirm each formerly repeated card appears once.
4. Double-click the remaining **Ron grateful for Chris** and **The old slave hanging tree** cards and confirm they seek correctly.
5. Add a new Moment, close and reopen Radio Vault, and confirm it remains one card.
6. Attempt to save the same title and notes again within two seconds of the same position; confirm it remains one card.
7. Save the same title more than two seconds away; confirm both intentionally distinct Moments remain.
8. Export and re-import metadata once; confirm the Moment count does not increase.
9. Recheck the buildfix1 progress restart/upgrade test.

A fresh metadata export after step 8 is useful for confirming database-level idempotency.
