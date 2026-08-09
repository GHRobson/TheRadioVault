# Alpha 13 Pass 3 Buildfix 2 Buildfix 5 — Live Date Projection

## Build

1. Extract the package into a fresh folder.
2. Run `BUILD-AND-RUN.cmd`.
3. Confirm the console title reports `Buildfix 5 Live Date Projection`.
4. Confirm the Avalonia project builds and Radio Vault opens.

## Primary acceptance

1. Open **Research → Date review**.
2. Choose an affected broadcast with a missing or incorrect Library date.
3. Select an exact date and choose **Approve as Library date**.
4. Return to Library and confirm the broadcast appears immediately in the correct year, month and day.
5. Confirm the same date appears in Search, Dashboard and Full Broadcast Information without restarting or rescanning.

Repeat the check with one normal daily show and one catalogue-style show such as Ron Bennington Interviews or Unmasked.

## Reversal acceptance

1. Enable **Show resolved decisions** and reopen the approved decision.
2. Confirm the prior Library date, or Undated state, is restored in Library.
3. Resolve another proposed date as recording-only or release/archive-only.
4. Confirm the evidence remains in Research without replacing a trusted Library date.

## Regression

- Local playback and progress still work.
- Radio Vault Anywhere starts and streams.
- All six first-class shows remain available in Date review.
- Ignored historical Library Truth projections are not rewritten.
