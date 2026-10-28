using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Decides which session files need a Graph Finish upload.
/// Compares Active vs Library <b>before</b> finish:copy — download/checkout rewrites
/// Library timestamps, so "mtime since CheckedOutAt" wrongly uploads the whole pack.
/// </summary>
public static class FinishUploadPlanner
{
    /// <summary>
    /// Paths relative to the session root (e.g. <c>RUNSCORE\foo.dat</c>, <c>race.event</c>)
    /// that differ between Active and Library (or exist only on Active).
    /// </summary>
    public static IReadOnlyList<string> CollectChangedRelativePaths(ActiveCheckout active)
    {
        var results = new List<string>();
        CollectTreeDiffs(
            Path.Combine(active.ActivePath, PathService.RunScoreFolderName),
            Path.Combine(active.LibrarySessionPath, PathService.RunScoreFolderName),
            PathService.RunScoreFolderName,
            results);

        var activeDbs = Path.Combine(active.ActivePath, SharedFolderLinkService.DbsFolderName);
        var libraryDbs = Path.Combine(active.LibrarySessionPath, SharedFolderLinkService.DbsFolderName);
        if (Directory.Exists(activeDbs) && !IsReparsePoint(activeDbs))
        {
            CollectTreeDiffs(
                activeDbs,
                libraryDbs,
                SharedFolderLinkService.DbsFolderName,
                results);
        }

        foreach (var eventFile in Directory.EnumerateFiles(active.ActivePath, "*.event"))
        {
            var name = Path.GetFileName(eventFile);
            var libraryEvent = Path.Combine(active.LibrarySessionPath, name);
            if (NeedsUpload(eventFile, libraryEvent))
                results.Add(name);
        }

        return results;
    }

    /// <summary>
    /// Merge a prior Finish-retry pending list with a fresh Active-vs-Library diff
    /// (covers edits while waiting for cellular).
    /// </summary>
    public static IReadOnlyList<string> PlanUploadRelativePaths(ActiveCheckout active)
    {
        var fresh = CollectChangedRelativePaths(active).Select(NormalizeRelative).ToList();
        var pending = active.PendingFinishUploadRelativePaths;
        if (pending is null || pending.Count == 0)
            return fresh;

        var pendingNorm = pending.Select(NormalizeRelative).ToList();
        if (fresh.Count == 0)
            return pendingNorm;

        return pendingNorm
            .Concat(fresh)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Keep only session-relative paths that still exist under the library session.</summary>
    public static IReadOnlyList<string> ExistingLibraryRelativePaths(
        string librarySessionPath,
        IReadOnlyList<string>? relativePaths)
    {
        if (relativePaths is null || relativePaths.Count == 0)
            return Array.Empty<string>();

        var kept = new List<string>();
        foreach (var rel in relativePaths)
        {
            if (string.IsNullOrWhiteSpace(rel))
                continue;
            var norm = NormalizeRelative(rel);
            var full = Path.Combine(librarySessionPath, norm);
            if (File.Exists(full))
                kept.Add(norm);
        }

        return kept;
    }

    public static string NormalizeRelative(string relative) =>
        relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    private static void CollectTreeDiffs(
        string activeRoot,
        string libraryRoot,
        string relativePrefix,
        List<string> results)
    {
        if (!Directory.Exists(activeRoot))
            return;

        foreach (var activeFile in Directory.EnumerateFiles(activeRoot, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(activeFile);
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith('~'))
                continue;

            var relUnderRoot = Path.GetRelativePath(activeRoot, activeFile);
            if (relUnderRoot.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(p => GraphSyncPolicy.ShouldSkipFolder(p)))
                continue;

            var libraryFile = Path.Combine(libraryRoot, relUnderRoot);
            if (!NeedsUpload(activeFile, libraryFile))
                continue;

            results.Add(Path.Combine(relativePrefix, relUnderRoot));
        }
    }

    /// <summary>True when Active differs from Library (missing, size, or content).</summary>
    public static bool NeedsUpload(string activeFile, string libraryFile)
    {
        if (!File.Exists(activeFile))
            return false;
        if (!File.Exists(libraryFile))
            return true;

        var a = new FileInfo(activeFile);
        var b = new FileInfo(libraryFile);
        if (a.Length != b.Length)
            return true;

        // Same size + Active clearly older than Library (2s skew) → unchanged.
        // Do NOT treat "Active slightly newer" as unchanged without a byte compare —
        // RunScore can rewrite same-length files within that window and we would
        // skip Finish upload (Active≈Library trap after a failed cloud Finish).
        if (a.LastWriteTimeUtc < b.LastWriteTimeUtc.AddSeconds(-2))
            return false;

        // Same size and Active same-or-newer — compare bytes (mtime bumps with no edit
        // still return false; real same-size content edits return true).
        return !FilesEqual(activeFile, libraryFile);
    }

    private static bool FilesEqual(string pathA, string pathB)
    {
        try
        {
            using var fa = File.OpenRead(pathA);
            using var fb = File.OpenRead(pathB);
            if (fa.Length != fb.Length)
                return false;
            const int bufSize = 81920;
            var ba = new byte[bufSize];
            var bb = new byte[bufSize];
            while (true)
            {
                var ra = fa.Read(ba, 0, bufSize);
                var rb = fb.Read(bb, 0, bufSize);
                if (ra != rb)
                    return false;
                if (ra == 0)
                    return true;
                if (!ba.AsSpan(0, ra).SequenceEqual(bb.AsSpan(0, rb)))
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }
}
