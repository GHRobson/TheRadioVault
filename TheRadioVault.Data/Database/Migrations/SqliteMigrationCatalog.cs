namespace TheRadioVault.Data.Database.Migrations;

internal static class SqliteMigrationCatalog
{
    public const int LegacySchemaVersion = 47;

    public static SqliteMigrationRunner Runner { get; } = new(
        LegacySchemaVersion,
        [
            new Migration048CreateMigrationHistory()
        ]);
}
