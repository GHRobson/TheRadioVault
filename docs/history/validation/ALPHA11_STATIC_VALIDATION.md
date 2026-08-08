# Alpha 11 static validation

Target: `0.28.0-alpha11-canonical-library-cutover`

Validation performed in the generation workspace:

- exact Alpha 10 buildfix1 source baseline SHA-256 matched;
- canonical projection SQL executed against a synthetic schema-45 database
  containing both adopted aliases and a held multi-row group;
- the SQL returned one unique row per truth broadcast, deterministic
  representatives, aggregated state and correct structure counts;
- legacy adopted and held identity resolution paths are covered by a new smoke
  test in `TheRadioVault.Tests`;
- adopted multipart and held direct-coverage playback plans were validated for
  deterministic ordering, logical offsets and physical source identity;
- the GRAHAM-PC evidence corpus matched 4,330 broadcasts, 4,305 adopted, 25 held,
  6,736 recordings, 7,106 coverage rows and 7,169 files;
- all C# files passed a delimiter, comment and string-literal structural scan;
- `MainWindow.xaml` and every project/XAML XML file parsed successfully;
- referenced `MainWindow.xaml` event handlers were checked against the partial
  `MainWindow` source;
- source-package manifest and SHA-256 are generated at packaging time.

The Linux generation environment does not contain the .NET SDK, MSBuild or a C#
compiler, so the complete WPF solution could not be compiled here. A Windows
Visual Studio build and desktop smoke test remain required before Alpha 11 is
accepted as a runnable baseline.
