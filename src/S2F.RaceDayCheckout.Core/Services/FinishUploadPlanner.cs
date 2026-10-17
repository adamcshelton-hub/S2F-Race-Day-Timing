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

        // Same size: if Active is not newer than Library, treat as unchanged (opened RS, no edit).
        if (a.LastWriteTimeUtc <= b.LastWriteTimeUtc.AddSeconds(2))
            return false;

        // Same size, Active newer — compare bytes (RS often bumps mtime without edits).
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
