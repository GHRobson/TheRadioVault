# Alpha10 Guarded Adoption — completion validation

Target version: `0.28.0-alpha10-guarded-adoption-buildfix1`

## Completed source checks

- Schema 45 creates the eight permanent structure/audit tables and upgrades existing rehearsal/adoption tables with five sealing columns.
- A fresh Alpha10 rehearsal atomically persists its item/conflict ledgers, computes SHA-256 seals for the exact truth run and both ledgers, and only then marks the rehearsal completed.
- Alpha9 rehearsals remain readable but cannot authorize live adoption because their seal fields are blank.
- Eligibility rechecks the source fingerprint, truth-plan seal, item seal and conflict seal. Interrupted `running`/`validating` adoption records fail closed.
- The live route repeats all seals under SQLite's write lock, reproduces the rehearsal operations, validates permanent/audit counts, foreign keys and integrity, then verifies everything again from an independent post-commit connection.
- Desktop quiescence saves progress, pauses playback without resetting it, stops timers and web ingress before inspecting background work, and restores captured state after a safe pre-commit exit.
- All 37 XML/XAML/project files parse. All 198 C# files parse with tree-sitter except the pre-existing embedded-JavaScript false positive in `TheRadioVault.Web/Services/LocalWebServer.cs`.
- ZIP/source packaging validation must confirm the final archive contains no build outputs or temporary databases.

## Runtime validation still required on Windows

This environment does not contain the .NET 8 SDK or WPF runtime, so the solution and smoke-test executable were not run here. Build the solution in Visual Studio, run `TheRadioVault.Tests`, then run one fresh Alpha10 rehearsal before enabling live adoption. The first real desktop run must retain the generated backup and confirm the accepted corpus totals after restart.
