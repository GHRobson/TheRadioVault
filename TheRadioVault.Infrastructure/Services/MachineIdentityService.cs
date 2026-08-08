using System.Text.Json;

namespace TheRadioVault.Services;

/// <summary>
/// Stores a per-installation identity outside the Radio Vault database. Database
/// backups intentionally do not include this file, so restoring another machine's backup
/// onto a desktop cannot make both computers claim the same machine identity.
/// </summary>
public sealed class MachineIdentityService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public MachineIdentityService(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.DataDirectory, "machine-identity.json");
    }

    public MachineIdentity LoadOrCreate()
    {
        try
        {
            if (File.Exists(_path))
            {
                var existing = JsonSerializer.Deserialize<MachineIdentity>(File.ReadAllText(_path), JsonOptions);
                if (existing is not null && Guid.TryParse(existing.MachineId, out _))
                {
                    var currentName = Environment.MachineName;
                    if (!string.Equals(existing.MachineName, currentName, StringComparison.Ordinal))
                    {
                        existing = existing with { MachineName = currentName };
                        Save(existing);
                    }
                    return existing;
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Machine identity", "Could not read the saved machine identity; a replacement will be created.", ex);
        }

        var created = new MachineIdentity(Guid.NewGuid().ToString("D"), Environment.MachineName, DateTimeOffset.UtcNow);
        Save(created);
        return created;
    }

    private void Save(MachineIdentity identity)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? AppPaths.DataDirectory);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(identity, JsonOptions));
        File.Move(temp, _path, true);
    }
}

public sealed record MachineIdentity(string MachineId, string MachineName, DateTimeOffset CreatedAt);
