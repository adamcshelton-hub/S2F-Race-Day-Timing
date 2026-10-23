using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

public sealed class EventBuildRequest
{
    public required string LibraryRoot { get; init; }
    public required string Office { get; init; }
    /// <summary>Full path to Template session or prior-year session leaf (contains RUNSCORE).</summary>
    public required string SourceSessionPath { get; init; }
    public required string RaceName { get; init; }
    public required string RaceDate { get; init; }
    public required string ResultsId { get; init; }
    /// <summary>
    /// When set (race group), create under <c>Office\{ParentFolderName}\{child}</c>.
    /// Parent folder name should already include the year (e.g. <c>2026 - LRM Weekend</c>).
    /// </summary>
    public string? ParentFolderName { get; init; }
}

public sealed class EventBuildResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Office { get; init; }
    public string? SessionPath { get; init; }
    /// <summary>Leaf folder name only (e.g. Sunday or 2026 - Pumpkin Run).</summary>
    public string? SessionFolderName { get; init; }
    /// <summary>Path relative to office (leaf, or Parent\leaf for a race group).</summary>
    public string? RelativePath { get; init; }
    public string? ParentFolderName { get; init; }
    public string? EntriesIniPath { get; init; }
    /// <summary>True when the copied pack has no MyLaps .event (common for Templates).</summary>
    public bool MissingEventFile { get; init; }
    public string? PrimaryEventFile { get; init; }
    /// <summary>Suggested .event file name for Save As (race name + .event).</summary>
    public string? SuggestedEventFileName { get; init; }
}

/// <summary>
/// Mid-week: copy Template or prior event into a new office session leaf,
/// then write race name / race date / %resultsid% into Entries.ini.
/// </summary>
public sealed class EventBuilderService
{
    public const string TemplatesFolderName = "Templates";
    public const string PastRacesFolderName = "Past Races";

    private readonly IFileCopyService _copy;
    private readonly PathService _paths;

    public EventBuilderService(IFileCopyService? copy = null, PathService? paths = null)
    {
        _copy = copy ?? new FileCopyService();
        _paths = paths ?? new PathService();
    }

    public static string GetTemplatesRoot(string libraryRoot) =>
        Path.Combine(libraryRoot, TemplatesFolderName);

    public static string GetPastRacesRoot(string libraryRoot) =>
        Path.Combine(libraryRoot, PastRacesFolderName);

    /// <summary>Offices that receive new current-year packs (not Past Races / Templates).</summary>
    public static IReadOnlyList<string> DestinationOffices =>
        PathService.AllowedOfficeFolders
            .Where(o => !o.Equals(PastRacesFolderName, StringComparison.OrdinalIgnoreCase)
                        && !o.Equals(TemplatesFolderName, StringComparison.OrdinalIgnoreCase))
            .ToList();

