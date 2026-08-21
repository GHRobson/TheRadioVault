namespace TheRadioVault.Data.Database.Migrations;

internal static class SqliteMigrationCatalog
{
    public const int LegacySchemaVersion = 47;

    public static SqliteMigrationRunner Runner { get; } = new(
        LegacySchemaVersion,
        [
            new Migration048CreateMigrationHistory(),
            new Migration049CreateSavedCollections(),
            new Migration050CreateRssFeedSubscriptions(),
            new Migration051CreateLiveRadioSchedule(),
            new Migration052CreateManagedArchiveOwnership()
        ]);
}
