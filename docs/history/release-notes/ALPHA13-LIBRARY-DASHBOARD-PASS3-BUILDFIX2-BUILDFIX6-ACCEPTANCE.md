# Alpha 13 Pass 3 Buildfix 2 Buildfix 6 — Quick Date Approval

## Build

1. Extract the package into a short fresh folder such as `C:\RV`.
2. Run `BUILD-AND-RUN.cmd`.
3. Confirm the console reports `Buildfix 6 Quick Date Approval` and Radio Vault opens.

## Quick workflow

1. Open **Research → Date review** and leave **Active** selected.
2. Choose a proposed date and press **A**. Confirm it moves to **Completed**, the next Active item is selected, and Library immediately shows the approved date.
3. On another item press **K**. Confirm the current Library date is unchanged and the item moves to Completed.
4. On another item press **I**. Confirm it moves to **Ignored** and the next Active item is selected.
5. Press **Ctrl+Z** or the visible Undo button. Confirm the last item returns to Active and is selected.

## Queue and recovery checks

1. Switch between **Active**, **Ignored**, and **Completed** and confirm the counts and contents persist after restarting Radio Vault.
2. Open an Ignored item and choose **Return to Active**.
3. Open a Completed item and choose **Return to Active**. If it had changed the Library date, confirm the previous date or Undated state is restored.
4. Expand **More date choices** and confirm recording-only, release/archive-only and leave-undated remain available.

## Research Pack round trip

1. Export a pack containing kept-existing and ignored decisions.
2. Import it into a test copy of the same library.
3. Confirm those decisions stay settled and do not clear or replace the current trusted Library date.

## Regression

- Approvals still update the visible active Library projection without a rescan.
- Local playback and progress still work.
- Radio Vault Anywhere starts and streams.
- All six first-class shows remain available in Date review.