    public IReadOnlyList<string> ListTemplateSessions(string libraryRoot)
    {
        var root = GetTemplatesRoot(libraryRoot);
        if (!Directory.Exists(root))
            return Array.Empty<string>();

        return Directory.GetDirectories(root)
            .Where(d => _paths.IsSessionLeaf(d) || Directory.Exists(Path.Combine(d, PathService.RunScoreFolderName)))
            .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Past packs live at Past Races\{Office} Past Races\{Year}\{Race}\ (session leaf = RUNSCORE).
    /// Returns full paths, newest year first.
    /// </summary>
    public IReadOnlyList<(string Display, string Path)> ListPastRaceSessions(string libraryRoot, string office)
    {
        if (string.IsNullOrWhiteSpace(office))
            return Array.Empty<(string, string)>();

        var officeRoot = Path.Combine(
            GetPastRacesRoot(libraryRoot),
            PastRacesArchivePaths.OfficeBucketName(office));
        if (!Directory.Exists(officeRoot))
            return Array.Empty<(string, string)>();

        var results = new List<(string Display, string Path)>();
        foreach (var sessionPath in FindSessionLeavesUnder(officeRoot))
        {
            var relative = Path.GetRelativePath(officeRoot, sessionPath);
            results.Add((relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar), sessionPath));
        }

        return results
            .OrderByDescending(r => r.Display, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IEnumerable<string> FindSessionLeavesUnder(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(dir);
            if (string.Equals(name, PathService.RunScoreFolderName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, SharedFolderLinkService.DbsFolderName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Pre-Race", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("._", StringComparison.Ordinal))
                continue;

            if (_paths.IsSessionLeaf(dir))
                yield return dir;
        }
    }

    public async Task<EventBuildResult> BuildAsync(
        EventBuildRequest request,
        IProgress<CopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.LibraryRoot) || !Directory.Exists(request.LibraryRoot))
            return Fail("Library root not found: " + request.LibraryRoot);

        if (string.IsNullOrWhiteSpace(request.Office))
            return Fail("Office is required.");

        if (!_paths.IsAllowedOfficeFolder(request.Office))
            return Fail("Office is not in the allow-list: " + request.Office);

        if (string.IsNullOrWhiteSpace(request.SourceSessionPath) || !Directory.Exists(request.SourceSessionPath))
            return Fail("Source pack not found: " + request.SourceSessionPath);

        if (!Directory.Exists(Path.Combine(request.SourceSessionPath, PathService.RunScoreFolderName)))
            return Fail("Source pack has no RUNSCORE folder: " + request.SourceSessionPath);

        var raceName = request.RaceName?.Trim() ?? string.Empty;
        if (raceName.Length == 0)
            return Fail("Race name is required.");

        string? parentFolder = null;
        if (!string.IsNullOrWhiteSpace(request.ParentFolderName))
        {
            parentFolder = SanitizeFolderSegment(request.ParentFolderName.Trim());
            if (parentFolder.Length == 0)
                return Fail("Parent folder name is required for a race group.");
        }

        string folderName;
        try
        {
            // Under a parent: leaf is just the race name (Sunday, AM 5K, …). Flat: yyyy - RaceName.
            folderName = parentFolder is null
                ? EntriesIniService.BuildSessionFolderName(raceName, request.RaceDate)
                : SanitizeFolderSegment(raceName);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        if (folderName.Length == 0)
            return Fail("Race name is required.");

        var officePath = Path.Combine(request.LibraryRoot, request.Office);
        Directory.CreateDirectory(officePath);
        var parentPath = parentFolder is null ? officePath : Path.Combine(officePath, parentFolder);
        if (parentFolder is not null)
            Directory.CreateDirectory(parentPath);

        var dest = Path.Combine(parentPath, folderName);
        if (Directory.Exists(dest))
            return Fail("Destination already exists:\n" + dest);

        var relativePath = parentFolder is null
            ? folderName
            : Path.Combine(parentFolder, folderName);

        try
        {
            await _copy.CopyDirectoryAsync(request.SourceSessionPath, dest, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Fail("Copy failed: " + ex.Message);
        }

        if (!EntriesIniService.TryFindEntriesIni(dest, out var iniPath))
        {
            // Ensure RUNSCORE + blank Entries.ini so the three fields can still be written.
            var runScore = Path.Combine(dest, PathService.RunScoreFolderName);
            Directory.CreateDirectory(runScore);
            iniPath = Path.Combine(runScore, "Entries.ini");
            File.WriteAllText(iniPath, string.Empty);
        }

        try
        {
            EntriesIniService.Write(
                iniPath,
                new EntriesIniService.Fields(
                    raceName,
                    request.RaceDate ?? string.Empty,
                    request.ResultsId ?? string.Empty));
        }
        catch (Exception ex)
        {
            return Fail("Copied pack, but couldn't update Entries.ini:\n" + ex.Message + "\n\n" + dest);
        }

        // OneDrive / Graph omit empty folders — seed DB's so the folder lands in the cloud pack.
        EnsureDbsFolderSeeded(dest);

        // .event files are MyLaps ZIP databases — an empty stub fails with "zip file is empty".
        // Templates usually have RUNSCORE only; do not invent a fake .event.
        var primaryEvent = EventFileSelector.ChoosePrimary(EventFileSelector.ListEventFiles(dest));
        var missingEvent = primaryEvent is null;
        var suggestedEvent = VendorLauncher.SuggestEventFileNameFromRaceName(raceName);

        // Staff already know the mid-week flow — only point at where the .event should land.
        var message = missingEvent
            ? $"Save Timing & Scoring as \"{suggestedEvent}\" into the Active folder."
            : $"Created {relativePath}.";

        return new EventBuildResult
        {
            Success = true,
            Office = request.Office,
            SessionPath = dest,
            SessionFolderName = folderName,
            RelativePath = relativePath,
            ParentFolderName = parentFolder,
            EntriesIniPath = iniPath,
            MissingEventFile = missingEvent,
            PrimaryEventFile = primaryEvent,
            SuggestedEventFileName = suggestedEvent,
            Message = message
        };
    }

    private static string SanitizeFolderSegment(string name)
    {
        var s = name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '-');
        return s.Trim().TrimEnd('.', ' ');
    }

    /// <summary>
    /// Ensures <c>DB's</c> exists with at least one small file so Graph/OneDrive create the folder.
    /// Skips seeding when the Template / Past Race already copied exports into DB's.
    /// </summary>
    internal static void EnsureDbsFolderSeeded(string sessionPath)
    {
        var dbs = Path.Combine(sessionPath, SharedFolderLinkService.DbsFolderName);
        Directory.CreateDirectory(dbs);
        try
        {
            if (Directory.EnumerateFileSystemEntries(dbs).Any())
                return;
        }
        catch
        {
            // still write seed
        }

        File.WriteAllText(
            Path.Combine(dbs, "Exports land here.txt"),
            "RunScore and Timing & Scoring CSV / database exports go in this folder.\r\n");
    }

    private static EventBuildResult Fail(string message) =>
        new() { Success = false, Message = message };
}
