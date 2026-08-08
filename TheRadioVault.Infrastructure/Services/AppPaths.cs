using System.IO;

namespace TheRadioVault.Services;

public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TheRadioVault");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string ArtworkDirectory => Path.Combine(DataDirectory, "Artwork");
    public static string DatabasePath => Path.Combine(DataDirectory, "radio_vault.db");
    public static string BackupDirectory { get { var path = Path.Combine(DataDirectory, "Backups"); Directory.CreateDirectory(path); return path; } }
}
