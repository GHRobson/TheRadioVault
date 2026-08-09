# Alpha 13 Pass 3 Buildfix 2 Buildfix 3 acceptance

This package repairs the remaining date-review compile failure caused by reading catalogue metadata from the wrong level of the research-pack model.

## Build

1. Extract into a fresh folder.
2. Run `BUILD-AND-RUN.cmd`.
3. Confirm all projects compile and Radio Vault launches.
4. Confirm there is no CS1061 error for `TrvPackBroadcast.Catalogue` in `DatabaseService.cs`.

## Date review

1. Open Research and choose **Date review**.
2. Filter to Ron Bennington Interviews, Unmasked, or The Ron & Ron Show.
3. Confirm date candidates appear and one exact date can be approved as the Library date.
4. Reopen the decision and confirm the previous Library state is restored.
