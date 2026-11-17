using System.Text.Json;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Per-laptop record of RunScore files this machine has already had in the Library session.
/// Stored under <c>._checkin</c> and never uploaded. Checkout uses it so a file the timer
/// deleted locally is removed from OneDrive instead of downloaded again.
/// A missing or unreadable index downloads as before — it never deletes a file this laptop
/// has not previously seen.
/// </summary>
public static class SessionCloudFileIndex
{
    public const string FileName = "cloud-file-index.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static string GetPath(string sessionPath) =>
        Path.Combine(sessionPath, PathService.AppMetaFolderName, FileName);

    public static string Normalize(string relative) =>
        FinishUploadPlanner.NormalizeRelative(relative);

    /// <summary>
    /// RunScore files whose local absence should be deleted on OneDrive.
    /// Logs, Events, <c>_autosave</c>, ignored-chip reads, and <c>Entries.DDD</c> stay on OneDrive
    /// even when this laptop does not keep a copy.
    /// </summary>
    public static bool IsDeletableRunScoreFile(string sessionRelative)
    {
        if (string.IsNullOrWhiteSpace(sessionRelative))
            return false;

        var norm = Normalize(sessionRelative);
        if (norm.Contains("..", StringComparison.Ordinal))
            return false;

        var parts = norm.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length < 2)
            return false;
        if (!parts[0].Equals(PathService.RunScoreFolderName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (parts.Any(GraphSyncPolicy.ShouldSkipFolder)
            || parts.Any(GraphSyncPolicy.ShouldSkipRunScoreDownloadFolder)
            || parts.Any(p => p.Equals(PathService.AppMetaFolderName, StringComparison.OrdinalIgnoreCase)))
            return false;

        var name = parts[^1];
        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith('~')
            || GraphSyncPolicy.IsEntriesDesignBackup(name)
            || GraphSyncPolicy.IsIgnoredChipReadFile(name))
            return false;

        return true;
    }

    public static Dictionary<string, long> Load(string sessionPath)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = GetPath(sessionPath);
            if (!File.Exists(path))
                return map;

            var root = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(path), JsonOptions);
            if (root?.Files is null)
                return map;

            foreach (var file in root.Files)
            {
                if (string.IsNullOrWhiteSpace(file.Path) || !IsDeletableRunScoreFile(file.Path))
                    continue;
                map[Normalize(file.Path)] = file.Length;
            }
        }
        catch
        {
            // Unreadable index → download, do not invent deletes.
        }

        return map;
    }

    /// <summary>
    /// Add every deletable RunScore file currently on disk. Does not drop entries, so a
    /// partial pass cannot forget a file the timer removed.
    /// </summary>
    public static void RememberLocalFiles(string sessionPath)
    {
        try
        {
            var runScore = Path.Combine(sessionPath, PathService.RunScoreFolderName);
            var map = Load(sessionPath);
            if (Directory.Exists(runScore))
            {
                foreach (var file in Directory.EnumerateFiles(runScore, "*", SearchOption.AllDirectories))
                {
                    var under = Path.GetRelativePath(runScore, file);
                    var rel = Normalize(Path.Combine(PathService.RunScoreFolderName, under));
                    if (!IsDeletableRunScoreFile(rel))
                        continue;
                    map[rel] = new FileInfo(file).Length;
                }
            }

            Save(sessionPath, map);
        }
        catch
        {
            // Next checkout records the list again.
        }
    }

    public static void Forget(string sessionPath, IEnumerable<string> relativePaths)
    {
        try
        {
            var map = Load(sessionPath);
            var changed = false;
            foreach (var rel in relativePaths)
            {
                if (string.IsNullOrWhiteSpace(rel))
                    continue;
                if (map.Remove(Normalize(rel)))
                    changed = true;
            }

            if (changed)
                Save(sessionPath, map);
        }
        catch
        {
            // Stale index entry retries a 404 delete. It does not re-download.
        }
    }

    /// <summary>
    /// True when this laptop previously stored the file at the same size and it is gone locally.
    /// A different remote size is a new copy from another laptop — download it.
    /// Unknown remote size downloads too.
    /// </summary>
    public static bool ShouldDeleteInsteadOfDownload(
        string sessionRelative,
        bool localExists,
        long? remoteSize,
        IReadOnlyDictionary<string, long> index)
    {
        if (localExists || remoteSize is not long size)
            return false;
        if (!IsDeletableRunScoreFile(sessionRelative))
            return false;
        return index.TryGetValue(Normalize(sessionRelative), out var indexed) && indexed == size;
    }

    public static bool TryDeleteLocalFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return true;

            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            File.Delete(path);
            return !File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static void Save(string sessionPath, Dictionary<string, long> map)
    {
        var folder = Path.Combine(sessionPath, PathService.AppMetaFolderName);
        Directory.CreateDirectory(folder);
        var dto = new IndexFile
        {
            Files = map
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => new IndexEntry { Path = p.Key, Length = p.Value })
                .ToList()
        };
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        File.WriteAllText(GetPath(sessionPath), json);
    }

    private sealed class IndexFile
    {
        public List<IndexEntry> Files { get; set; } = new();
    }

    private sealed class IndexEntry
    {
        public string Path { get; set; } = "";
        public long Length { get; set; }
    }
}
