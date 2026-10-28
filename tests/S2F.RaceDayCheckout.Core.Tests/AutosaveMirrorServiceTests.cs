using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class AutosaveMirrorServiceTests
{
    [Fact]
    public async Task Pause_SkipsMirrorTicks()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-pause-" + Guid.NewGuid().ToString("N"));
        var activeStorePath = Path.Combine(root, "active.json");
        var backup = Path.Combine(root, "Backup", "Event");
        var autosave = Path.Combine(root, "autosave");
        var activePath = Path.Combine(root, "Active", "Event");
        Directory.CreateDirectory(backup);
        Directory.CreateDirectory(Path.Combine(activePath, PathService.RunScoreFolderName));
        File.WriteAllText(Path.Combine(backup, "ENTRIES.DTA"), "live");

        var store = new ActiveCheckoutStore(activeStorePath);
        store.Save(new Models.ActiveCheckout
        {
            Office = "Test",
            ParentEvent = "Event",
            SessionName = "Event",
            RelativePath = "Event",
            LibrarySessionPath = root,
            ActivePath = activePath,
            BackupPath = backup,
            AutosavePath = autosave,
            DbsPath = Path.Combine(root, "DB's"),
            PrimaryEventFile = "Event.event",
            CheckedOutAt = DateTimeOffset.UtcNow
        });

        var mirror = new AutosaveMirrorService(new FileCopyService(), store);
        mirror.Pause();
        await mirror.MirrorOnceAsync();
        Assert.False(Directory.Exists(autosave), "paused mirror must not create _autosave");

        mirror.Resume();
        await mirror.MirrorOnceAsync();
        Assert.True(File.Exists(Path.Combine(autosave, "ENTRIES.DTA")));
    }

    [Fact]
    public async Task MirrorOnce_CopiesBackupOnly_NotActiveRunScore()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-overlay-" + Guid.NewGuid().ToString("N"));
        try
        {
            var activeStorePath = Path.Combine(root, "active.json");
            var backup = Path.Combine(root, "Backup", "Event");
            var autosave = Path.Combine(root, "Library", "Event", "RUNSCORE", "_autosave");
            var activePath = Path.Combine(root, "Active", "Event");
            var activeRs = Path.Combine(activePath, PathService.RunScoreFolderName);
            Directory.CreateDirectory(backup);
            Directory.CreateDirectory(activeRs);

            File.WriteAllText(Path.Combine(backup, "ENTRIES.DTA"), "from-backup");
            File.WriteAllText(Path.Combine(activeRs, "race.dat"), "from-active-only");
            File.WriteAllText(Path.Combine(activeRs, "ENTRIES.DTA"), "active-entries-ignored");

            var store = new ActiveCheckoutStore(activeStorePath);
            store.Save(new Models.ActiveCheckout
            {
                Office = "Test",
                ParentEvent = "Event",
                SessionName = "Event",
                RelativePath = "Event",
                LibrarySessionPath = Path.Combine(root, "Library", "Event"),
                ActivePath = activePath,
                BackupPath = backup,
                AutosavePath = autosave,
                DbsPath = Path.Combine(root, "DB's"),
                PrimaryEventFile = "Event.event",
                CheckedOutAt = DateTimeOffset.UtcNow
            });

            var mirror = new AutosaveMirrorService(new FileCopyService(), store);
            await mirror.MirrorOnceAsync();

            Assert.Equal("from-backup", File.ReadAllText(Path.Combine(autosave, "ENTRIES.DTA")));
            Assert.False(
                File.Exists(Path.Combine(autosave, "race.dat")),
                "Active RUNSCORE must not be mirrored into _autosave (overlay-only design)");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task MirrorNewer_ReturnsAbsoluteDestPathsCopied()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-mirror-paths-" + Guid.NewGuid().ToString("N"));
        try
        {
            var src = Path.Combine(root, "src");
            var dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "ENTRIES.DTA"), "a");
            File.WriteAllText(Path.Combine(src, "Finish.FN2"), "b");

            var copy = new FileCopyService();
            var first = await copy.MirrorNewerFilesAsync(src, dst);
            Assert.Equal(2, first.Count);
            Assert.All(first, p => Assert.True(File.Exists(p)));
            Assert.Contains(first, p => p.EndsWith("ENTRIES.DTA", StringComparison.OrdinalIgnoreCase));

            var second = await copy.MirrorNewerFilesAsync(src, dst);
            Assert.Empty(second);

            File.WriteAllText(Path.Combine(src, "ENTRIES.DTA"), "changed");
            File.SetLastWriteTimeUtc(Path.Combine(src, "ENTRIES.DTA"), DateTime.UtcNow.AddMinutes(1));
            var third = await copy.MirrorNewerFilesAsync(src, dst);
            Assert.Single(third);
            Assert.EndsWith("ENTRIES.DTA", third[0], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task RecoverMisplacedBackupSiblings_DemanglesAndMovesIntoRaceFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-sib-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(root, "Backup", "Test Office", "2026 - Pumpkin Run Jackson");
        var autosave = Path.Combine(root, "autosave");
        Directory.CreateDirectory(backup);
        var parent = Path.GetDirectoryName(backup)!;

        // RunScore wrote beside the folder when registry path lacked trailing '\'.
        var entries = Path.Combine(parent, "2026 - Pumpkin Run JacksonENTRIES.DTA");
        var finish = Path.Combine(parent, "2026 - Pumpkin Run JacksonFinish.FN2");
        var status = Path.Combine(parent, "2026 - Pumpkin Run Jacksonstatus.TM4");
        File.WriteAllText(entries, "entries");
        File.WriteAllText(finish, "finish");
        File.WriteAllText(status, "status");

        await AutosaveMirrorService.RecoverMisplacedBackupSiblingsAsync(backup, autosave);

        Assert.Equal("entries", File.ReadAllText(Path.Combine(backup, "ENTRIES.DTA")));
        Assert.Equal("finish", File.ReadAllText(Path.Combine(backup, "Finish.FN2")));
        Assert.Equal("status", File.ReadAllText(Path.Combine(backup, "status.TM4")));
        Assert.Equal("entries", File.ReadAllText(Path.Combine(autosave, "ENTRIES.DTA")));
        Assert.False(File.Exists(entries), "mangled sibling should be removed after heal");
        Assert.False(File.Exists(finish));
        Assert.False(File.Exists(status));
    }

    [Theory]
    [InlineData("2026 - EventENTRIES.DTA", "2026 - Event", "ENTRIES.DTA", true)]
    [InlineData("2026 - EventFinish.FN2", "2026 - Event", "Finish.FN2", true)]
    [InlineData("2026 - Event TESTENTRIES.DTA", "2026 - Event", "", false)]
    [InlineData("2026 - Events.pdf", "2026 - Event", "", false)]
    [InlineData("2026 - Event", "2026 - Event", "", false)]
    public void TryDemangleMisplacedSibling_RejectsPrefixCollisions(
        string fileName, string folderName, string expectedDest, bool expectMatch)
    {
        var matched = AutosaveMirrorService.TryDemangleMisplacedSibling(fileName, folderName, out var dest);
        Assert.Equal(expectMatch, matched);
        if (expectMatch)
            Assert.Equal(expectedDest, dest);
    }

    [Fact]
    public async Task MirrorMisplacedEntriesSiblings_DemanglesConcatenatedNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-sib-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(root, "Backup", "Test Office", "2026 - Heavens Cradle TEST");
        var autosave = Path.Combine(root, "autosave");
        Directory.CreateDirectory(backup);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);

        var sibling = Path.Combine(
            Path.GetDirectoryName(backup)!,
            "2026 - Heavens Cradle TESTENTRIES.DTA");
        File.WriteAllText(sibling, "live");
        File.SetLastWriteTimeUtc(sibling, DateTime.UtcNow);

        await AutosaveMirrorService.MirrorMisplacedEntriesSiblingsAsync(backup, autosave);

        var dest = Path.Combine(autosave, "ENTRIES.DTA");
        Assert.True(File.Exists(dest), "expected demangled ENTRIES.DTA in autosave");
        Assert.Equal("live", File.ReadAllText(dest));
        Assert.Equal("live", File.ReadAllText(Path.Combine(backup, "ENTRIES.DTA")));
    }

    [Fact]
    public async Task MirrorOnce_AnnouncerDoesNotTouchAutosave_PullsDbsIntoActive()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-announcer-mirror-" + Guid.NewGuid().ToString("N"));
        try
        {
            var libraryEvent = Path.Combine(root, "Library", "Event");
            var backup = Path.Combine(root, "Backup", "Event");
            var autosave = Path.Combine(libraryEvent, "RUNSCORE", "_autosave");
            var activePath = Path.Combine(root, "Active", "Event");
            Directory.CreateDirectory(backup);
            Directory.CreateDirectory(activePath);
            File.WriteAllText(Path.Combine(backup, "ENTRIES.DTA"), "live");

            var store = new ActiveCheckoutStore(Path.Combine(root, "active.json"));
            store.Save(new Models.ActiveCheckout
            {
                Office = "Test",
                ParentEvent = "Event",
                SessionName = "Event",
                RelativePath = "Event",
                LibrarySessionPath = libraryEvent,
                ActivePath = activePath,
                BackupPath = backup,
                AutosavePath = autosave,
                DbsPath = Path.Combine(activePath, "DB's"),
                PrimaryEventFile = "Event-announcer.event",
                Role = Models.CheckoutRole.Announcer,
                CheckedOutAt = DateTimeOffset.UtcNow
            });

            var cloud = new AnnouncerDbsPullCloud(libraryEvent);
            var mirror = new AutosaveMirrorService(
                new FileCopyService(),
                store,
                cloud,
                () => new Models.AppSettings { LibraryRoot = Path.Combine(root, "Library") });
            await mirror.MirrorOnceAsync();

            Assert.False(Directory.Exists(autosave), "announcer must not create timing _autosave");
            Assert.True(File.Exists(Path.Combine(backup, "ENTRIES.DTA")));
            Assert.Equal(1, cloud.DownloadCalls);
            Assert.True(cloud.LastOptions?.DbsOnly == true);
            Assert.True(File.Exists(Path.Combine(activePath, "DB's", "race announcer.csv")));
            Assert.Equal("from-graph", File.ReadAllText(Path.Combine(activePath, "DB's", "race announcer.csv")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task MirrorOnce_AnnouncerSkipsWhenGraphUnavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-announcer-offline-" + Guid.NewGuid().ToString("N"));
        try
        {
            var libraryEvent = Path.Combine(root, "Library", "Event");
            var activePath = Path.Combine(root, "Active", "Event");
            Directory.CreateDirectory(libraryEvent);
            Directory.CreateDirectory(activePath);

            var store = new ActiveCheckoutStore(Path.Combine(root, "active.json"));
            store.Save(new Models.ActiveCheckout
            {
                Office = "Test",
                ParentEvent = "Event",
                SessionName = "Event",
                RelativePath = "Event",
                LibrarySessionPath = libraryEvent,
                ActivePath = activePath,
                BackupPath = Path.Combine(root, "Backup"),
                AutosavePath = Path.Combine(libraryEvent, "RUNSCORE", "_autosave"),
                DbsPath = Path.Combine(activePath, "DB's"),
                PrimaryEventFile = "Event-announcer.event",
                Role = Models.CheckoutRole.Announcer,
                CheckedOutAt = DateTimeOffset.UtcNow
            });

            var cloud = new AnnouncerDbsPullCloud(libraryEvent) { Available = false };
            var mirror = new AutosaveMirrorService(
                new FileCopyService(), store, cloud,
                () => new Models.AppSettings { LibraryRoot = Path.Combine(root, "Library") });
            await mirror.MirrorOnceAsync();
            Assert.Equal(0, cloud.DownloadCalls);
            Assert.False(Directory.Exists(Path.Combine(activePath, "DB's")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task MirrorOnce_RetriesGraphPublish_WhenPriorUploadFailed()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-retry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var libraryEvent = Path.Combine(root, "Library", "Event");
            var backup = Path.Combine(root, "Backup", "Event");
            var autosave = Path.Combine(libraryEvent, "RUNSCORE", "_autosave");
            var activePath = Path.Combine(root, "Active", "Event");
            Directory.CreateDirectory(backup);
            Directory.CreateDirectory(Path.Combine(activePath, PathService.RunScoreFolderName));
            File.WriteAllText(Path.Combine(backup, "ENTRIES.DTA"), "live");

            var store = new ActiveCheckoutStore(Path.Combine(root, "active.json"));
            store.Save(new Models.ActiveCheckout
            {
                Office = "Test",
                ParentEvent = "Event",
                SessionName = "Event",
                RelativePath = "Event",
                LibrarySessionPath = libraryEvent,
                ActivePath = activePath,
                BackupPath = backup,
                AutosavePath = autosave,
                DbsPath = Path.Combine(libraryEvent, "DB's"),
                PrimaryEventFile = "Event.event",
                CheckedOutAt = DateTimeOffset.UtcNow
            });

            var cloud = new FailThenSucceedUploadCloud();
            var mirror = new AutosaveMirrorService(
                new FileCopyService(),
                store,
                cloud,
                () => new Models.AppSettings { LibraryRoot = Path.Combine(root, "Library") });

            await mirror.MirrorOnceAsync();
            Assert.Equal(1, cloud.UploadCalls);
            Assert.True(File.Exists(Path.Combine(autosave, "ENTRIES.DTA")));

            // Second tick: MirrorNewer copies nothing (local already matches), but pending
            // Graph publish from the failed first tick must still retry.
            await mirror.MirrorOnceAsync();
            Assert.Equal(2, cloud.UploadCalls);
            Assert.True(cloud.LastUploadSucceeded);

            // Third tick: fingerprint matches — no re-upload tax on cellular.
            await mirror.MirrorOnceAsync();
            Assert.Equal(2, cloud.UploadCalls);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    private sealed class AnnouncerDbsPullCloud : ICloudLibrarySync
    {
        private readonly string _librarySessionPath;
        public bool Available { get; set; } = true;
        public int DownloadCalls { get; private set; }
        public SessionDownloadOptions? LastOptions { get; private set; }
        public bool IsAvailable => Available;

        public AnnouncerDbsPullCloud(string librarySessionPath) => _librarySessionPath = librarySessionPath;

        public Task<CloudSyncResult> DownloadSessionAsync(
            string librarySessionPath, string libraryRoot, string graphDriveRoot, IProgress<string>? status = null, CancellationToken cancellationToken = default, SessionDownloadOptions? options = null)
        {
            DownloadCalls++;
            LastOptions = options;
            var dbs = Path.Combine(_librarySessionPath, "DB's");
            Directory.CreateDirectory(dbs);
            var csv = Path.Combine(dbs, "race announcer.csv");
            File.WriteAllText(csv, "from-graph");
            File.SetLastWriteTimeUtc(csv, DateTime.UtcNow.AddMinutes(1));
            return Task.FromResult(new CloudSyncResult { Success = true, FilesTransferred = 1 });
        }

        public Task<CloudSyncResult> RefreshOfficeListAsync(
            string libraryRoot, string graphDriveRoot, IProgress<string>? status = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> RefreshOfficeSessionsAsync(
            string libraryRoot, string graphDriveRoot, string officeName, IProgress<string>? status = null, CancellationToken cancellationToken = default, string? protectSessionPath = null) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> RefreshFolderChildrenAsync(
            string libraryRoot, string graphDriveRoot, string relativeUnderLibrary, bool probeSessionLeaves = false, IProgress<string>? status = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> DownloadSessionRolesAsync(
            IReadOnlyList<string> librarySessionPaths, string libraryRoot, string graphDriveRoot, IProgress<string>? status = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<IReadOnlyList<string>> ListRemoteEventFileNamesAsync(
            string librarySessionPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<CloudSyncResult> UploadSessionRolesAsync(
            string librarySessionPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> UploadRaceStartsAsync(
            string librarySessionPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> UploadSessionAsync(
            string librarySessionPath, string libraryRoot, string graphDriveRoot, IProgress<string>? status = null, CancellationToken cancellationToken = default, IReadOnlyList<string>? onlyRelativePaths = null) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> UploadFolderAsync(
            string localFolderPath, string libraryRoot, string graphDriveRoot, IProgress<string>? status = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> UploadLocalFilesAsync(
            IReadOnlyList<string> localFilePaths, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> UploadCheckoutLockAsync(
            string librarySessionPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> DownloadCheckoutLockAsync(
            string librarySessionPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> ClearRemoteFolderAsync(
            string localFolderPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> MoveOfficeItemToPastRacesAsync(
            string libraryRoot, string graphDriveRoot, string officeName, string itemFolderName, IProgress<string>? status = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> SyncRunScoreVendorSecretsAsync(
            string graphDriveRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });
    }

    private sealed class FailThenSucceedUploadCloud : ICloudLibrarySync
    {
        public int UploadCalls { get; private set; }
        public bool LastUploadSucceeded { get; private set; }
        public bool IsAvailable => true;

        public Task<CloudSyncResult> UploadLocalFilesAsync(
            IReadOnlyList<string> localFilePaths,
            string libraryRoot,
            string graphDriveRoot,
            CancellationToken cancellationToken = default)
        {
            UploadCalls++;
            if (UploadCalls == 1)
            {
                LastUploadSucceeded = false;
                return Task.FromResult(new CloudSyncResult
                {
                    Success = false,
                    Message = "cellular timeout"
                });
            }

            LastUploadSucceeded = true;
            return Task.FromResult(new CloudSyncResult
            {
                Success = true,
                FilesTransferred = localFilePaths.Count,
                Message = "ok"
            });
        }

        public Task<CloudSyncResult> RefreshOfficeListAsync(
            string libraryRoot, string graphDriveRoot, IProgress<string>? status = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> RefreshOfficeSessionsAsync(
            string libraryRoot, string graphDriveRoot, string officeName, IProgress<string>? status = null, CancellationToken cancellationToken = default, string? protectSessionPath = null) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> RefreshFolderChildrenAsync(
            string libraryRoot, string graphDriveRoot, string relativeUnderLibrary, bool probeSessionLeaves = false, IProgress<string>? status = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> DownloadSessionAsync(
            string librarySessionPath, string libraryRoot, string graphDriveRoot, IProgress<string>? status = null, CancellationToken cancellationToken = default, SessionDownloadOptions? options = null) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> DownloadSessionRolesAsync(
            IReadOnlyList<string> librarySessionPaths, string libraryRoot, string graphDriveRoot, IProgress<string>? status = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<IReadOnlyList<string>> ListRemoteEventFileNamesAsync(
            string librarySessionPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<CloudSyncResult> UploadSessionRolesAsync(
            string librarySessionPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> UploadRaceStartsAsync(
            string librarySessionPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> UploadSessionAsync(
            string librarySessionPath, string libraryRoot, string graphDriveRoot, IProgress<string>? status = null, CancellationToken cancellationToken = default, IReadOnlyList<string>? onlyRelativePaths = null) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> UploadFolderAsync(
            string localFolderPath, string libraryRoot, string graphDriveRoot, IProgress<string>? status = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> UploadCheckoutLockAsync(
            string librarySessionPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> DownloadCheckoutLockAsync(
            string librarySessionPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> ClearRemoteFolderAsync(
            string localFolderPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> MoveOfficeItemToPastRacesAsync(
            string libraryRoot, string graphDriveRoot, string officeName, string itemFolderName, IProgress<string>? status = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> SyncRunScoreVendorSecretsAsync(
            string graphDriveRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });
    }
}
