# Beta 1 buildfix 3 — compile correction

Version: `0.28.0-beta1-buildfix3`  
Database schema: `45`

## Visual Studio failure corrected

Buildfixes 1 and 2 introduced two local variables in `DatabaseService.GetPlaybackState` using conditional expressions whose branches were `null` and a non-nullable `DateTime`. With `var`, C# could not infer a common type and reported CS0173 on both lines.

Buildfix 3 declares both values explicitly as `DateTime?`:

```csharp
DateTime? firstPlayed = reader.IsDBNull(4)
    ? null
    : DateTime.Parse(reader.GetString(4));
DateTime? lastPlayed = reader.IsDBNull(5)
    ? null
    : DateTime.Parse(reader.GetString(5));
```

No database, playback-policy or Moment-deduplication behaviour changed.

## Validation target

1. Build the complete solution in Visual Studio 2022.
2. Confirm the two CS0173 errors are gone.
3. Launch against the existing schema-45 database.
4. Check whether Bennington 2015-07-16 recovers progress.
5. Open Moments and confirm duplicated cards collapse safely.
6. Set recognisable progress, restart, and confirm it persists.
