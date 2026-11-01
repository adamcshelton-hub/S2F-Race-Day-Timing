using System.Text.Json;
using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _path;

    public SettingsStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "S2FRaceDayCheckout",
            "settings.json");
    }

    public string SettingsPath => _path;

    public AppSettings Load()
    {
        if (!File.Exists(_path))
            return ApplyFixedPaths(new AppSettings());

        try
        {
            var json = File.ReadAllText(_path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            return ApplyFixedPaths(settings);
        }
        catch
        {
            return ApplyFixedPaths(new AppSettings());
        }
    }

    public void Save(AppSettings settings)
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        // AdminPin is JsonIgnore — never persisted; always AppSettings.DefaultAdminPin at runtime.
        // Library + RaceDay roots are company-fixed under C:\S2F — never persist overrides.
        ApplyFixedPaths(settings);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
    }

    /// <summary>
    /// Graph-era installs always use <c>C:\S2F\Library</c> and <c>C:\S2F\RaceDay</c>.
    /// Migrates older settings that pointed at OneDrive Desktop paths.
    /// Heartbeat / soft-takeover windows are company-fixed (not per-laptop tuning).
    /// </summary>
    public static AppSettings ApplyFixedPaths(AppSettings settings)
    {
        settings.LibraryRoot = AppSettings.DefaultLibraryRoot;
        settings.RaceDayRoot = AppSettings.DefaultRaceDayRoot;
        settings.HeartbeatSeconds = AppSettings.DefaultHeartbeatSeconds;
        settings.StaleLockMinutes = AppSettings.DefaultStaleLockMinutes;
        // Mistaken default: Explorer label "Start 2 - Personal" is the OD sync root, not a Graph child.
        // Offices (Test Office, Memphis, …) live at My files — empty GraphDriveRoot.
        if (string.Equals(settings.GraphDriveRoot?.Trim(), "Start 2 - Personal", StringComparison.OrdinalIgnoreCase))
            settings.GraphDriveRoot = string.Empty;
        return settings;
    }

    /// <summary>Load + persist path/Graph-root migrations so Admin and next launch stay correct.</summary>
    public AppSettings LoadAndPersistMigrations()
    {
        var settings = Load();
        Save(settings);
        return settings;
    }
}
