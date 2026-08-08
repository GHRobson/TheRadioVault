# Alpha4 static validation record

- Baseline source SHA-256: `9229d563447eaac4874dd1c5284e13ed0596e2260b01548e31541e82361113e9`
- Target version: `0.28.0-alpha4-recording-integrity`
- Validation result: **PASS**

## Checks

- [x] **Version marker** — 0.28.0-alpha4-recording-integrity
- [x] **XML project/XAML parse** — 37/37 files
- [x] **Changed C# delimiter scan** — 7 files
- [x] **Test runner registration** — 91 tests (81 direct methods); 0 missing; 0 duplicates
- [x] **Alpha4 regression registrations** — 7 focused cases
- [x] **Internal record constructor arity** — 6 constructor sites
- [x] **Alpha4 implementation surface** — 7/7 capabilities
- [x] **Legacy per-file trailing-number promotion removed** — removed
- [x] **Desktop export regression corpus** — 7,169 files / 4,330 broadcasts / 6,736 recordings; 5/5 fixtures

## Environment limitation

The execution environment does not contain the .NET 8 SDK, MSBuild, Roslyn compiler, Mono, or PowerShell. The source could therefore not be compiled here. The full solution and regression runner must still be built in Visual Studio/.NET 8 on the Windows test machine before this alpha is treated as build-validated.

## Intended desktop validation

1. Build `TheRadioVault.sln` in Visual Studio.
2. Run `TheRadioVault.Tests` and confirm every registered test passes.
3. Run a fresh Library Truth analysis against the 7,169-file GRAHAM-PC archive.
4. Check every case in `LIBRARY_TRUTH_ALPHA4_VALIDATION.md` before considering adoption work.

