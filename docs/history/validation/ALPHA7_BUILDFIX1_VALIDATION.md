# Alpha7 Buildfix 1 validation

## Scope

Settings and Preservation navigation performance only. Database schema remains 39.

## Structural validation

- Version metadata is consistent across `VERSION.txt` and the WPF project.
- All project and XAML files parse as XML.
- Every XAML event handler resolves to a C# method.
- Modified C# files pass delimiter and raw-string balance checks.
- The preservation summary SQL was exercised against a synthetic 100,000-row SQLite database.
- Existing alpha7 preservation, manifest and comparison source files remain present.

## User test

1. Open Settings on the large desktop library. The Library section should appear immediately.
2. Select Preservation. The page should appear immediately with loading placeholders, then fill its totals without freezing navigation.
3. Return to Library or another page while the summary loads; the app should remain interactive.
4. Select Storage. Its cached database summary should load without walking every physical file.
5. Press Refresh storage information. The check may take time, but playback and navigation should remain responsive.
6. Confirm deep preservation scan, manifest export and comparison still open normally.
