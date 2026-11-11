namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// RunScore stores listing color and sort order in the file name.
/// <c>results.1.R.lst</c> is priority 1, color Red. Blue has no letter.
/// Gray (Inactive) is <c>.X</c> — the file stays; it is not a delete.
/// A color, priority, or invalid-letter change is a rename
/// (<c>ReportTemplate.B.lst</c> → <c>ReportTemplate.lst</c>,
/// <c>results.R.lst</c> → <c>results.G.lst</c>).
/// Finish uploads the new name and used to leave the old name on OneDrive,
/// so the next checkout downloaded both and Listings failed the rename.
/// The Active → Library copy also leaves the old file on disk (overlay, not a purge).
/// Checkout copies that Library file into the next Active, so a deleted listing comes back
/// even after OneDrive dropped it. The cloud delete and the local Library delete stay together.
/// </summary>
public static class RunScoreListingNames
{
    /// <summary>RSServer color letters. Blue is omitted on purpose — it has no code. X is Gray (Inactive).</summary>
    public const string ColorCodes = "RGFTOQSPMX";

    /// <summary>
    /// One old cloud listing name to rename or delete after Finish.
    /// <see cref="NewRelative"/> null means the timer deleted the listing.
    /// <see cref="RenameInPlace"/> means the bytes match, so Graph can rename the same item.
    /// </summary>
    public sealed class PendingListingCloudFix
    {
        public string OldRelative { get; set; } = "";
        public string? NewRelative { get; set; }
        public string? NewFileName { get; set; }
        public bool RenameInPlace { get; set; }
    }

    public sealed class FinishPlan
    {
        public IReadOnlyList<PendingListingCloudFix> Fixes { get; init; } = Array.Empty<PendingListingCloudFix>();

        /// <summary>Could not read one of the folders. Do not invent deletes.</summary>
        public bool ScanFailed { get; init; }

        /// <summary>
        /// Active has no listings while Library still does. That is a bad copy, not "delete every listing."
        /// </summary>
        public bool SuppressCloudDeletes { get; init; }
    }

    public sealed class RenamePass
    {
        public List<PendingListingCloudFix> StillPending { get; } = new();
        public List<string> SkipUploadRelativePaths { get; } = new();
    }

    /// <summary>
    /// Drop an invalid single-letter color code. Returns null when the name should stay.
    /// <c>ReportTemplate.B.lst</c> → <c>ReportTemplate.lst</c>.
    /// <c>ManualBackup.9.B.lst</c> → <c>ManualBackup.9.lst</c>.
    /// Real colors, including Gray <c>X</c>, stay.
    /// </summary>
    public static string? CanonicalListingFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var ext = Path.GetExtension(fileName);
        if (!ext.Equals(".lst", StringComparison.OrdinalIgnoreCase))
            return null;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var dot = stem.LastIndexOf('.');
        if (dot <= 0 || dot >= stem.Length - 1)
            return null;

        var token = stem[(dot + 1)..];
        if (token.Length != 1 || !char.IsLetter(token[0]) || IsColorCode(token[0]))
            return null;

        var canonicalStem = stem[..dot];
        if (canonicalStem.Length == 0)
            return null;

