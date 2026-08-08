# Beta 1 buildfix 1 — static validation

## Completed in the packaging environment

- All XAML and project XML files parse successfully.
- Every XAML event-handler reference resolves to a C# method.
- All C# files pass delimiter/structure checks after string and comment removal.
- Canonical state expansion covers both adopted survivor/alias rows and held-group members.
- Explicit played/unplayed actions expand to every retained canonical member, preventing stale member progress from reappearing.
- The playback persistence policy has regression assertions for transient zero protection, explicit reset and legitimate non-zero backward movement.
- Archive Health diagnostic format 6 includes recent playback records only when playback inclusion is selected.
- Source-root documentation policy remains satisfied.
- ZIP integrity and source-manifest verification are required before handoff.

## Windows acceptance test

1. Build the complete solution in Visual Studio.
2. Open the affected Bennington broadcast or another in-progress broadcast and manually set a known position.
3. Let it play for at least ten seconds, close Radio Vault normally and reopen it.
4. Repeat while switching from the previous Beta 1 executable to buildfix 1.
5. Repeat with a multipart broadcast and after desktop/web ownership transfer.
6. Start a full diagnostics or other long operation while listening, then close and reopen after the operation completes.
7. Confirm **Mark unplayed** still resets only after its explicit confirmation.
8. Export a playback-inclusive diagnostic and confirm the test broadcast appears in `recentPlayback`.

The supplied format-5 diagnostic does not contain the 16 July 2015 playback-state row, so it cannot reveal the lost timestamp. Buildfix 1 may make the position reappear if it still exists on another retained canonical member; if every stored member was overwritten, the exact position cannot be reconstructed from this diagnostic.
