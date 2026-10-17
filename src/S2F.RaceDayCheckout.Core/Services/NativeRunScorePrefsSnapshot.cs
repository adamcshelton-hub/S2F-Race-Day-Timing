using System.Text.Json;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Office / native RSServer prefs captured before race-day SOP overwrite.
/// RSServer has one HKCU hive — we cannot give race-day a separate prefs store;
/// snapshot + restore on Finish is the isolation we can own.
/// </summary>
public sealed class NativeRunScorePrefsSnapshot
{
    public Dictionary<string, bool> Bools { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Ints { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Strings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset CapturedAt { get; set; }
}

public interface INativeRunScorePrefsStore
{
    string Path { get; }
    NativeRunScorePrefsSnapshot? Load();
    void Save(NativeRunScorePrefsSnapshot snapshot);
    void Clear();
}

public sealed class NativeRunScorePrefsStore : INativeRunScorePrefsStore
{
    public const string FileName = "runscore-native-prefs.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;

    public NativeRunScorePrefsStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "S2FRaceDayCheckout",
            FileName);
    }

    public string Path => _path;

    public NativeRunScorePrefsSnapshot? Load()
    {
        if (!File.Exists(_path))
            return null;
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<NativeRunScorePrefsSnapshot>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void Save(NativeRunScorePrefsSnapshot snapshot)
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(snapshot, JsonOptions));
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch
        {
            // best-effort
        }
    }
}
