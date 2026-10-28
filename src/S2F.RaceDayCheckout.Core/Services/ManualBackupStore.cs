using System.Globalization;
using System.Text;
using System.Text.Json;
using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Local disk store for Manual Backup segments under session <c>._checkin/manual_backup/</c>.
/// </summary>
public sealed class ManualBackupStore
{
    public const string FolderName = "manual_backup";
    public const string ManifestFileName = "manifest.json";
    public const string DeviceFileName = "device.json";
    public const string CurrentFileName = "current.jsonl";
    public const string OfficeCatalogFileName = "manual_backup_catalog.json";
    public const string OfficeMetaFolderName = "._s2f";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };

    private static readonly JsonSerializerOptions LineOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = null
    };

    public static string GetRoot(string sessionPath) =>
        Path.Combine(sessionPath, PathService.AppMetaFolderName, FolderName);

    public static string GetOfficeCatalogPath(string libraryRoot, string office) =>
        Path.Combine(libraryRoot, office, OfficeMetaFolderName, OfficeCatalogFileName);

    public ManualBackupManifest LoadManifest(string sessionPath)
    {
        var path = Path.Combine(GetRoot(sessionPath), ManifestFileName);
        if (!File.Exists(path))
            return new ManualBackupManifest();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ManualBackupManifest>(json, JsonOpts) ?? new ManualBackupManifest();
        }
        catch
        {
            return new ManualBackupManifest();
        }
    }

    public void SaveManifest(string sessionPath, ManualBackupManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var root = GetRoot(sessionPath);
        Directory.CreateDirectory(root);
        manifest.UpdatedUtc = DateTimeOffset.UtcNow;
        var path = Path.Combine(root, ManifestFileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(manifest, JsonOpts));
        File.Copy(tmp, path, overwrite: true);
        try { File.Delete(tmp); } catch { /* ignore */ }
    }

    public void EnsureLocationFolders(string sessionPath, IEnumerable<string> locations)
    {
        var root = GetRoot(sessionPath);
        foreach (var loc in locations)
        {
            var name = SanitizeLocation(loc);
            if (string.IsNullOrEmpty(name))
                continue;
            Directory.CreateDirectory(Path.Combine(root, name));
        }
    }

    public static string SanitizeLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return "";
        var s = location.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s;
    }

    public static string SanitizeDeviceId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return "device";
        var s = deviceId.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s.Length > 64 ? s[..64] : s;
    }

    public string GetDeviceDir(string sessionPath, string location, string deviceId) =>
        Path.Combine(GetRoot(sessionPath), SanitizeLocation(location), SanitizeDeviceId(deviceId));

    public ManualBackupDeviceState LoadOrCreateDevice(
        string sessionPath,
        string location,
        string deviceId,
        string displayName)
    {
        var dir = GetDeviceDir(sessionPath, location, deviceId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, DeviceFileName);
        if (File.Exists(path))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<ManualBackupDeviceState>(
                    File.ReadAllText(path), JsonOpts);
                if (existing is not null)
                {
                    if (!string.IsNullOrWhiteSpace(displayName)
                        && !string.Equals(existing.DisplayName, displayName, StringComparison.Ordinal))
                    {
                        existing.DisplayName = displayName.Trim();
                        SaveDeviceState(sessionPath, location, existing);
                    }
                    return existing;
                }
            }
            catch { /* recreate */ }
        }

        var state = new ManualBackupDeviceState
        {
            DeviceId = SanitizeDeviceId(deviceId),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? deviceId : displayName.Trim(),
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        SaveDeviceState(sessionPath, location, state);
        EnsureCurrentFile(dir);
        RegisterDeviceInManifest(sessionPath, state.DeviceId, state.DisplayName);
        return state;
    }

    public void SaveDeviceState(string sessionPath, string location, ManualBackupDeviceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var dir = GetDeviceDir(sessionPath, location, state.DeviceId);
        Directory.CreateDirectory(dir);
        state.UpdatedUtc = DateTimeOffset.UtcNow;
        var path = Path.Combine(dir, DeviceFileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOpts));
        File.Copy(tmp, path, overwrite: true);
        try { File.Delete(tmp); } catch { /* ignore */ }
    }

    private void EnsureCurrentFile(string deviceDir)
    {
        var current = Path.Combine(deviceDir, CurrentFileName);
        if (!File.Exists(current))
            File.WriteAllText(current, "");
    }

    private void RegisterDeviceInManifest(string sessionPath, string deviceId, string displayName)
    {
        var manifest = LoadManifest(sessionPath);
        if (manifest.Devices.Any(d =>
                string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)))
            return;
        manifest.Devices.Add(new ManualBackupDeviceInfo
        {
            DeviceId = deviceId,
            DisplayName = displayName,
            CreatedUtc = DateTimeOffset.UtcNow
        });
        SaveManifest(sessionPath, manifest);
    }

    public ManualBackupTap AppendTap(
        string sessionPath,
        string location,
        string deviceId,
        string displayName,
        string bib,
        DateTimeOffset? timestampUtc = null,
        int? staffingUserId = null)
    {
        var state = LoadOrCreateDevice(sessionPath, location, deviceId, displayName);
        var dir = GetDeviceDir(sessionPath, location, deviceId);
        EnsureCurrentFile(dir);
        var tap = new ManualBackupTap
        {
            Id = Guid.NewGuid().ToString("N"),
            Bib = bib.Trim(),
            TimestampUtc = timestampUtc ?? DateTimeOffset.UtcNow,
            Location = location.Trim(),
            DeviceId = state.DeviceId,
            StaffingUserId = staffingUserId,
            CreatedUtc = DateTimeOffset.UtcNow
        };
        var line = JsonSerializer.Serialize(tap, LineOpts) + "\n";
        File.AppendAllText(Path.Combine(dir, CurrentFileName), line, Encoding.UTF8);
        return tap;
    }

    public bool TryVoidTap(string sessionPath, string location, string deviceId, string tapId)
    {
        var dir = GetDeviceDir(sessionPath, location, deviceId);
        var currentPath = Path.Combine(dir, CurrentFileName);
        if (!File.Exists(currentPath))
            return false;

        var state = LoadOrCreateDevice(sessionPath, location, deviceId, deviceId);
        var taps = ReadTaps(currentPath);
        var idx = taps.FindIndex(t => string.Equals(t.Id, tapId, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            return false;
        // Only void unplayed lines (index >= playCursor).
        if (idx < state.PlayCursor)
            return false;
        if (taps[idx].Voided)
            return true;
        taps[idx].Voided = true;
        WriteTaps(currentPath, taps);
        return true;
    }

    public IReadOnlyList<ManualBackupTap> ReadCurrentTaps(string sessionPath, string location, string deviceId) =>
        ReadTaps(Path.Combine(GetDeviceDir(sessionPath, location, deviceId), CurrentFileName));

    public IReadOnlyList<ManualBackupTap> ReadSegmentTaps(string segmentPath) =>
        ReadTaps(segmentPath);

    private static List<ManualBackupTap> ReadTaps(string path)
    {
        var list = new List<ManualBackupTap>();
        if (!File.Exists(path))
            return list;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                var tap = JsonSerializer.Deserialize<ManualBackupTap>(line, LineOpts);
                if (tap is not null)
                    list.Add(tap);
            }
            catch { /* skip bad line */ }
        }
        return list;
    }

    private static void WriteTaps(string path, List<ManualBackupTap> taps)
    {
        var tmp = path + ".tmp";
        using (var sw = new StreamWriter(tmp, false, Encoding.UTF8))
        {
            foreach (var t in taps)
                sw.WriteLine(JsonSerializer.Serialize(t, LineOpts));
        }
        File.Copy(tmp, path, overwrite: true);
        try { File.Delete(tmp); } catch { /* ignore */ }
    }

    /// <summary>Archive current.jsonl → {utcStamp}.jsonl and start empty current; reset play cursor.</summary>
    public string? CreateNewFile(string sessionPath, string location, string deviceId)
    {
        var dir = GetDeviceDir(sessionPath, location, deviceId);
        Directory.CreateDirectory(dir);
        var currentPath = Path.Combine(dir, CurrentFileName);
        EnsureCurrentFile(dir);

        string? archiveName = null;
        if (File.Exists(currentPath) && new FileInfo(currentPath).Length > 0)
        {
            archiveName = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".jsonl";
            var dest = Path.Combine(dir, archiveName);
            // Avoid clobber if same second.
            var n = 0;
            while (File.Exists(dest))
            {
                n++;
                archiveName = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)
                    + "-" + n.ToString(CultureInfo.InvariantCulture) + ".jsonl";
                dest = Path.Combine(dir, archiveName);
            }
            File.Copy(currentPath, dest, overwrite: false);
        }

        File.WriteAllText(currentPath, "");
        var state = LoadOrCreateDevice(sessionPath, location, deviceId, deviceId);
        state.PlayCursor = 0;
        SaveDeviceState(sessionPath, location, state);
        return archiveName;
    }

    public int CreateNewFileAll(string sessionPath)
    {
        var manifest = LoadManifest(sessionPath);
        if (!manifest.Enabled || manifest.Locations.Count == 0)
            return 0;

        var count = 0;
        foreach (var loc in manifest.Locations)
        {
            var locName = SanitizeLocation(loc);
            var locDir = Path.Combine(GetRoot(sessionPath), locName);
            if (!Directory.Exists(locDir))
                continue;

            foreach (var deviceDir in Directory.EnumerateDirectories(locDir))
            {
                var deviceId = Path.GetFileName(deviceDir);
                CreateNewFile(sessionPath, loc, deviceId);
                count++;
            }

            // Also rotate devices known in manifest that have no folder yet (creates empty current).
            foreach (var d in manifest.Devices)
            {
                var dir = GetDeviceDir(sessionPath, loc, d.DeviceId);
                if (!Directory.Exists(dir))
                {
                    LoadOrCreateDevice(sessionPath, loc, d.DeviceId, d.DisplayName);
                    CreateNewFile(sessionPath, loc, d.DeviceId);
                    count++;
                }
            }
        }

        return count;
    }

    public IReadOnlyList<string> ListArchiveFiles(string sessionPath, string location, string deviceId)
    {
        var dir = GetDeviceDir(sessionPath, location, deviceId);
        if (!Directory.Exists(dir))
            return Array.Empty<string>();
        return Directory.EnumerateFiles(dir, "*.jsonl")
            .Where(f => !string.Equals(Path.GetFileName(f), CurrentFileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Cast<string>()
            .ToList();
    }

    public IReadOnlyList<string> ListDeviceIds(string sessionPath, string location)
    {
        var locDir = Path.Combine(GetRoot(sessionPath), SanitizeLocation(location));
        if (!Directory.Exists(locDir))
            return Array.Empty<string>();
        return Directory.EnumerateDirectories(locDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Cast<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void SetPaused(string sessionPath, string location, string deviceId, bool paused)
    {
        var state = LoadOrCreateDevice(sessionPath, location, deviceId, deviceId);
        state.Paused = paused;
        SaveDeviceState(sessionPath, location, state);
    }

    public void SetPlayCursor(string sessionPath, string location, string deviceId, int cursor)
    {
        var state = LoadOrCreateDevice(sessionPath, location, deviceId, deviceId);
        state.PlayCursor = Math.Max(0, cursor);
        SaveDeviceState(sessionPath, location, state);
    }

    /// <summary>Unplayed, non-voided taps from Current starting at playCursor.</summary>
    public IReadOnlyList<ManualBackupTap> GetUnplayedCurrentTaps(
        string sessionPath,
        string location,
        string deviceId)
    {
        var state = LoadOrCreateDevice(sessionPath, location, deviceId, deviceId);
        var taps = ReadCurrentTaps(sessionPath, location, deviceId);
        if (state.PlayCursor >= taps.Count)
            return Array.Empty<ManualBackupTap>();
        return taps
            .Skip(state.PlayCursor)
            .Where(t => !t.Voided)
            .ToList();
    }

    public ManualBackupCatalog LoadOfficeCatalog(string libraryRoot, string office)
    {
        var path = GetOfficeCatalogPath(libraryRoot, office);
        if (!File.Exists(path))
            return new ManualBackupCatalog { Office = office };
        try
        {
            return JsonSerializer.Deserialize<ManualBackupCatalog>(File.ReadAllText(path), JsonOpts)
                ?? new ManualBackupCatalog { Office = office };
        }
        catch
        {
            return new ManualBackupCatalog { Office = office };
        }
    }

    public void UpsertOfficeCatalogSession(
        string libraryRoot,
        string office,
        string sessionRelativePath,
        string displayLeaf,
        IReadOnlyList<string> locations,
        bool enabled)
    {
        var catalog = LoadOfficeCatalog(libraryRoot, office);
        catalog.Office = office;
        catalog.Sessions.RemoveAll(s =>
            string.Equals(s.RelativePath, sessionRelativePath, StringComparison.OrdinalIgnoreCase));
        if (enabled && locations.Count > 0)
        {
            catalog.Sessions.Add(new ManualBackupCatalogSession
            {
                RelativePath = sessionRelativePath.Replace('\\', '/').Trim('/'),
                DisplayLeaf = displayLeaf,
                Locations = locations.ToList(),
                UpdatedUtc = DateTimeOffset.UtcNow
            });
        }

        catalog.Sessions = catalog.Sessions
            .OrderBy(s => s.DisplayLeaf, StringComparer.OrdinalIgnoreCase)
            .ToList();
        catalog.UpdatedUtc = DateTimeOffset.UtcNow;

        var path = GetOfficeCatalogPath(libraryRoot, office);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(catalog, JsonOpts));
        File.Copy(tmp, path, overwrite: true);
        try { File.Delete(tmp); } catch { /* ignore */ }
    }

    public void RemoveOfficeCatalogSession(string libraryRoot, string office, string sessionRelativePath)
    {
        UpsertOfficeCatalogSession(
            libraryRoot,
            office,
            sessionRelativePath,
            displayLeaf: "",
            locations: Array.Empty<string>(),
            enabled: false);
    }

    /// <summary>
    /// Drop the session and any nested race-group children when moving to Past Races.
    /// </summary>
    public void RemoveOfficeCatalogSessionsUnder(string libraryRoot, string office, string sessionRelativePath)
    {
        var norm = sessionRelativePath.Replace('\\', '/').Trim('/');
        var catalog = LoadOfficeCatalog(libraryRoot, office);
        var before = catalog.Sessions.Count;
        catalog.Sessions.RemoveAll(s =>
        {
            var rel = (s.RelativePath ?? "").Replace('\\', '/').Trim('/');
            return string.Equals(rel, norm, StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith(norm + "/", StringComparison.OrdinalIgnoreCase);
        });
        if (catalog.Sessions.Count == before)
            return;

        catalog.Office = office;
        catalog.UpdatedUtc = DateTimeOffset.UtcNow;
        var path = GetOfficeCatalogPath(libraryRoot, office);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(catalog, JsonOpts));
        File.Copy(tmp, path, overwrite: true);
        try { File.Delete(tmp); } catch { /* ignore */ }
    }

    /// <summary>Format TOD for RunScore Open from a UTC instant using local Windows zone.</summary>
    public static string FormatTodLocal(DateTimeOffset utc)
    {
        var local = utc.ToLocalTime();
        // H:MM:SS.ff — centiseconds like race gun stamps in this app.
        var cs = local.Millisecond / 10;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}:{1:D2}:{2:D2}.{3:D2}",
            local.Hour,
            local.Minute,
            local.Second,
            cs);
    }

    public static string GetStableWindowsDeviceId()
    {
        var machine = Environment.MachineName;
        var user = Environment.UserName;
        var raw = machine + "|" + user;
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        return "win-" + hash[..16].ToLowerInvariant();
    }

    public static string GetWindowsDeviceDisplayName() =>
        "Laptop · " + Environment.MachineName;
}

