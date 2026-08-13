using System.Globalization;
using Microsoft.Data.Sqlite;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Models;
using TheRadioVault.Services.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Manual saved collections preserve queue order", ManualCollectionsPreserveOrder),
    ("Saved collection revisions reject stale device edits", RevisionsRejectStaleEdits),
    ("Smart collections materialize current Library state", SmartCollectionsRemainLive),
    ("Saved collection creation is atomic and names are unique", CreationIsAtomicAndNamesAreUnique),
    ("Live Radio schedule is stable and never mutates listening state", LiveRadioScheduleIsStableAndReadOnly)
};

var selected = args.Length == 0
    ? tests
    : tests.Where(test => args.Any(filter => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
if (selected.Length == 0)
{
    Console.Error.WriteLine("No service tests matched the supplied filters.");
    return 2;
}

var failures = new List<string>();
foreach (var test in selected)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.Error.WriteLine($"FAIL  {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{selected.Length - failures.Count}/{selected.Length} service tests passed.");
return failures.Count == 0 ? 0 : 1;

static async Task ManualCollectionsPreserveOrder()
{
    await WithDatabaseAsync("manual-order", async database =>
    {
        SeedLibrary(database, (7101, "First", false), (7102, "Second", false));
        var service = new SavedCollectionService(database);
        var created = await service.CreateAsync(
            "  Road   Trip  ",
            SavedCollectionKind.Manual,
            episodeIds: [7102, 7101, 7102]);

        Equal("Road Trip", created.Summary.Name, "normalized name");
        Equal(1L, created.Summary.Revision, "initial revision");
        Equal(2, created.Summary.ItemCount, "deduplicated item count");
        Equal("Second,First", string.Join(',', created.Broadcasts.Select(value => value.Title)), "saved order");

        var summaries = await service.GetAllAsync();
        Equal(1, summaries.Count, "summary count");
        Equal(2, summaries[0].ItemCount, "manual summary count");
    });
}

static async Task RevisionsRejectStaleEdits()
{
    await WithDatabaseAsync("revision", async database =>
    {
        SeedLibrary(database, (7201, "One", false), (7202, "Two", false), (7203, "Three", false));
        var service = new SavedCollectionService(database);
        var collection = await service.CreateAsync("Revision Test", SavedCollectionKind.Manual, episodeIds: [7201, 7202]);

        collection = await service.AddAsync(collection.Summary.Id, 7203, collection.Summary.Revision);
        Equal(2L, collection.Summary.Revision, "revision after add");
        var duplicate = await service.AddAsync(collection.Summary.Id, 7203, collection.Summary.Revision);
        Equal(2L, duplicate.Summary.Revision, "duplicate add revision");

        await ThrowsAsync<SavedCollectionConflictException>(() =>
            service.RemoveAsync(collection.Summary.Id, 7201, expectedRevision: 1));

        collection = await service.MoveAsync(collection.Summary.Id, 7203, 0, collection.Summary.Revision);
        Equal("Three,One,Two", string.Join(',', collection.Broadcasts.Select(value => value.Title)), "moved order");
        Equal(3L, collection.Summary.Revision, "revision after move");

        collection = await service.RemoveAsync(collection.Summary.Id, 7201, collection.Summary.Revision);
        Equal("Three,Two", string.Join(',', collection.Broadcasts.Select(value => value.Title)), "order after removal");
        await ThrowsAsync<SavedCollectionConflictException>(() =>
            service.DeleteAsync(collection.Summary.Id, expectedRevision: 3));
        await service.DeleteAsync(collection.Summary.Id, collection.Summary.Revision);
        Equal(0, (await service.GetAllAsync()).Count, "deleted collection count");
    });
}

static async Task SmartCollectionsRemainLive()
{
    await WithDatabaseAsync("smart", async database =>
    {
        SeedLibrary(database, (7301, "Finished", true), (7302, "Waiting", false));
        var service = new SavedCollectionService(database);
        var created = await service.CreateAsync(
            "Finished shows",
            SavedCollectionKind.Smart,
            new SavedCollectionRule(Filter: LibraryListeningFilter.Completed));

        Equal(1, created.Broadcasts.Count, "initial smart result count");
        Equal("Finished", created.Broadcasts[0].Title, "initial smart result");
        var summaries = await service.GetAllAsync();
        Equal<int?>(null, summaries[0].ItemCount, "unmaterialized smart summary count");

        SetCompleted(database, 7302, true);
        var refreshed = await service.GetAsync(created.Summary.Id) ?? throw new InvalidOperationException("Smart collection disappeared.");
        Equal(2, refreshed.Broadcasts.Count, "live smart result count");
        Equal(2, refreshed.Summary.ItemCount, "materialized smart count");

        await ThrowsAsync<InvalidOperationException>(() =>
            service.AddAsync(refreshed.Summary.Id, 7301, refreshed.Summary.Revision));
    });
}

static async Task CreationIsAtomicAndNamesAreUnique()
{
    await WithDatabaseAsync("atomic", async database =>
    {
        SeedLibrary(database, (7401, "Valid", false));
        var service = new SavedCollectionService(database);
        await ThrowsAsync<SqliteException>(() => service.CreateAsync(
            "Broken",
            SavedCollectionKind.Manual,
            episodeIds: [999999]));
        Equal(0, (await service.GetAllAsync()).Count, "rolled-back collection count");

        _ = await service.CreateAsync("Unique", SavedCollectionKind.Manual, episodeIds: [7401]);
        await ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(" unique ", SavedCollectionKind.Manual));
        Equal(1, (await service.GetAllAsync()).Count, "unique collection count");
    });
}

static async Task LiveRadioScheduleIsStableAndReadOnly()
{
    await WithDatabaseAsync("live-radio", async database =>
    {
        SeedLibrary(database,
            (7501, "Morning archive", false),
            (7502, "Midday archive", false),
            (7503, "Evening archive", true));
        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,storage_state,is_preferred)
                SELECT id,'/archive/' || id || '.mp3',id || '.mp3',1000,$now,0,$now,3600000,'AvailableOffline',1
                  FROM episodes WHERE id IN (7501,7502,7503);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        var before = ReadListeningState(database);
        var at = DateTimeOffset.UtcNow;
        LiveRadioSnapshot first;
        using (var service = new LiveRadioScheduleService(database))
        {
            first = await service.GetSnapshotAsync(at);
            var repeated = await service.GetSnapshotAsync(at);
            Equal(first.Current?.ScheduleEntryId, repeated.Current?.ScheduleEntryId, "same programme within a persisted schedule");
            Equal(first.ScheduleRevision, repeated.ScheduleRevision, "same schedule revision");
        }
        using (var restarted = new LiveRadioScheduleService(database))
        {
            var afterRestart = await restarted.GetSnapshotAsync(at);
            Equal(first.Current?.ScheduleEntryId, afterRestart.Current?.ScheduleEntryId, "programme after service restart");
            Equal(first.ScheduleRevision, afterRestart.ScheduleRevision, "schedule revision after service restart");
        }
        if (first.Current is null) throw new InvalidOperationException("Expected a live programme.");
        Equal(before, ReadListeningState(database), "unchanged listening state");
    });
}

