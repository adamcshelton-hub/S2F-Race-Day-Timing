using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class RunScoreListingNamesTests
{
    [Theory]
    [InlineData("ReportTemplate.B.lst", "ReportTemplate.lst")]
    [InlineData("ManualBackup.9.B.lst", "ManualBackup.9.lst")]
    [InlineData("results.b.lst", "results.lst")]
    public void Canonical_DropsInvalidColorLetter(string name, string expected) =>
        Assert.Equal(expected, RunScoreListingNames.CanonicalListingFileName(name));

    [Theory]
    [InlineData("results.R.lst")]
    [InlineData("results.1.R.lst")]
    [InlineData("results.1.lst")]
    [InlineData("ReportTemplate.lst")]
    [InlineData("foo.bar.lst")]
    [InlineData("results.X.lst")]
    public void Canonical_LeavesRealColorPriorityAndPlainNames(string name) =>
        Assert.Null(RunScoreListingNames.CanonicalListingFileName(name));

    [Theory]
    [InlineData("results.1.R.lst", "results")]
    [InlineData("results.2.G.lst", "results")]
    [InlineData("results.X.lst", "results")]
    [InlineData("ReportTemplate.B.lst", "ReportTemplate")]
    [InlineData("ManualBackup.9.B.lst", "ManualBackup")]
    [InlineData("foo.bar.lst", "foo.bar")]
    public void Stem_IgnoresPriorityAndColor(string name, string expected) =>
        Assert.Equal(expected, RunScoreListingNames.ListingStem(name));

    [Fact]
    public void Drop_LeavesLoneInvalidFile()
    {
        using var library = new TempRunScore();
        library.Write("ReportTemplate.B.lst", "listing");

        var dropped = RunScoreListingNames.DropIdenticalInvalidDuplicates(library.Path);

        Assert.Empty(dropped);
        Assert.Equal("listing", File.ReadAllText(library.File("ReportTemplate.B.lst")));
    }

    [Fact]
    public void Drop_RemovesIdenticalInvalidDuplicate()
    {
        using var library = new TempRunScore();
        library.Write("ReportTemplate.lst", "same");
        library.Write("ReportTemplate.B.lst", "same");

        var dropped = RunScoreListingNames.DropIdenticalInvalidDuplicates(library.Path);

        Assert.Equal("same", File.ReadAllText(library.File("ReportTemplate.lst")));
        Assert.False(File.Exists(library.File("ReportTemplate.B.lst")));
        Assert.Contains(dropped, p => p.EndsWith("ReportTemplate.B.lst", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Drop_LeavesDifferentBytesAndRealColors()
    {
        using var library = new TempRunScore();
        library.Write("ReportTemplate.lst", "current");
        library.Write("ReportTemplate.B.lst", "older-edit");
        library.Write("results.R.lst", "red");
        library.Write("results.X.lst", "off");
        Directory.CreateDirectory(Path.Combine(library.Path, "_autosave"));
        library.Write(Path.Combine("_autosave", "ReportTemplate.B.lst"), "same");
        library.Write(Path.Combine("_autosave", "ReportTemplate.lst"), "same");

        var dropped = RunScoreListingNames.DropIdenticalInvalidDuplicates(library.Path);

        Assert.Empty(dropped);
        Assert.Equal("older-edit", File.ReadAllText(library.File("ReportTemplate.B.lst")));
        Assert.True(File.Exists(library.File(Path.Combine("_autosave", "ReportTemplate.B.lst"))));
        Assert.True(File.Exists(library.File("results.X.lst")));
    }

    [Fact]
    public void Plan_RenamesIdenticalInvalidLetterInPlace()
    {
        using var library = new TempRunScore();
        using var active = new TempRunScore();
        library.Write("ReportTemplate.B.lst", "listing");
        active.Write("ReportTemplate.lst", "listing");
        active.Write("results.R.lst", "stay");
        library.Write("results.R.lst", "stay");

        var plan = RunScoreListingNames.PlanFinish(active.Path, library.Path);

        var fix = Assert.Single(plan.Fixes);
        Assert.True(fix.RenameInPlace);
        Assert.EndsWith("ReportTemplate.B.lst", fix.OldRelative, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("ReportTemplate.lst", fix.NewRelative, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ReportTemplate.lst", fix.NewFileName);
    }

    [Fact]
    public void Plan_ColorAndPriorityChange_DeletesOldAfterUploadWhenBytesDiffer()
    {
        using var library = new TempRunScore();
        using var active = new TempRunScore();
        library.Write("results.1.R.lst", "old-body");
        active.Write("results.2.G.lst", "edited");

        var plan = RunScoreListingNames.PlanFinish(active.Path, library.Path);

        var fix = Assert.Single(plan.Fixes);
        Assert.False(fix.RenameInPlace);
        Assert.EndsWith("results.1.R.lst", fix.OldRelative, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("results.2.G.lst", fix.NewRelative, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_GrayIsARenameNotADeleteOfTheListing()
    {
        using var library = new TempRunScore();
        using var active = new TempRunScore();
        library.Write("ChipStartCheck.lst", "check");
        active.Write("ChipStartCheck.X.lst", "check");

        var plan = RunScoreListingNames.PlanFinish(active.Path, library.Path);

        var fix = Assert.Single(plan.Fixes);
        Assert.True(fix.RenameInPlace);
        Assert.EndsWith("ChipStartCheck.X.lst", fix.NewRelative, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrEmpty(fix.NewRelative));
    }

    [Fact]
    public void Plan_DeletedListingRemovesOldName()
    {
        using var library = new TempRunScore();
        using var active = new TempRunScore();
        library.Write("awards.R.lst", "awards");
        library.Write("results.lst", "results");
        active.Write("results.lst", "results");

        var plan = RunScoreListingNames.PlanFinish(active.Path, library.Path);

        var fix = Assert.Single(plan.Fixes);
        Assert.EndsWith("awards.R.lst", fix.OldRelative, StringComparison.OrdinalIgnoreCase);
        Assert.Null(fix.NewRelative);
    }

    [Fact]
    public void Plan_EmptyActiveDoesNotDeleteLibraryListings()
    {
        using var library = new TempRunScore();
        using var active = new TempRunScore();
        library.Write("results.R.lst", "results");
        library.Write("awards.lst", "awards");

        var plan = RunScoreListingNames.PlanFinish(active.Path, library.Path);

        Assert.True(plan.SuppressCloudDeletes);
        Assert.Empty(plan.Fixes);
        Assert.Empty(RunScoreListingNames.MergePending(
            plan,
            new[]
            {
                new RunScoreListingNames.PendingListingCloudFix { OldRelative = "RUNSCORE\\results.R.lst" }
            }));
    }

    [Fact]
    public void Plan_AmbiguousStemDoesNotDelete()
    {
        using var library = new TempRunScore();
        using var active = new TempRunScore();
        library.Write("results.R.lst", "red");
        library.Write("results.G.lst", "green");
        active.Write("results.T.lst", "teal");

        var plan = RunScoreListingNames.PlanFinish(active.Path, library.Path);

        Assert.Empty(plan.Fixes);
    }

    [Fact]
    public void Plan_DoesNotTouchAutosave()
    {
        using var library = new TempRunScore();
        using var active = new TempRunScore();
        Directory.CreateDirectory(Path.Combine(library.Path, "_autosave"));
        library.Write(Path.Combine("_autosave", "results.R.lst"), "old");
        active.Write("results.lst", "live");
        library.Write("results.lst", "live");

        var plan = RunScoreListingNames.PlanFinish(active.Path, library.Path);

        Assert.Empty(plan.Fixes);
    }

    [Fact]
    public async Task Delete_RemovesLocalLibraryCopiesAfterCloudDelete()
    {
        using var library = new TempSession();
        using var active = new TempSession();
        library.Write("results.lst", "results");
        library.Write("awards.lst", "awards");
        library.Write("splits.lst", "stay");
        active.Write("splits.lst", "stay");
        File.SetAttributes(library.File("awards.lst"), FileAttributes.ReadOnly);

        var plan = RunScoreListingNames.PlanFinish(active.RunScore, library.RunScore);
        Assert.Equal(2, plan.Fixes.Count);
        Assert.All(plan.Fixes, fix => Assert.Null(fix.NewRelative));

        var still = await RunScoreListingNames.DeleteSupersededListingsAsync(
            new ListingCloud(),
            active.Path,
            library.Path,
            library.Path,
            "",
            plan.Fixes,
            Array.Empty<string>());

        Assert.Empty(still);
        Assert.False(File.Exists(library.File("results.lst")));
        Assert.False(File.Exists(library.File("awards.lst")));
        Assert.Equal("stay", File.ReadAllText(library.File("splits.lst")));
    }

    [Fact]
    public async Task Delete_LeavesLocalFileWhenCloudDeleteFails()
    {
        using var library = new TempSession();
        using var active = new TempSession();
        library.Write("results.lst", "results");
        library.Write("splits.lst", "stay");
        active.Write("splits.lst", "stay");

        var plan = RunScoreListingNames.PlanFinish(active.RunScore, library.RunScore);
        var cloud = new ListingCloud { DeleteSucceeds = false };

        var still = await RunScoreListingNames.DeleteSupersededListingsAsync(
            cloud,
            active.Path,
            library.Path,
            library.Path,
            "",
            plan.Fixes,
            Array.Empty<string>());

        var pending = Assert.Single(still);
        Assert.EndsWith("results.lst", pending.OldRelative, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("results", File.ReadAllText(library.File("results.lst")));
        Assert.Equal(1, cloud.DeleteCalls);
    }

    [Fact]
    public async Task Rename_RemovesOldLocalNameAfterCloudRename()
    {
        using var library = new TempSession();
        using var active = new TempSession();
        library.Write("results.R.lst", "same");
        active.Write("results.G.lst", "same");

        var plan = RunScoreListingNames.PlanFinish(active.RunScore, library.RunScore);
        var fix = Assert.Single(plan.Fixes);
        Assert.True(fix.RenameInPlace);

        // Finish copies Active onto Library before the rename, so both names are on disk.
        library.Write("results.G.lst", "same");

        var pass = await RunScoreListingNames.RenameMatchingListingsAsync(
            new ListingCloud(),
            library.Path,
            library.Path,
            "",
            plan.Fixes);

        Assert.Empty(pass.StillPending);
        Assert.False(File.Exists(library.File("results.R.lst")));
        Assert.Equal("same", File.ReadAllText(library.File("results.G.lst")));
        Assert.Contains(pass.SkipUploadRelativePaths, p => p.EndsWith("results.G.lst", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class TempSession : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "s2f-lst-session-" + Guid.NewGuid().ToString("N"));

        public string RunScore => System.IO.Path.Combine(Path, "RUNSCORE");

        public TempSession() => Directory.CreateDirectory(RunScore);

        public string File(string name) => System.IO.Path.Combine(RunScore, name);

        public void Write(string name, string contents) =>
            System.IO.File.WriteAllText(File(name), contents);

        public void Dispose()
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(RunScore))
                {
                    var attrs = System.IO.File.GetAttributes(file);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                        System.IO.File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                }
            }
            catch { /* temp */ }

            try { Directory.Delete(Path, recursive: true); } catch { /* temp */ }
        }
    }

    private sealed class ListingCloud : ICloudLibrarySync
    {
        public bool DeleteSucceeds { get; init; } = true;
        public int DeleteCalls { get; private set; }
        public bool IsAvailable => true;

        public Task<CloudSyncResult> DeleteLibraryFileAsync(
            string localFilePath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            return Task.FromResult(new CloudSyncResult { Success = DeleteSucceeds });
        }

        public Task<CloudSyncResult> RenameLibraryFileAsync(
            string localFilePath, string newFileName, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> RefreshOfficeListAsync(string libraryRoot, string graphDriveRoot, IProgress<string>? status = null, CancellationToken cancellationToken = default, string? protectSessionPath = null) => Ok();
        public Task<CloudSyncResult> RefreshOfficeSessionsAsync(string libraryRoot, string graphDriveRoot, string officeName, IProgress<string>? status = null, CancellationToken cancellationToken = default, string? protectSessionPath = null) => Ok();
        public Task<CloudSyncResult> RefreshFolderChildrenAsync(string libraryRoot, string graphDriveRoot, string relativeUnderLibrary, bool probeSessionLeaves = false, IProgress<string>? status = null, CancellationToken cancellationToken = default, string? protectSessionPath = null) => Ok();
        public Task<CloudSyncResult> DownloadSessionAsync(string librarySessionPath, string libraryRoot, string graphDriveRoot, IProgress<string>? status = null, CancellationToken cancellationToken = default, SessionDownloadOptions? options = null) => Ok();
        public Task<CloudSyncResult> DownloadSessionRolesAsync(IReadOnlyList<string> librarySessionPaths, string libraryRoot, string graphDriveRoot, IProgress<string>? status = null, CancellationToken cancellationToken = default) => Ok();
        public Task<IReadOnlyList<string>> ListRemoteEventFileNamesAsync(string librarySessionPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<CloudSyncResult> UploadSessionRolesAsync(string librarySessionPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) => Ok();
        public Task<CloudSyncResult> UploadRaceStartsAsync(string librarySessionPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) => Ok();
        public Task<CloudSyncResult> UploadSessionAsync(string librarySessionPath, string libraryRoot, string graphDriveRoot, IProgress<string>? status = null, CancellationToken cancellationToken = default, IReadOnlyList<string>? onlyRelativePaths = null) => Ok();
        public Task<CloudSyncResult> UploadFolderAsync(string localFolderPath, string libraryRoot, string graphDriveRoot, IProgress<string>? status = null, CancellationToken cancellationToken = default) => Ok();
        public Task<CloudSyncResult> UploadLocalFilesAsync(IReadOnlyList<string> localFilePaths, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) => Ok();
        public Task<CloudSyncResult> UploadCheckoutLockAsync(string librarySessionPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) => Ok();
        public Task<CloudSyncResult> DownloadCheckoutLockAsync(string librarySessionPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) => Ok();
        public Task<CloudSyncResult> AppendManualBackupTapsAsync(string librarySessionPath, string libraryRoot, string graphDriveRoot, string location, string deviceId, string displayName, IReadOnlyList<ManualBackupTap> taps, CancellationToken cancellationToken = default) => Ok();
        public Task<CloudSyncResult> ClearRemoteFolderAsync(string localFolderPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) => Ok();
        public Task<CloudSyncResult> MoveOfficeItemToPastRacesAsync(string libraryRoot, string graphDriveRoot, string officeName, string itemFolderName, IProgress<string>? status = null, CancellationToken cancellationToken = default) => Ok();
        public Task<CloudSyncResult> SyncRunScoreVendorSecretsAsync(string graphDriveRoot, CancellationToken cancellationToken = default) => Ok();

        private static Task<CloudSyncResult> Ok() =>
            Task.FromResult(new CloudSyncResult { Success = true });
    }

    private sealed class TempRunScore : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "s2f-lst-" + Guid.NewGuid().ToString("N"));

        public TempRunScore() => Directory.CreateDirectory(Path);

        public string File(string relative) => System.IO.Path.Combine(Path, relative);

        public string Write(string relative, string contents)
        {
            var path = File(relative);
            var parent = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            System.IO.File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* temp */ }
        }
    }
}
