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
/// <para>
/// RSServer has one prefs hive — race-day cannot use a separate store. Before SOP write we
/// snapshot non-race alternate folders; <see cref="RestoreNativePrefs"/> puts them back on Finish
/// so a native (non-app) RSServer launch does not keep the race Backup paths.
/// </para>
/// <para>
/// External Editor → Notepad++ is machine policy when that exe exists: set on app start / install,
/// re-applied after <see cref="RestoreNativePrefs"/> so Finish does not revert to Windows Notepad.
/// </para>
/// <para>
/// Vendor credentials (SMS / RaceRoster / RunSignUp) come from the OneDrive secrets cache — never from
/// Admin SOP. Pinned on ApplySop and again after RestoreNativePrefs; excluded from native snapshot restore.
/// </para>
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

    public const string SmsLogin = "SMS login";
    public const string SmsPassword = "SMS password";
    public const string RaceRosterLogin = "RaceRoster login";
    public const string RaceRosterPassword = "RaceRoster password";
    public const string RunSignUpLogin = "RunSignUp login";
    public const string RunSignUpPassword = "RunSignUp password";

    public static readonly string[] VendorCredentialNames =
    [
        SmsLogin,
        SmsPassword,
        RaceRosterLogin,
        RaceRosterPassword,
        RunSignUpLogin,
        RunSignUpPassword
    ];

    private readonly IRunScorePreferenceStore _store;
    private readonly IRunScoreSopStore _sopStore;
    private readonly INativeRunScorePrefsStore _nativeStore;
    private readonly IRunScoreVendorSecretsStore _vendorSecrets;

    public RunScorePreferencesService(
        IRunScorePreferenceStore? store = null,
        IRunScoreSopStore? sopStore = null,
        INativeRunScorePrefsStore? nativeStore = null,
        IRunScoreVendorSecretsStore? vendorSecrets = null)
    {
        _store = store ?? CreateDefaultStore();
        _sopStore = sopStore ?? new RunScoreSopStore();
        _nativeStore = nativeStore ?? new NativeRunScorePrefsStore();
        _vendorSecrets = vendorSecrets ?? new RunScoreVendorSecretsStore();
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
        // RunScore concatenates alternate-dir + "ENTRIES.DTA" with no separator.
        // Without a trailing backslash, files land as siblings of the Backup folder
        // (e.g. "…\2026 - EventTESTENTRIES.DTA" instead of "…\2026 - Event\ENTRIES.DTA").
        var alt = WithTrailingSeparator(backupPath ?? string.Empty);
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
        {
            // Vendor credentials are company secrets — never apply from editable SOP JSON.
            if (IsVendorCredentialName(name))
                continue;
            list.Add(SopPreference.String(name, value ?? string.Empty));
        }

        return list;
    }

    /// <param name="backupPath">This checkout’s RaceDay Backup folder (SOP alternate dirs).</param>
    /// <param name="raceDayRoot">
    /// Optional RaceDay root — used so we don’t snapshot race Backup paths as “office” prefs.
    /// </param>
    public PreferenceApplyResult ApplySop(string backupPath, string? raceDayRoot = null)
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
        TrySnapshotNativePrefs(desired, backupPath, raceDayRoot);

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

        var vendor = EnsureVendorCredentials();
        if (vendor.UpdatedCount > 0)
        {
            foreach (var name in vendor.UpdatedNames)
            {
                if (!updated.Contains(name, StringComparer.OrdinalIgnoreCase))
                    updated.Add(name);
            }
        }

        var msg = updated.Count == 0
            ? $"RunScore preferences already match SOP ({matched} checks)."
            : $"Updated {updated.Count} RunScore preference(s) to SOP ({matched} already matched).";

        if (!string.IsNullOrWhiteSpace(loaded.Warning))
            msg += " " + loaded.Warning;
        if (!string.IsNullOrWhiteSpace(vendor.Message) && vendor.UpdatedCount > 0)
            msg += " " + vendor.Message;

        return new PreferenceApplyResult
        {
            Success = true,
            UpdatedCount = updated.Count,
            AlreadyMatchedCount = matched,
            UpdatedNames = updated,
            Message = msg
        };
    }

    /// <summary>
    /// Restore office/native prefs captured before the last race-day SOP write.
    /// Call on Finish (and app start with no Active) so native RSServer does not keep race Backup paths.
    /// When Notepad++ is installed, External Editor is pinned again afterward (not reverted to Notepad).
    /// </summary>
    /// <param name="notepadPlusPlusExePath">Override for tests; default Program Files path.</param>
    /// <param name="fileExists">Override for tests; default <see cref="File.Exists(string)"/>.</param>
    public PreferenceApplyResult RestoreNativePrefs(
        string? notepadPlusPlusExePath = null,
        Func<string, bool>? fileExists = null)
    {
        if (!_store.IsAvailable)
        {
            return new PreferenceApplyResult
            {
                Success = true,
                Message = "Native RunScore prefs restore skipped (not Windows)."
            };
        }

        var snap = _nativeStore.Load();
        if (snap is null)
        {
            var pinnedOnly = EnsureNotepadPlusPlusExternalEditor(notepadPlusPlusExePath, fileExists);
            return new PreferenceApplyResult
            {
                Success = true,
                UpdatedCount = pinnedOnly.UpdatedCount,
                UpdatedNames = pinnedOnly.UpdatedNames,
                Message = string.IsNullOrWhiteSpace(pinnedOnly.Message)
                    ? "No saved office RunScore prefs to restore."
                    : "No saved office RunScore prefs to restore. " + pinnedOnly.Message
            };
        }

        var updated = new List<string>();
        try
        {
            foreach (var (name, value) in snap.Bools)
            {
                if (_store.TryGetBool(name) == value)
                    continue;
                _store.SetBool(name, value);
                updated.Add(name);
            }

            foreach (var (name, value) in snap.Ints)
            {
                if (_store.TryGetInt(name) == value)
                    continue;
                _store.SetInt(name, value);
                updated.Add(name);
            }

            foreach (var (name, value) in snap.Strings)
            {
                // Notepad++ External Editor is machine policy — don't restore a blank / Notepad snapshot.
                if (IsExternalEditorName(name)
                    && NotepadPlusPlusInstalled(notepadPlusPlusExePath, fileExists))
                    continue;

                // Company vendor credentials — never restore a stale office snapshot over them.
                if (IsVendorCredentialName(name))
                    continue;

                var desired = value ?? string.Empty;
                if (IsAlternateFolderPref(name))
                    desired = WithTrailingSeparator(desired);
                var current = _store.TryGetString(name) ?? string.Empty;
                if (string.Equals(current, desired, StringComparison.OrdinalIgnoreCase))
                    continue;
                _store.SetString(name, desired);
                updated.Add(name);
            }
        }
        catch (Exception ex)
        {
            return new PreferenceApplyResult
            {
                Success = false,
                UpdatedCount = updated.Count,
                UpdatedNames = updated,
                Message = "Failed restoring office RunScore prefs: " + ex.Message
            };
        }

        var pin = EnsureNotepadPlusPlusExternalEditor(notepadPlusPlusExePath, fileExists);
        if (pin.UpdatedCount > 0)
        {
            foreach (var name in pin.UpdatedNames)
            {
                if (!updated.Contains(name, StringComparer.OrdinalIgnoreCase))
                    updated.Add(name);
            }
        }

        var vendor = EnsureVendorCredentials();
        if (vendor.UpdatedCount > 0)
        {
            foreach (var name in vendor.UpdatedNames)
            {
                if (!updated.Contains(name, StringComparer.OrdinalIgnoreCase))
                    updated.Add(name);
            }
        }

        var msg = updated.Count == 0
            ? "Office RunScore prefs already restored."
            : $"Restored {updated.Count} office RunScore preference(s) for native RSServer.";
        if (!string.IsNullOrWhiteSpace(pin.Message) && pin.UpdatedCount > 0)
            msg += " " + pin.Message;
        if (!string.IsNullOrWhiteSpace(vendor.Message) && vendor.UpdatedCount > 0)
            msg += " " + vendor.Message;

        return new PreferenceApplyResult
        {
            Success = true,
            UpdatedCount = updated.Count,
            UpdatedNames = updated,
            Message = msg
        };
    }

    /// <summary>
    /// Pin SMS / RaceRoster / RunSignUp into HKCU.
    /// SMS always from the OneDrive secrets cache.
    /// RaceRoster / RunSignUp use Admin overrides when that login is set; otherwise the cache.
    /// Custom logins are never overwritten by a later secrets.json pull.
    /// </summary>
    public PreferenceApplyResult EnsureVendorCredentials(
        RunScoreVendorSecrets? secrets = null,
        VendorCredentialOverrides? overrides = null)
    {
        if (!_store.IsAvailable)
        {
            return new PreferenceApplyResult
            {
                Success = true,
                Message = "Vendor credentials pin skipped (not Windows)."
            };
        }

        var src = secrets ?? _vendorSecrets.Load();
        var ovr = overrides ?? LoadOverridesFromSettings();
        var updated = new List<string>();

        try
        {
            if (src is not null)
                PinCompletePair(SmsLogin, SmsPassword, src.SmsLogin, src.SmsPassword, updated);

            if (ovr.HasRaceRosterLogin)
                PinCustomLogin(RaceRosterLogin, RaceRosterPassword, ovr.RaceRosterLogin, ovr.RaceRosterPassword, updated);
            else if (src is not null)
                PinCompletePair(RaceRosterLogin, RaceRosterPassword, src.RaceRosterLogin, src.RaceRosterPassword, updated);

            if (ovr.HasRunSignUpLogin)
                PinCustomLogin(RunSignUpLogin, RunSignUpPassword, ovr.RunSignUpLogin, ovr.RunSignUpPassword, updated);
            else if (src is not null)
                PinCompletePair(RunSignUpLogin, RunSignUpPassword, src.RunSignUpLogin, src.RunSignUpPassword, updated);
        }
        catch (Exception ex)
        {
            return new PreferenceApplyResult
            {
                Success = false,
                UpdatedCount = updated.Count,
                UpdatedNames = updated,
                Message = "Could not set RunScore vendor credentials: " + ex.Message
            };
        }

        if (updated.Count == 0)
        {
            var hasSource = src?.HasAnyCompletePair == true
                || ovr.HasRaceRosterLogin
                || ovr.HasRunSignUpLogin;
            return new PreferenceApplyResult
            {
                Success = true,
                Message = hasSource
                    ? "RunScore vendor credentials already match."
                    : "No vendor secrets cache or Admin overrides — SMS / RaceRoster / RunSignUp unchanged."
            };
        }

        return new PreferenceApplyResult
        {
            Success = true,
            UpdatedCount = updated.Count,
            UpdatedNames = updated,
            Message = $"Updated {updated.Count} RunScore vendor credential value(s)."
        };
    }

    private static VendorCredentialOverrides LoadOverridesFromSettings()
    {
        try
        {
            return VendorCredentialOverrides.From(new SettingsStore().Load());
        }
        catch
        {
            return new VendorCredentialOverrides();
        }
    }

    /// <summary>Company secrets pair — both login and password required.</summary>
    private void PinCompletePair(
        string loginName,
        string passwordName,
        string? login,
        string? password,
        List<string> updated)
    {
        if (!RunScoreVendorSecrets.IsComplete(login, password))
            return;
        PinString(loginName, login!.Trim(), updated);
        PinString(passwordName, password!, updated);
    }

    /// <summary>
    /// Admin override: always write login; write password only when the override password is non-empty
    /// (blank password keeps the current registry password).
    /// </summary>
    private void PinCustomLogin(
        string loginName,
        string passwordName,
        string login,
        string? password,
        List<string> updated)
    {
        PinString(loginName, login.Trim(), updated);
        if (!string.IsNullOrEmpty(password))
            PinString(passwordName, password, updated);
    }

    private void PinString(string name, string value, List<string> updated)
    {
        var current = _store.TryGetString(name) ?? string.Empty;
        if (string.Equals(current, value, StringComparison.Ordinal))
            return;
        _store.SetString(name, value);
        updated.Add(name);
    }

    public static bool IsVendorCredentialName(string? name) =>
        !string.IsNullOrEmpty(name)
        && VendorCredentialNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When Notepad++ is on disk at the SOP path, write RunScore External Editor to that path.
    /// Survives Finish / app close. No-op if the exe is missing.
    /// </summary>
    public PreferenceApplyResult EnsureNotepadPlusPlusExternalEditor(
        string? exePath = null,
        Func<string, bool>? fileExists = null)
    {
        if (!_store.IsAvailable)
        {
            return new PreferenceApplyResult
            {
                Success = true,
                Message = "External Editor pin skipped (not Windows)."
            };
        }

        var path = string.IsNullOrWhiteSpace(exePath)
            ? RunScoreSopProfile.NotepadPlusPlusExePath
            : exePath.Trim();
        var exists = fileExists ?? File.Exists;
        if (!exists(path))
        {
            return new PreferenceApplyResult
            {
                Success = true,
                Message = "Notepad++ not at expected path — External Editor unchanged."
            };
        }

        try
        {
            var current = _store.TryGetString(RunScoreSopProfile.ExternalEditorName) ?? string.Empty;
            if (string.Equals(NormalizePathish(current), NormalizePathish(path), StringComparison.OrdinalIgnoreCase))
            {
                return new PreferenceApplyResult
                {
                    Success = true,
                    AlreadyMatchedCount = 1,
                    Message = "RunScore External Editor already points at Notepad++."
                };
            }

            _store.SetString(RunScoreSopProfile.ExternalEditorName, path);
            return new PreferenceApplyResult
            {
                Success = true,
                UpdatedCount = 1,
                UpdatedNames = new[] { RunScoreSopProfile.ExternalEditorName },
                Message = "RunScore External Editor set to Notepad++."
            };
        }
        catch (Exception ex)
        {
            return new PreferenceApplyResult
            {
                Success = false,
                Message = "Could not set RunScore External Editor: " + ex.Message
            };
        }
    }

    private static bool IsExternalEditorName(string name) =>
        string.Equals(name, RunScoreSopProfile.ExternalEditorName, StringComparison.OrdinalIgnoreCase);

    private static bool NotepadPlusPlusInstalled(string? exePath = null, Func<string, bool>? fileExists = null)
    {
        var path = string.IsNullOrWhiteSpace(exePath)
            ? RunScoreSopProfile.NotepadPlusPlusExePath
            : exePath.Trim();
        var exists = fileExists ?? File.Exists;
        return exists(path);
    }

    /// <summary>
    /// True when <paramref name="path"/> is this checkout’s Backup or under RaceDay\Backup.
    /// </summary>
    public static bool IsRaceDayBackupPath(string? path, string backupPath, string? raceDayRoot = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var cur = NormalizePathish(path);
        var bak = NormalizePathish(backupPath);
        if (string.Equals(cur, bak, StringComparison.OrdinalIgnoreCase))
            return true;

        string backupRoot;
        if (!string.IsNullOrWhiteSpace(raceDayRoot))
            backupRoot = NormalizePathish(Path.Combine(raceDayRoot, PathService.BackupFolderName));
        else
        {
            // Walk up until a folder named Backup (RaceDay layout).
            backupRoot = "";
            var walk = bak;
            while (!string.IsNullOrEmpty(walk))
            {
                if (string.Equals(Path.GetFileName(walk), PathService.BackupFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    backupRoot = walk;
                    break;
                }
                walk = Path.GetDirectoryName(walk) ?? "";
            }
        }

        if (string.IsNullOrEmpty(backupRoot))
            return false;

        return cur.StartsWith(backupRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || cur.StartsWith(backupRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || string.Equals(cur, backupRoot, StringComparison.OrdinalIgnoreCase);
    }

    private void TrySnapshotNativePrefs(
        IReadOnlyList<SopPreference> desired,
        string backupPath,
        string? raceDayRoot)
    {
        try
        {
            // If registry already points at race Backup, keep the prior office snapshot.
            var entries = _store.TryGetString(SaveEntriesDir);
            var results = _store.TryGetString(SaveResultsDir);
            if (IsRaceDayBackupPath(entries, backupPath, raceDayRoot)
                || IsRaceDayBackupPath(results, backupPath, raceDayRoot))
                return;

            var snap = new NativeRunScorePrefsSnapshot { CapturedAt = DateTimeOffset.UtcNow };
            foreach (var pref in desired)
            {
                switch (pref.Kind)
                {
                    case SopPreferenceKind.Bool:
                        if (_store.TryGetBool(pref.Name) is { } b)
                            snap.Bools[pref.Name] = b;
                        break;
                    case SopPreferenceKind.Int:
                        if (_store.TryGetInt(pref.Name) is { } i)
                            snap.Ints[pref.Name] = i;
                        break;
                    case SopPreferenceKind.String:
                        if (_store.TryGetString(pref.Name) is { } s)
                            snap.Strings[pref.Name] = s;
                        break;
                }
            }

            // Always capture alternate dirs even if empty (office may clear them).
            snap.Strings[SaveEntriesDir] = entries ?? string.Empty;
            snap.Strings[SaveResultsDir] = results ?? string.Empty;
            if (_store.TryGetInt(AutoSaveCounts) is { } counts)
                snap.Ints[AutoSaveCounts] = counts;

            _nativeStore.Save(snap);
        }
        catch
        {
            // Non-fatal — race-day SOP still applies; native restore may be missing once.
        }
    }

    private static string ExpandToken(string? template, string backupPathWithSep)
    {
        var t = template ?? string.Empty;
        if (string.IsNullOrWhiteSpace(t) ||
            t.Equals(RunScoreSopProfile.BackupPathToken, StringComparison.OrdinalIgnoreCase))
            return backupPathWithSep;

        var expanded = t.Replace(
            RunScoreSopProfile.BackupPathToken,
            backupPathWithSep.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
        return WithTrailingSeparator(expanded);
    }

    /// <summary>RunScore requires a trailing separator on alternate folder paths.</summary>
    public static string WithTrailingSeparator(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path ?? string.Empty;
        // Trim both separators explicitly — on non-Windows hosts Path.DirectorySeparatorChar
        // is '/', so TrimEnd(Path.*) would leave a trailing '\' and we'd double it.
        var trimmed = path.TrimEnd('\\', '/');
        // Always use backslash for RunScore (HKCU is Windows-only).
        return trimmed + '\\';
    }

    private bool Matches(SopPreference pref) =>
        pref.Kind switch
        {
            SopPreferenceKind.Bool => _store.TryGetBool(pref.Name) == pref.BoolValue,
            SopPreferenceKind.Int => _store.TryGetInt(pref.Name) == pref.IntValue,
            SopPreferenceKind.String => MatchesString(pref.Name, pref.StringValue),
            _ => false
        };

    private bool MatchesString(string name, string desired)
    {
        var current = _store.TryGetString(name) ?? string.Empty;
        desired ??= string.Empty;

        // Alternate-folder paths are load-bearing: RunScore concatenates dir + "ENTRIES.DTA"
        // with no separator. NormalizePathish used to strip '\' so a registry value without
        // the trailing slash looked "already matched" and never got repaired.
        if (IsAlternateFolderPref(name))
        {
            return string.Equals(current, WithTrailingSeparator(desired), StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(
            NormalizePathish(current),
            NormalizePathish(desired),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAlternateFolderPref(string name) =>
        string.Equals(name, SaveEntriesDir, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, SaveResultsDir, StringComparison.OrdinalIgnoreCase);

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
        return value.Trim().TrimEnd('\\', '/');
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