static string ReadListeningState(SqliteDatabase database)
{
    using var connection = database.OpenConnection();
    using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT GROUP_CONCAT(value,'|') FROM (
            SELECT e.id || ':' || e.status || ':' || p.position_ms || ':' || p.completed || ':' || p.play_count AS value
              FROM episodes e JOIN playback_state p ON p.episode_id=e.id
             WHERE e.id IN (7501,7502,7503)
             ORDER BY e.id
        );
        """;
    return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
}

static async Task WithDatabaseAsync(string name, Func<SqliteDatabase, Task> test)
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultServiceTests", $"{name}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var database = new SqliteDatabase(Path.Combine(directory, $"{name}.sqlite"));
        database.Initialize();
        await test(database);
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}

static void SeedLibrary(SqliteDatabase database, params (long Id, string Title, bool Completed)[] broadcasts)
{
    using var connection = database.OpenConnection();
    using var transaction = connection.BeginTransaction();
    var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    foreach (var broadcast in broadcasts)
    {
        using var episode = connection.CreateCommand();
        episode.Transaction = transaction;
        episode.CommandText = """
            INSERT INTO episodes(id,collection_id,title,status,date_added,updated_at,broadcast_uid,hidden,air_date)
            VALUES($id,(SELECT id FROM collections WHERE name='Unsorted'),$title,$status,$now,$now,$uid,0,$date);
            INSERT INTO playback_state(episode_id,position_ms,duration_ms,completed,last_played_at)
            VALUES($id,$position,3600000,$completed,$now);
            """;
        episode.Parameters.AddWithValue("$id", broadcast.Id);
        episode.Parameters.AddWithValue("$title", broadcast.Title);
        episode.Parameters.AddWithValue("$status", broadcast.Completed ? "Completed" : "Unplayed");
        episode.Parameters.AddWithValue("$uid", $"SAVED-{broadcast.Id}");
        episode.Parameters.AddWithValue("$date", $"2026-01-{Math.Clamp((int)(broadcast.Id % 28) + 1, 1, 28):00}");
        episode.Parameters.AddWithValue("$position", broadcast.Completed ? 3_600_000 : 0);
        episode.Parameters.AddWithValue("$completed", broadcast.Completed ? 1 : 0);
        episode.Parameters.AddWithValue("$now", now);
        episode.ExecuteNonQuery();
    }
    transaction.Commit();
}

static void SetCompleted(SqliteDatabase database, long episodeId, bool completed)
{
    using var connection = database.OpenConnection();
    using var command = connection.CreateCommand();
    command.CommandText = """
        UPDATE playback_state SET completed=$completed,position_ms=CASE WHEN $completed=1 THEN duration_ms ELSE 0 END
         WHERE episode_id=$id;
        UPDATE episodes SET status=CASE WHEN $completed=1 THEN 'Completed' ELSE 'Unplayed' END WHERE id=$id;
        """;
    command.Parameters.AddWithValue("$completed", completed ? 1 : 0);
    command.Parameters.AddWithValue("$id", episodeId);
    command.ExecuteNonQuery();
}

static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try { await action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static void Equal<T>(T expected, T actual, string context)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {context} to be {expected}, got {actual}.");
}
