using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class CheckoutServiceTests
{
    private sealed class NoProcesses : IProcessGuard
    {
        public IReadOnlyList<string> GetBlockingProcessNames() => Array.Empty<string>();
        public IReadOnlyList<string> GetRunScoreProcessNames() => Array.Empty<string>();
    }

    private sealed class RunScoreRunning : IProcessGuard
    {
        public IReadOnlyList<string> GetBlockingProcessNames() => new[] { "RSServer" };
        public IReadOnlyList<string> GetRunScoreProcessNames() => new[] { "RSServer" };
    }

    private sealed class InstantSync : IOneDriveSyncMonitor
    {
        public Task<SyncWaitResult> WaitUntilSettledAsync(
            string path, TimeSpan timeout, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SyncWaitResult { Success = true, Message = "ok" });
    }

    private sealed class FakeMachine : IMachineIdentity
    {
        public string MachineId => "TESTMACHINE";
        public string MachineName => "TestPC";
        public string WindowsUser => "tester";
    }

    private static (string Pack, string LibraryRoot) CreateFlatPack()
    {
        var libraryRoot = Path.Combine(Path.GetTempPath(), "s2f-co-" + Guid.NewGuid().ToString("N"));
        var pack = Path.Combine(libraryRoot, "Memphis", "2026 - Sample Event");
        Directory.CreateDirectory(Path.Combine(pack, "RUNSCORE"));
        Directory.CreateDirectory(Path.Combine(pack, "DB's"));
        Directory.CreateDirectory(Path.Combine(pack, "Pre-Race"));
        File.WriteAllText(Path.Combine(pack, "RUNSCORE", "ENTRIES.FRM"), "data");
        File.WriteAllText(Path.Combine(pack, "Sample Event.event"), "event");
        File.WriteAllText(Path.Combine(pack, "Sample Event-S2F-MEM-1.event"), "announcer");
        File.WriteAllText(Path.Combine(pack, "DB's", "live.csv"), "csv");
        File.WriteAllText(Path.Combine(pack, "Pre-Race", "notes.txt"), "note");
        return (pack, libraryRoot);
    }

    private static EventPackInfo PackInfo(string packPath, string office, string relative)
    {
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var eventFiles = EventFileSelector.ListEventFiles(packPath);
        return new EventPackInfo
        {
            Office = office,
            ParentEvent = parts[0],
            SessionName = parts[^1],
            RelativePath = relative,
            Path = packPath,
            EventFiles = eventFiles,
            PrimaryEventFile = EventFileSelector.ChoosePrimary(eventFiles)
        };
    }

    [Fact]
    public async Task Checkout_WithoutEvent_CopiesRunScoreOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-lib-" + Guid.NewGuid().ToString("N"));
        var pack = Path.Combine(root, "Test Office", "2026 - No Event");
        Directory.CreateDirectory(Path.Combine(pack, "RUNSCORE"));
        Directory.CreateDirectory(Path.Combine(pack, "DB's"));
        File.WriteAllText(Path.Combine(pack, "RUNSCORE", "ENTRIES.FRM"), "data");
        var raceDay = Path.Combine(Path.GetTempPath(), "s2f-rd-" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings { LibraryRoot = root, RaceDayRoot = raceDay, DisplayName = "Adam" };
        var paths = new PathService();
        var service = new CheckoutService(
            paths,
            new CheckoutLockService(paths, new FakeMachine()),
            new FileCopyService(),
            new NoProcesses(),
            new ActiveCheckoutStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")),
            new InstantSync());

        var result = await service.CheckoutAsync(new CheckoutRequest
        {
            Pack = PackInfo(pack, "Test Office", "2026 - No Event"),
            Settings = settings,
            HolderName = "Adam",
            PrimaryEventFileOverride = ""
        });

        Assert.True(result.Success, result.Message);
        Assert.Equal("", result.Active!.PrimaryEventFile);
        Assert.True(File.Exists(Path.Combine(result.Active.ActivePath, "RUNSCORE", "ENTRIES.FRM")));
        Assert.Empty(Directory.GetFiles(result.Active.ActivePath, "*.event"));
    }

    [Fact]
    public async Task Checkin_DiscoversSaveAsEvent_CopiesToLibrary()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-lib-" + Guid.NewGuid().ToString("N"));
        var pack = Path.Combine(root, "Test Office", "2026 - No Event");
        Directory.CreateDirectory(Path.Combine(pack, "RUNSCORE"));
        Directory.CreateDirectory(Path.Combine(pack, "DB's"));
        File.WriteAllText(Path.Combine(pack, "RUNSCORE", "ENTRIES.FRM"), "data");
        var raceDay = Path.Combine(Path.GetTempPath(), "s2f-rd-" + Guid.NewGuid().ToString("N"));
        var activeStorePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var settings = new AppSettings { LibraryRoot = root, RaceDayRoot = raceDay, DisplayName = "Adam" };
        var paths = new PathService();
        var service = new CheckoutService(
            paths,
            new CheckoutLockService(paths, new FakeMachine()),
            new FileCopyService(),
            new NoProcesses(),
            new ActiveCheckoutStore(activeStorePath),
            new InstantSync());

        var checkout = await service.CheckoutAsync(new CheckoutRequest
        {
            Pack = PackInfo(pack, "Test Office", "2026 - No Event"),
            Settings = settings,
            HolderName = "Adam",
            PrimaryEventFileOverride = ""
        });
        Assert.True(checkout.Success, checkout.Message);
        Assert.Equal("", checkout.Active!.PrimaryEventFile);

        // Staff Save As'd a real .event into Active mid-checkout.
        File.WriteAllText(Path.Combine(checkout.Active.ActivePath, "No Event.event"), "mylaps-zip");

        var checkin = await service.CheckinAsync(settings, "Adam", waitForOneDrive: true);
        Assert.True(checkin.Success, checkin.Message);
        Assert.True(File.Exists(Path.Combine(pack, "No Event.event")));
        Assert.True(File.Exists(Path.Combine(pack, "RUNSCORE", "ENTRIES.FRM")));
    }

    [Fact]
    public async Task Checkout_OnlyCopiesScoped_LeavesSharedOnOneDrive()
    {
        var (packPath, libraryRoot) = CreateFlatPack();
        var raceDay = Path.Combine(Path.GetTempPath(), "s2f-rd-" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings { LibraryRoot = libraryRoot, RaceDayRoot = raceDay, DisplayName = "Adam" };
        var paths = new PathService();
        var service = new CheckoutService(
            paths,
            new CheckoutLockService(paths, new FakeMachine()),
            new FileCopyService(),
            new NoProcesses(),
            new ActiveCheckoutStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")),
            new InstantSync());

        var result = await service.CheckoutAsync(new CheckoutRequest
        {
            Pack = PackInfo(packPath, "Memphis", "2026 - Sample Event"),
            Settings = settings,
            HolderName = "Adam"
        });

        Assert.True(result.Success, result.Message);
        Assert.Equal("Sample Event.event", result.Active!.PrimaryEventFile);
        Assert.True(File.Exists(Path.Combine(result.Active.ActivePath, "RUNSCORE", "ENTRIES.FRM")));
        Assert.True(File.Exists(Path.Combine(result.Active.ActivePath, "Sample Event.event")));
        // Active DB's is a link to OneDrive so RunScore ~..\DB's\ exports stay on the cloud pack.
        var activeDbs = Path.Combine(result.Active.ActivePath, "DB's");
        Assert.True(Directory.Exists(activeDbs));
        Assert.True(File.GetAttributes(activeDbs).HasFlag(FileAttributes.ReparsePoint));
        Assert.False(File.Exists(Path.Combine(result.Active.ActivePath, "Sample Event-S2F-MEM-1.event")));
        Assert.True(File.Exists(Path.Combine(packPath, "DB's", "live.csv")));
        Assert.Contains("RUNSCORE", result.Active.ScopedPaths);
    }

    [Fact]
    public async Task Checkout_DbsLink_WritesLandOnOneDrive_AndCheckinDoesNotWipeThem()
    {
        var (packPath, libraryRoot) = CreateFlatPack();
        var raceDay = Path.Combine(Path.GetTempPath(), "s2f-rd-" + Guid.NewGuid().ToString("N"));
        var activeStorePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var settings = new AppSettings { LibraryRoot = libraryRoot, RaceDayRoot = raceDay, DisplayName = "Adam" };
        var paths = new PathService();
        var service = new CheckoutService(
            paths, new CheckoutLockService(paths, new FakeMachine()), new FileCopyService(),
            new NoProcesses(), new ActiveCheckoutStore(activeStorePath), new InstantSync());

        var pack = PackInfo(packPath, "Memphis", "2026 - Sample Event");
        var result = await service.CheckoutAsync(new CheckoutRequest
        {
            Pack = pack,
            Settings = settings,
            HolderName = "Adam"
        });
        Assert.True(result.Success, result.Message);

        var viaLink = Path.Combine(result.Active!.ActivePath, "DB's", "from-runscore.csv");
        File.WriteAllText(viaLink, "announcer");
        Assert.True(File.Exists(Path.Combine(packPath, "DB's", "from-runscore.csv")));
        Assert.True(File.Exists(Path.Combine(packPath, "DB's", "live.csv")));

        var checkin = await service.CheckinAsync(settings, "Adam", waitForOneDrive: true);
        Assert.True(checkin.Success, checkin.Message);
        Assert.False(Directory.Exists(result.Active.ActivePath));
        Assert.True(File.Exists(Path.Combine(packPath, "DB's", "from-runscore.csv")));
        Assert.True(File.Exists(Path.Combine(packPath, "DB's", "live.csv")));
    }

    [Fact]
    public async Task CheckoutAndCheckin_RoundTrip_DoesNotWipeDbs()
    {
        var (packPath, libraryRoot) = CreateFlatPack();
        var raceDay = Path.Combine(Path.GetTempPath(), "s2f-rd-" + Guid.NewGuid().ToString("N"));
        var activeStorePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var settings = new AppSettings { LibraryRoot = libraryRoot, RaceDayRoot = raceDay, DisplayName = "Adam" };
        var paths = new PathService();
        var locks = new CheckoutLockService(paths, new FakeMachine());
        var service = new CheckoutService(
            paths, locks, new FileCopyService(), new NoProcesses(),
            new ActiveCheckoutStore(activeStorePath), new InstantSync());

        var pack = PackInfo(packPath, "Memphis", "2026 - Sample Event");
        var result = await service.CheckoutAsync(new CheckoutRequest { Pack = pack, Settings = settings, HolderName = "Adam" });
        Assert.True(result.Success, result.Message);

        File.WriteAllText(Path.Combine(result.Active!.ActivePath, "RUNSCORE", "NEW.txt"), "local edit");
        File.WriteAllText(Path.Combine(packPath, "DB's", "announcer-wrote.csv"), "from other laptop");

        var checkin = await service.CheckinAsync(settings, "Adam", waitForOneDrive: true);
        Assert.True(checkin.Success, checkin.Message);
        Assert.True(File.Exists(Path.Combine(packPath, "RUNSCORE", "NEW.txt")));
        Assert.True(File.Exists(Path.Combine(packPath, "DB's", "live.csv")));
        Assert.True(File.Exists(Path.Combine(packPath, "DB's", "announcer-wrote.csv")));
        Assert.True(File.Exists(Path.Combine(packPath, "Sample Event-S2F-MEM-1.event")));
        Assert.Equal(LockLifecycleStatus.Released, locks.TryRead(packPath)!.Status);
    }

    [Fact]
    public async Task Checkout_WhenRunScoreAlreadyOpen_FlagsNeedsRestart()
    {
        var (packPath, libraryRoot) = CreateFlatPack();
        var raceDay = Path.Combine(Path.GetTempPath(), "s2f-rd-" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings { LibraryRoot = libraryRoot, RaceDayRoot = raceDay, DisplayName = "Adam" };
        var paths = new PathService();
        var service = new CheckoutService(
            paths, new CheckoutLockService(paths, new FakeMachine()), new FileCopyService(),
            new RunScoreRunning(), new ActiveCheckoutStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")),
            new InstantSync());

        var result = await service.CheckoutAsync(new CheckoutRequest
        {
            Pack = PackInfo(packPath, "Memphis", "2026 - Sample Event"),
            Settings = settings,
            HolderName = "Adam"
        });

        Assert.True(result.Success, result.Message);
        Assert.True(result.RunScoreNeedsRestart);
        Assert.Contains("already open", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Checkout_Normal_DoesNotMergeLeftoverAutosave()
    {
        var (packPath, libraryRoot) = CreateFlatPack();
        File.WriteAllText(Path.Combine(packPath, "RUNSCORE", "ENTRIES.FRM"), "onedrive-final");
        var autosave = Path.Combine(packPath, "RUNSCORE", "_autosave");
        Directory.CreateDirectory(autosave);
        File.WriteAllText(Path.Combine(autosave, "ENTRIES.FRM"), "stale-leftover");
        File.SetLastWriteTimeUtc(Path.Combine(autosave, "ENTRIES.FRM"), DateTime.UtcNow.AddHours(1));

        var raceDay = Path.Combine(Path.GetTempPath(), "s2f-rd-" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings { LibraryRoot = libraryRoot, RaceDayRoot = raceDay, DisplayName = "Adam" };
        var paths = new PathService();
        var service = new CheckoutService(
            paths, new CheckoutLockService(paths, new FakeMachine()), new FileCopyService(),
            new NoProcesses(), new ActiveCheckoutStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")),
            new InstantSync());

        var result = await service.CheckoutAsync(new CheckoutRequest
        {
            Pack = PackInfo(packPath, "Memphis", "2026 - Sample Event"),
            Settings = settings,
            HolderName = "Adam"
        });

        Assert.True(result.Success, result.Message);
        Assert.Equal(
            "onedrive-final",
            File.ReadAllText(Path.Combine(result.Active!.ActivePath, "RUNSCORE", "ENTRIES.FRM")));
    }

    [Fact]
    public async Task Checkout_EmergencyReclaim_MergesNewerAutosaveIntoActiveRunScore()
    {
        var (packPath, libraryRoot) = CreateFlatPack();
        File.WriteAllText(Path.Combine(packPath, "RUNSCORE", "ENTRIES.FRM"), "stale-base");
        File.SetLastWriteTimeUtc(Path.Combine(packPath, "RUNSCORE", "ENTRIES.FRM"), DateTime.UtcNow.AddHours(-2));
        var autosave = Path.Combine(packPath, "RUNSCORE", "_autosave");
        Directory.CreateDirectory(autosave);
        var autosaveFrm = Path.Combine(autosave, "ENTRIES.FRM");
        File.WriteAllText(autosaveFrm, "live-autosave");
        File.SetLastWriteTimeUtc(autosaveFrm, DateTime.UtcNow);

        var raceDay = Path.Combine(Path.GetTempPath(), "s2f-rd-" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings
        {
            LibraryRoot = libraryRoot,
            RaceDayRoot = raceDay,
            DisplayName = "Adam",
            StaleLockMinutes = 18
        };
        var paths = new PathService();
        var t0 = DateTimeOffset.Parse("2026-10-16T12:00:00Z");
        new CheckoutLockService(paths, new OtherMachine(), () => t0)
            .Acquire(packPath, "Memphis", "2026 - Sample Event", "2026 - Sample Event", "Other",
                new[] { "RUNSCORE" }, primaryEventFile: "Sample Event.event");

        // Evaluator clock is past stale window → NeedsEmergencyConfirm.
        var locks = new CheckoutLockService(paths, new FakeMachine(), () => t0.AddMinutes(30));
        var service = new CheckoutService(
            paths, locks, new FileCopyService(),
            new NoProcesses(), new ActiveCheckoutStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")),
            new InstantSync());

        var result = await service.CheckoutAsync(new CheckoutRequest
        {
            Pack = PackInfo(packPath, "Memphis", "2026 - Sample Event"),
            Settings = settings,
            HolderName = "Adam",
            ConfirmEmergencyReclaim = true,
            EmergencyReason = "Other laptop crashed"
        });

        Assert.True(result.Success, result.Message);
        var activeFrm = Path.Combine(result.Active!.ActivePath, "RUNSCORE", "ENTRIES.FRM");
        Assert.Equal("live-autosave", File.ReadAllText(activeFrm));
        Assert.Contains("autosave", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("live-autosave", File.ReadAllText(Path.Combine(result.Active.BackupPath, "ENTRIES.FRM")));
    }

    [Fact]
    public async Task Checkout_ForceTakeover_WhenLockLooksAlive()
    {
        var (packPath, libraryRoot) = CreateFlatPack();
        var raceDay = Path.Combine(Path.GetTempPath(), "s2f-rd-" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings { LibraryRoot = libraryRoot, RaceDayRoot = raceDay, DisplayName = "Adam" };
        var paths = new PathService();
        var other = new CheckoutLockService(paths, new OtherMachine());
        other.Acquire(packPath, "Memphis", "2026 - Sample Event", "2026 - Sample Event", "Other",
            new[] { "RUNSCORE" }, primaryEventFile: "Sample Event.event");

        var service = new CheckoutService(
            paths, new CheckoutLockService(paths, new FakeMachine()), new FileCopyService(),
            new NoProcesses(), new ActiveCheckoutStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")),
            new InstantSync());

        var blocked = await service.CheckoutAsync(new CheckoutRequest
        {
            Pack = PackInfo(packPath, "Memphis", "2026 - Sample Event"),
            Settings = settings,
            HolderName = "Adam"
        });
        Assert.False(blocked.Success);
        Assert.Equal(LockAcquireOutcome.BlockedAlive, blocked.Outcome);

        var forced = await service.CheckoutAsync(new CheckoutRequest
        {
            Pack = PackInfo(packPath, "Memphis", "2026 - Sample Event"),
            Settings = settings,
            HolderName = "Adam",
            ConfirmForceTakeover = true,
            EmergencyReason = "Force take-over (Admin PIN)"
        });
        Assert.True(forced.Success, forced.Message);
        Assert.Contains("Force take-over", forced.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Checkin_ClearsAutosaveFolder()
    {
        var (packPath, libraryRoot) = CreateFlatPack();
        var raceDay = Path.Combine(Path.GetTempPath(), "s2f-rd-" + Guid.NewGuid().ToString("N"));
        var activeStorePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var settings = new AppSettings { LibraryRoot = libraryRoot, RaceDayRoot = raceDay, DisplayName = "Adam" };
        var paths = new PathService();
        var service = new CheckoutService(
            paths, new CheckoutLockService(paths, new FakeMachine()), new FileCopyService(),
            new NoProcesses(), new ActiveCheckoutStore(activeStorePath), new InstantSync());

        var pack = PackInfo(packPath, "Memphis", "2026 - Sample Event");
        var result = await service.CheckoutAsync(new CheckoutRequest { Pack = pack, Settings = settings, HolderName = "Adam" });
        Assert.True(result.Success, result.Message);

        var autosave = result.Active!.AutosavePath;
        Directory.CreateDirectory(autosave);
        File.WriteAllText(Path.Combine(autosave, "ENTRIES.FRM"), "mid-race");

        var checkin = await service.CheckinAsync(settings, "Adam", waitForOneDrive: true);
        Assert.True(checkin.Success, checkin.Message);
        Assert.True(checkin.AutosaveCleared);
        Assert.True(CheckoutService.IsAutosaveCleared(autosave));
        Assert.Null(new ActiveCheckoutStore(activeStorePath).Load());
    }

    [Fact]
    public void ClearAutosaveWithRetry_RemovesReadOnlyFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-as-" + Guid.NewGuid().ToString("N"));
        var autosave = Path.Combine(root, "RUNSCORE", "_autosave");
        Directory.CreateDirectory(autosave);
        var file = Path.Combine(autosave, "ENTRIES.DTA");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        Assert.True(CheckoutService.ClearAutosaveWithRetry(autosave));
        Assert.True(CheckoutService.IsAutosaveCleared(autosave));
    }

    [Fact]
    public void IsAutosaveCleared_EmptyFolderCountsAsCleared()
    {
        var autosave = Path.Combine(Path.GetTempPath(), "s2f-as-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(autosave);
        Assert.True(CheckoutService.IsAutosaveCleared(autosave));
        try { Directory.Delete(autosave); } catch { }
    }

    private sealed class OtherMachine : IMachineIdentity
    {
        public string MachineId => "OTHERMACHINE";
        public string MachineName => "OtherPC";
        public string WindowsUser => "other";
    }

    [Fact]
    public async Task NestedSunday_SessionRelativePath()
    {
        var libraryRoot = Path.Combine(Path.GetTempPath(), "s2f-lrm-" + Guid.NewGuid().ToString("N"));
        var sunday = Path.Combine(libraryRoot, "Memphis", "2026 - LRM Weekend", "Sunday");
        Directory.CreateDirectory(Path.Combine(sunday, "RUNSCORE"));
        File.WriteAllText(Path.Combine(sunday, "RUNSCORE", "ENTRIES.FRM"), "d");
        File.WriteAllText(Path.Combine(sunday, "26 LRM Sunday.event"), "e");

        var raceDay = Path.Combine(Path.GetTempPath(), "s2f-rd-lrm-" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings { LibraryRoot = libraryRoot, RaceDayRoot = raceDay, DisplayName = "Adam" };
        var paths = new PathService();
        var service = new CheckoutService(
            paths, new CheckoutLockService(paths, new FakeMachine()), new FileCopyService(),
            new NoProcesses(), new ActiveCheckoutStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")),
            new InstantSync());

        var relative = Path.Combine("2026 - LRM Weekend", "Sunday");
        var result = await service.CheckoutAsync(new CheckoutRequest
        {
            Pack = PackInfo(sunday, "Memphis", relative),
            Settings = settings,
            HolderName = "Adam"
        });

        Assert.True(result.Success, result.Message);
        Assert.Equal("2026 - LRM Weekend / Sunday", result.Active!.DisplayName);
        Assert.Contains(Path.Combine("2026 - LRM Weekend", "Sunday"), result.Active.ActivePath);
    }

    private sealed class FailingCloudSync : ICloudLibrarySync
    {
        public bool IsAvailable => true;

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
            string librarySessionPath, string libraryRoot, string graphDriveRoot, IProgress<string>? status = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true, Message = "local" });

        public Task<CloudSyncResult> UploadSessionAsync(
            string librarySessionPath, string libraryRoot, string graphDriveRoot, IProgress<string>? status = null, CancellationToken cancellationToken = default, IReadOnlyList<string>? onlyRelativePaths = null) =>
            Task.FromResult(new CloudSyncResult { Success = false, Message = "cellular timeout" });

        public Task<CloudSyncResult> UploadFolderAsync(
            string localFolderPath, string libraryRoot, string graphDriveRoot, IProgress<string>? status = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> UploadLocalFilesAsync(
            IReadOnlyList<string> localFilePaths, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> UploadCheckoutLockAsync(
            string librarySessionPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });

        public Task<CloudSyncResult> ClearRemoteFolderAsync(
            string localFolderPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncResult { Success = true });
    }

    [Fact]
    public async Task Checkin_GraphUploadFail_RetainsActiveAndLock()
    {
        var (packPath, libraryRoot) = CreateFlatPack();
        var raceDay = Path.Combine(Path.GetTempPath(), "s2f-rd-" + Guid.NewGuid().ToString("N"));
        var activeStorePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var settings = new AppSettings { LibraryRoot = libraryRoot, RaceDayRoot = raceDay, DisplayName = "Adam" };
        var paths = new PathService();
        var locks = new CheckoutLockService(paths, new FakeMachine());
        var activeStore = new ActiveCheckoutStore(activeStorePath);
        var service = new CheckoutService(
            paths, locks, new FileCopyService(), new NoProcesses(), activeStore,
            new InstantSync(), cloud: new FailingCloudSync());

        var checkout = await service.CheckoutAsync(new CheckoutRequest
        {
            Pack = PackInfo(packPath, "Memphis", "2026 - Sample Event"),
            Settings = settings,
            HolderName = "Adam"
        });
        Assert.True(checkout.Success, checkout.Message);

        var checkin = await service.CheckinAsync(settings, "Adam", waitForOneDrive: true);
        Assert.True(checkin.Success, checkin.Message);
        Assert.True(checkin.RetainActiveForCloudRetry);
        Assert.True(checkin.AwaitingOneDriveConfirm);
        Assert.NotNull(activeStore.Load());
        Assert.NotNull(locks.TryRead(packPath));
        Assert.Equal(LockLifecycleStatus.Active, locks.TryRead(packPath)!.Status);
    }
}
