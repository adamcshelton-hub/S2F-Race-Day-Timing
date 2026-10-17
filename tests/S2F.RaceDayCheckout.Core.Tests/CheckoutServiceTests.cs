using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class CheckoutServiceTests
{
    private sealed class NoProcesses : IProcessGuard
    {
        public IReadOnlyList<string> GetBlockingProcessNames() => Array.Empty<string>();
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
        Assert.False(Directory.Exists(Path.Combine(result.Active.ActivePath, "DB's")));
        Assert.False(File.Exists(Path.Combine(result.Active.ActivePath, "Sample Event-S2F-MEM-1.event")));
        Assert.True(File.Exists(Path.Combine(packPath, "DB's", "live.csv")));
        Assert.Contains("RUNSCORE", result.Active.ScopedPaths);
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
    public async Task Checkout_MergesNewerAutosaveIntoActiveRunScore()
    {
        var (packPath, libraryRoot) = CreateFlatPack();
        // Stale base RUNSCORE on OneDrive…
        File.WriteAllText(Path.Combine(packPath, "RUNSCORE", "ENTRIES.FRM"), "stale-base");
        File.SetLastWriteTimeUtc(Path.Combine(packPath, "RUNSCORE", "ENTRIES.FRM"), DateTime.UtcNow.AddHours(-2));
        // …but live autosave from crashed laptop is newer.
        var autosave = Path.Combine(packPath, "RUNSCORE", "_autosave");
        Directory.CreateDirectory(autosave);
        var autosaveFrm = Path.Combine(autosave, "ENTRIES.FRM");
        File.WriteAllText(autosaveFrm, "live-autosave");
        File.SetLastWriteTimeUtc(autosaveFrm, DateTime.UtcNow);

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
        var activeFrm = Path.Combine(result.Active!.ActivePath, "RUNSCORE", "ENTRIES.FRM");
        Assert.Equal("live-autosave", File.ReadAllText(activeFrm));
        Assert.Contains("autosave", result.Message, StringComparison.OrdinalIgnoreCase);
        // Backup seeded for RunScore alternate recovery
        Assert.Equal("live-autosave", File.ReadAllText(Path.Combine(result.Active.BackupPath, "ENTRIES.FRM")));
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
}
