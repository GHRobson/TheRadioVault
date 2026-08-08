# v0.28.0 Beta 1 static validation

The package was validated without a local .NET SDK, so Windows compilation remains required.

## Static gates

- All XAML files parse as XML.
- Every XAML event handler resolves to a C# method.
- Duplicate `x:Name` declarations are rejected.
- C# delimiter and source-structure checks pass.
- Direct-decision engine smoke coverage includes conflicting person roles and weak topics.
- The advanced-diagnostics click path contains no automatic full-audit call.
- Exact Metadata editor navigation preserves the target episode outside the first 1,000 visible rows.
- Undo restores research and episode `user_modified` flags as well as metadata values.
- Source root documentation remains limited to `README.md`, `BUILDING.md` and `CHANGELOG.md`.
- Database schema remains 45.

## Windows acceptance

1. Build the complete solution in Visual Studio 2022 with .NET 8.
2. Open Research and confirm the normal page appears without a long UI stall.
3. Run a recheck, then resolve a multiple-role issue from its cards using mouse and number keys.
4. Undo the last choice, resolve it again, restart, recheck and confirm it does not return incorrectly.
5. Use `Open affected metadata` on an item well outside the first visible results and confirm the exact broadcast and field are selected.
6. Open Advanced diagnostics and confirm the panel appears immediately; run the full diagnostics explicitly and verify progress remains responsive.
7. Smoke-test Library, playback, multipart seeking, transcripts, Moments, web handoff and offline playback.
