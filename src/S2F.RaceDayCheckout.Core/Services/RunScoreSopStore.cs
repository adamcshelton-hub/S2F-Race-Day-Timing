using System.Text.Json;
using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

public sealed class RunScoreSopLoadResult
{
    public required RunScoreSopProfile Profile { get; init; }
    public bool UsedFallback { get; init; }
    public string? Warning { get; init; }
    public string Path { get; init; } = string.Empty;
}

public interface IRunScoreSopStore
{
    string SopPath { get; }
    RunScoreSopLoadResult Load();
    /// <summary>Ensure the SOP file exists on disk (seed defaults if missing). Returns path.</summary>
    string EnsureFile();
    void Save(RunScoreSopProfile profile);
}

public sealed class RunScoreSopStore : IRunScoreSopStore
{
    public const string FileName = "runscore-sop.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null // keep registry value names as-is
    };

    private readonly string _path;

    public RunScoreSopStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "S2FRaceDayCheckout",
            FileName);
    }

    public string SopPath => _path;

    public RunScoreSopLoadResult Load()
    {
        if (!File.Exists(_path))
        {
            var seeded = RunScoreSopProfile.CreateDefaults();
            try
            {
                Save(seeded);
            }
            catch (Exception ex)
            {
                return new RunScoreSopLoadResult
                {
                    Profile = seeded,
                    UsedFallback = true,
                    Warning = "Could not seed runscore-sop.json: " + ex.Message,
                    Path = _path
                };
            }

            return new RunScoreSopLoadResult
            {
                Profile = seeded,
                UsedFallback = false,
                Path = _path
            };
        }

        try
        {
            var json = File.ReadAllText(_path);
            var profile = JsonSerializer.Deserialize<RunScoreSopProfile>(json, JsonOptions);
            if (profile is null)
            {
                return new RunScoreSopLoadResult
                {
                    Profile = RunScoreSopProfile.CreateDefaults(),
                    UsedFallback = true,
                    Warning = "runscore-sop.json was empty — using built-in defaults.",
                    Path = _path
                };
            }

            NormalizeDictionaries(profile);
            if (ApplyNotepadPlusPlusDefault(profile))
            {
                try
                {
                    Save(profile);
                }
                catch
                {
                    // In-memory profile still has the path for this launch.
                }
            }

            return new RunScoreSopLoadResult
            {
                Profile = profile,
                UsedFallback = false,
                Path = _path
            };
        }
        catch (Exception ex)
        {
            return new RunScoreSopLoadResult
            {
                Profile = RunScoreSopProfile.CreateDefaults(),
                UsedFallback = true,
                Warning = "Invalid runscore-sop.json — using built-in defaults. " + ex.Message,
                Path = _path
            };
        }
    }

    public string EnsureFile()
    {
        // Load seeds a missing file and fills a blank External Editor on older SOP files.
        Load();
        return _path;
    }

    public void Save(RunScoreSopProfile profile)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(profile, JsonOptions));
    }

    private static void NormalizeDictionaries(RunScoreSopProfile profile)
    {
        // Re-wrap with ordinal comparer after deserialize (System.Text.Json uses default comparer).
        profile.Bools = new Dictionary<string, bool>(profile.Bools ?? new(), StringComparer.Ordinal);
        profile.Ints = new Dictionary<string, int>(profile.Ints ?? new(), StringComparer.Ordinal);
        profile.Strings = new Dictionary<string, string>(profile.Strings ?? new(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Older SOP files were seeded with a blank External Editor (RunScore then uses Notepad).
    /// Fill that blank with the Program Files path. A non-empty custom editor is left alone.
    /// </summary>
    internal static bool ApplyNotepadPlusPlusDefault(RunScoreSopProfile profile)
    {
        if (profile.Strings.TryGetValue(RunScoreSopProfile.ExternalEditorName, out var current)
            && !string.IsNullOrWhiteSpace(current))
            return false;

        profile.Strings[RunScoreSopProfile.ExternalEditorName] = RunScoreSopProfile.NotepadPlusPlusExePath;
        return true;
    }
}
