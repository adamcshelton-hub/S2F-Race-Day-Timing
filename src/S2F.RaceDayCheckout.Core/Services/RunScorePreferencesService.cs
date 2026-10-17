using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

public sealed class PreferenceApplyResult
{
    public bool Success { get; init; }
    public int UpdatedCount { get; init; }
    public int AlreadyMatchedCount { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<string> UpdatedNames { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Enforces Start2Finish RunScore Preferences SOP by writing
/// HKCU\Software\RunTime Software\RunScore before RSServer launches.
/// Registry value names match RSProfile::SaveToRegistry (not the JSON export keys).
/// SOP values come from runscore-sop.json (editable); built-in defaults are the fallback.
/// </summary>
public sealed class RunScorePreferencesService
{
    /// <summary>RunScore stores Minutes for Auto Save as counts = minutes × 300.</summary>
    public const int CountsPerMinute = 300;

    /// <summary>Built-in default when SOP file is missing/invalid.</summary>
    public const int SopAutoSaveMinutes = 1;

    public static int SopAutoSaveCounts => SopAutoSaveMinutes * CountsPerMinute;

    // Registry value names from RSProfile::LoadFromRegistry / SaveToRegistry
    public const string SaveEntriesDir = "Save entries dir";
    public const string SaveResultsDir = "Save results dir";
    public const string AutoSaveCounts = "Auto save counts";

    private readonly IRunScorePreferenceStore _store;
    private readonly IRunScoreSopStore _sopStore;

    public RunScorePreferencesService(
        IRunScorePreferenceStore? store = null,
        IRunScoreSopStore? sopStore = null)
    {
        _store = store ?? CreateDefaultStore();
        _sopStore = sopStore ?? new RunScoreSopStore();
    }

    private static IRunScorePreferenceStore CreateDefaultStore()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return CreateWindowsStore();
        return new UnavailableRunScorePreferenceStore();
    }

    [SupportedOSPlatform("windows")]
    private static IRunScorePreferenceStore CreateWindowsStore() => new WindowsRunScorePreferenceStore();

    /// <summary>Build prefs from built-in defaults (tests / fallback).</summary>
    public static IReadOnlyList<SopPreference> BuildSop(string backupPath) =>
        BuildSop(RunScoreSopProfile.CreateDefaults(), backupPath);

    public static IReadOnlyList<SopPreference> BuildSop(RunScoreSopProfile profile, string backupPath)
    {
        var alt = (backupPath ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var list = new List<SopPreference>();

        list.Add(SopPreference.String(SaveEntriesDir, ExpandToken(profile.SaveEntriesDir, alt)));
        list.Add(SopPreference.String(SaveResultsDir, ExpandToken(profile.SaveResultsDir, alt)));

        var minutes = profile.AutoSaveMinutes;
        if (double.IsNaN(minutes) || minutes < 0)
            minutes = SopAutoSaveMinutes;
        // RunScore allows fractional minutes (e.g. 0.1); counts are minutes × 300.
        var counts = (int)Math.Round(minutes * CountsPerMinute, MidpointRounding.AwayFromZero);
        list.Add(SopPreference.Int(AutoSaveCounts, counts));

        foreach (var (name, value) in profile.Bools)
            list.Add(SopPreference.Bool(name, value));
        foreach (var (name, value) in profile.Ints)
            list.Add(SopPreference.Int(name, value));
        foreach (var (name, value) in profile.Strings)
            list.Add(SopPreference.String(name, value ?? string.Empty));

        return list;
    }

    public PreferenceApplyResult ApplySop(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            return new PreferenceApplyResult
            {
                Success = false,
                Message = "Backup path is empty — cannot set RunScore alternate folders."
            };
        }

        if (!_store.IsAvailable)
        {
            // Soft skip on non-Windows (unit tests / Mac host). Race-day laptops are Windows.
            return new PreferenceApplyResult
            {
                Success = true,
                Message = "RunScore preference SOP skipped (not Windows)."
            };
        }

        try
        {
            Directory.CreateDirectory(backupPath);
        }
        catch (Exception ex)
        {
            return new PreferenceApplyResult
            {
                Success = false,
                Message = "Could not create Backup folder for RunScore alternate save: " + ex.Message
            };
        }

        var loaded = _sopStore.Load();
        var desired = BuildSop(loaded.Profile, backupPath);
        var updated = new List<string>();
        var matched = 0;

        try
        {
            foreach (var pref in desired)
            {
                if (Matches(pref))
                {
                    matched++;
                    continue;
                }

                Write(pref);
                updated.Add(pref.Name);
            }
        }
        catch (Exception ex)
        {
            return new PreferenceApplyResult
            {
                Success = false,
                UpdatedCount = updated.Count,
                AlreadyMatchedCount = matched,
                UpdatedNames = updated,
                Message = "Failed writing RunScore preferences: " + ex.Message
            };
        }

        var msg = updated.Count == 0
            ? $"RunScore preferences already match SOP ({matched} checks)."
            : $"Updated {updated.Count} RunScore preference(s) to SOP ({matched} already matched).";

        if (!string.IsNullOrWhiteSpace(loaded.Warning))
            msg += " " + loaded.Warning;

        return new PreferenceApplyResult
        {
            Success = true,
            UpdatedCount = updated.Count,
            AlreadyMatchedCount = matched,
            UpdatedNames = updated,
            Message = msg
        };
    }

    private static string ExpandToken(string? template, string backupPath)
    {
        var t = template ?? string.Empty;
        if (string.IsNullOrWhiteSpace(t) ||
            t.Equals(RunScoreSopProfile.BackupPathToken, StringComparison.OrdinalIgnoreCase))
            return backupPath;

        return t.Replace(RunScoreSopProfile.BackupPathToken, backupPath, StringComparison.OrdinalIgnoreCase)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private bool Matches(SopPreference pref) =>
        pref.Kind switch
        {
            SopPreferenceKind.Bool => _store.TryGetBool(pref.Name) == pref.BoolValue,
            SopPreferenceKind.Int => _store.TryGetInt(pref.Name) == pref.IntValue,
            SopPreferenceKind.String => string.Equals(
                NormalizePathish(_store.TryGetString(pref.Name)),
                NormalizePathish(pref.StringValue),
                StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private void Write(SopPreference pref)
    {
        switch (pref.Kind)
        {
            case SopPreferenceKind.Bool:
                _store.SetBool(pref.Name, pref.BoolValue);
                break;
            case SopPreferenceKind.Int:
                _store.SetInt(pref.Name, pref.IntValue);
                break;
            case SopPreferenceKind.String:
                _store.SetString(pref.Name, pref.StringValue);
                break;
        }
    }

    private static string NormalizePathish(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        return value.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

public enum SopPreferenceKind
{
    Bool,
    Int,
    String
}

public readonly record struct SopPreference(string Name, SopPreferenceKind Kind, bool BoolValue, int IntValue, string StringValue)
{
    public static SopPreference Bool(string name, bool value) =>
        new(name, SopPreferenceKind.Bool, value, 0, string.Empty);

    public static SopPreference Int(string name, int value) =>
        new(name, SopPreferenceKind.Int, false, value, string.Empty);

    public static SopPreference String(string name, string value) =>
        new(name, SopPreferenceKind.String, false, 0, value ?? string.Empty);
}