        return canonicalStem + ".lst";
    }

    public static bool IsColorCode(char letter)
    {
        var upper = char.ToUpperInvariant(letter);
        return ColorCodes.IndexOf(upper) >= 0;
    }

    /// <summary>
    /// Listing identity with the priority digit and the color letter removed.
    /// <c>results.1.R.lst</c> and <c>results.2.G.lst</c> are both <c>results</c>.
    /// </summary>
    public static string ListingStem(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrEmpty(stem))
            return "";

        var dot = stem.LastIndexOf('.');
        if (dot > 0 && dot == stem.Length - 2 && char.IsLetter(stem[dot + 1]))
        {
            stem = stem[..dot];
            dot = stem.LastIndexOf('.');
        }

        if (dot > 0 && dot == stem.Length - 2 && stem[dot + 1] is >= '1' and <= '9')
            stem = stem[..dot];

        return stem;
    }

    /// <summary>
    /// Remove an invalid-letter listing when the plain name is already in the same folder
    /// and the bytes match. A lone <c>.B.lst</c> is left for RunScore to rename.
    /// Different bytes are left alone — download time is not a winner.
    /// Returns paths relative to <paramref name="runScoreDirectory"/>.
    /// </summary>
    public static IReadOnlyList<string> DropIdenticalInvalidDuplicates(string runScoreDirectory)
    {
        var dropped = new List<string>();
        foreach (var file in ListListings(runScoreDirectory) ?? new List<ListingFile>())
        {
            var canonicalName = CanonicalListingFileName(Path.GetFileName(file.Relative));
            if (canonicalName is null)
                continue;

            var directory = Path.GetDirectoryName(file.Relative) ?? "";
            var canonicalRelative = string.IsNullOrEmpty(directory)
                ? canonicalName
                : Path.Combine(directory, canonicalName);
            var invalidPath = Path.Combine(runScoreDirectory, file.Relative);
            var canonicalPath = Path.Combine(runScoreDirectory, canonicalRelative);
            if (!File.Exists(canonicalPath) || PathsEqual(invalidPath, canonicalPath))
                continue;
            if (!FilesEqual(invalidPath, canonicalPath))
                continue;

            try
            {
                var attrs = File.GetAttributes(invalidPath);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(invalidPath, attrs & ~FileAttributes.ReadOnly);
                File.Delete(invalidPath);
                dropped.Add(file.Relative);
            }
            catch
            {
                // RunScore or a copy still has it. Next open retries.
            }
        }

        return dropped;
    }

    /// <summary>
    /// Old listing names that Active no longer has, paired with the name that replaced them.
    /// Call before Finish copies Active over Library.
    /// </summary>
    public static FinishPlan PlanFinish(string activeRunScore, string libraryRunScore)
    {
        var active = ListListings(activeRunScore);
        var library = ListListings(libraryRunScore);
        if (active is null || library is null)
            return new FinishPlan { ScanFailed = true };

        if (active.Count == 0 && library.Count > 0)
            return new FinishPlan { SuppressCloudDeletes = true };

        var activeByPath = IndexByPath(active);
        var libraryByPath = IndexByPath(library);

        var removed = library.Where(f => !activeByPath.ContainsKey(Norm(f.Relative))).ToList();
        var added = active.Where(f => !libraryByPath.ContainsKey(Norm(f.Relative))).ToList();

        var fixes = new List<PendingListingCloudFix>();
        foreach (var group in removed.Select(f => GroupKey(f))
                     .Concat(added.Select(GroupKey))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var gone = removed.Where(f => GroupKey(f).Equals(group, StringComparison.OrdinalIgnoreCase)).ToList();
            var arrived = added.Where(f => GroupKey(f).Equals(group, StringComparison.OrdinalIgnoreCase)).ToList();
            if (gone.Count == 0)
                continue;

            if (arrived.Count == 0)
            {
                foreach (var old in gone)
                {
                    fixes.Add(new PendingListingCloudFix
                    {
                        OldRelative = SessionRelative(old.Relative)
                    });
                }
                continue;
            }

            if (gone.Count != 1 || arrived.Count != 1)
                continue;

            var from = gone[0];
            var to = arrived[0];
            var sameBytes = FilesEqual(
                Path.Combine(activeRunScore, to.Relative),
                Path.Combine(libraryRunScore, from.Relative));
            fixes.Add(new PendingListingCloudFix
            {
                OldRelative = SessionRelative(from.Relative),
                NewRelative = SessionRelative(to.Relative),
                NewFileName = Path.GetFileName(to.Relative),
                RenameInPlace = sameBytes
            });
        }

        return new FinishPlan { Fixes = fixes };
    }

    public static List<PendingListingCloudFix> MergePending(
        FinishPlan plan,
        IReadOnlyList<PendingListingCloudFix>? previous)
    {
        if (plan.SuppressCloudDeletes)
            return new List<PendingListingCloudFix>();

        var map = new Dictionary<string, PendingListingCloudFix>(StringComparer.OrdinalIgnoreCase);
        if (previous is not null)
        {
            foreach (var fix in previous)
            {
                if (string.IsNullOrWhiteSpace(fix.OldRelative) || !IsSafeListingRelative(fix.OldRelative))
                    continue;
                map[Norm(fix.OldRelative)] = fix;
            }
        }

        // A failed scan must not replace a delete list we already trusted.
        if (!plan.ScanFailed)
        {
            foreach (var fix in plan.Fixes)
            {
                if (!IsSafeListingRelative(fix.OldRelative))
                    continue;
                map[Norm(fix.OldRelative)] = fix;
            }
        }

        return map.Values.ToList();
    }

    /// <summary>
    /// Rename cloud items whose bytes did not change. A successful rename is the same OneDrive
    /// file under the new name, so Finish must not also upload that path.
    /// Failures stay pending and fall through to upload-then-delete.
    /// </summary>
    public static async Task<RenamePass> RenameMatchingListingsAsync(
        ICloudLibrarySync cloud,
        string librarySessionPath,
        string libraryRoot,
        string graphDriveRoot,
        IReadOnlyList<PendingListingCloudFix> fixes,
        CancellationToken cancellationToken = default)
    {
        var pass = new RenamePass();
        foreach (var fix in fixes)
        {
            if (!fix.RenameInPlace
                || string.IsNullOrWhiteSpace(fix.NewFileName)
                || string.IsNullOrWhiteSpace(fix.NewRelative)
                || !IsSafeListingRelative(fix.OldRelative))
            {
                pass.StillPending.Add(fix);
                continue;
            }

            try
            {
                var oldLocal = Path.Combine(librarySessionPath, Norm(fix.OldRelative));
                var renamed = await cloud.RenameLibraryFileAsync(
                    oldLocal,
                    fix.NewFileName,
                    libraryRoot,
                    graphDriveRoot,
                    cancellationToken).ConfigureAwait(false);
                if (renamed.Success)
                {
                    // Same OneDrive item now has the new name. Drop the old Library file
                    // or the next checkout copies both names into Active.
                    if (!TryDeleteLocalFile(oldLocal))
                    {
                        pass.StillPending.Add(fix);
                        continue;
                    }

                    pass.SkipUploadRelativePaths.Add(Norm(fix.NewRelative));
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Fall through — upload the new name, then delete the old one.
            }

            pass.StillPending.Add(fix);
        }

        return pass;
    }

    /// <summary>
    /// Delete old cloud listing names, then the same file under the local Library session.
    /// A replacement is deleted only after that new name uploaded, or after a direct upload
    /// of the local file when this attempt's diff skipped it. A cloud miss stays pending
    /// and leaves the Library file in place for the retry.
    /// </summary>
    public static async Task<IReadOnlyList<PendingListingCloudFix>> DeleteSupersededListingsAsync(
        ICloudLibrarySync cloud,
        string activeSessionPath,
        string librarySessionPath,
        string libraryRoot,
        string graphDriveRoot,
        IReadOnlyList<PendingListingCloudFix> fixes,
        IReadOnlyCollection<string> uploadedRelativePaths,
        CancellationToken cancellationToken = default)
    {
        var uploaded = new HashSet<string>(
            uploadedRelativePaths.Select(Norm),
            StringComparer.OrdinalIgnoreCase);
        var still = new List<PendingListingCloudFix>();
        foreach (var fix in fixes)
        {
            if (string.IsNullOrWhiteSpace(fix.OldRelative) || !IsSafeListingRelative(fix.OldRelative))
                continue;

            var activeOld = Path.Combine(activeSessionPath, Norm(fix.OldRelative));
            if (File.Exists(activeOld))
                continue;

            if (!string.IsNullOrWhiteSpace(fix.NewRelative))
            {
                var newRel = Norm(fix.NewRelative);
                if (!uploaded.Contains(newRel))
                {
                    var newLocal = Path.Combine(librarySessionPath, newRel);
                    if (!File.Exists(newLocal))
                    {
                        still.Add(fix);
                        continue;
                    }

                    try
                    {
                        var put = await cloud.UploadLocalFilesAsync(
                            new[] { newLocal },
                            libraryRoot,
                            graphDriveRoot,
                            cancellationToken).ConfigureAwait(false);
                        if (!put.Success)
                        {
                            still.Add(fix);
                            continue;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        still.Add(fix);
                        continue;
                    }
                }
            }

            try
            {
                var oldLocal = Path.Combine(librarySessionPath, Norm(fix.OldRelative));
                var deleted = await cloud.DeleteLibraryFileAsync(
                    oldLocal,
                    libraryRoot,
                    graphDriveRoot,
                    cancellationToken).ConfigureAwait(false);
                if (!deleted.Success || !TryDeleteLocalFile(oldLocal))
                    still.Add(fix);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                still.Add(fix);
            }
        }

        return still;
    }

    private readonly record struct ListingFile(string Relative, string Stem);

    private static List<ListingFile>? ListListings(string runScoreDirectory)
    {
        if (string.IsNullOrWhiteSpace(runScoreDirectory) || !Directory.Exists(runScoreDirectory))
            return new List<ListingFile>();

        try
        {
            var list = new List<ListingFile>();
            foreach (var path in Directory.EnumerateFiles(runScoreDirectory, "*.lst", SearchOption.AllDirectories))
            {
                if (IsUnderSkippedFolder(runScoreDirectory, path))
                    continue;
                var relative = Path.GetRelativePath(runScoreDirectory, path);
                var stem = ListingStem(Path.GetFileName(relative));
                if (stem.Length == 0)
                    continue;
                list.Add(new ListingFile(relative, stem));
            }

            return list;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, ListingFile> IndexByPath(List<ListingFile> files)
    {
        var map = new Dictionary<string, ListingFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
            map[Norm(file.Relative)] = file;
        return map;
    }

    private static string GroupKey(ListingFile file)
    {
        var dir = Path.GetDirectoryName(file.Relative) ?? "";
        return Norm(dir) + "|" + file.Stem;
    }

    private static string SessionRelative(string relativeUnderRunScore) =>
        Norm(Path.Combine(PathService.RunScoreFolderName, relativeUnderRunScore));

    internal static bool IsSafeListingRelative(string relative)
    {
        var norm = Norm(relative);
        if (norm.Contains("..", StringComparison.Ordinal))
            return false;
        var prefix = PathService.RunScoreFolderName + Path.DirectorySeparatorChar;
        if (!norm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!norm.EndsWith(".lst", StringComparison.OrdinalIgnoreCase))
            return false;

        var parts = norm.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !parts.Any(GraphSyncPolicy.ShouldSkipFolder)
               && !parts.Any(p => p.Equals(PathService.AppMetaFolderName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnderSkippedFolder(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (GraphSyncPolicy.ShouldSkipFolder(part)
                || part.Equals(PathService.AppMetaFolderName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string Norm(string relative) => FinishUploadPlanner.NormalizeRelative(relative);

    /// <summary>
    /// Library overlay keeps files Active deleted. Remove one listing after OneDrive has dropped it.
    /// Missing is success. A locked or read-only failure stays pending so Finish retries.
    /// </summary>
    private static bool TryDeleteLocalFile(string path)
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

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static bool FilesEqual(string pathA, string pathB)
    {
        try
        {
            using var fa = File.OpenRead(pathA);
            using var fb = File.OpenRead(pathB);
            if (fa.Length != fb.Length)
                return false;

            var ba = new byte[81920];
            var bb = new byte[81920];
            while (true)
            {
                var ra = fa.Read(ba, 0, ba.Length);
                var rb = fb.Read(bb, 0, bb.Length);
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
}
