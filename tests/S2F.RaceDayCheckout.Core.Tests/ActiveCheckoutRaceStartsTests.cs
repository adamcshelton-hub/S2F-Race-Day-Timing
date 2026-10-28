using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class ActiveCheckoutRaceStartsTests
{
    private static ActiveCheckout Sample() => new()
    {
        Office = "Memphis",
        ParentEvent = "Event",
        SessionName = "Event",
        RelativePath = "Event",
        LibrarySessionPath = @"C:\S2F\Library\Memphis\Event",
        ActivePath = @"C:\S2F\RaceDay\Active\Memphis\Event",
        BackupPath = @"C:\S2F\RaceDay\Backup\Memphis\Event",
        AutosavePath = @"C:\S2F\Library\Memphis\Event\RUNSCORE\_autosave",
        DbsPath = @"C:\S2F\Library\Memphis\Event\DB's",
        PrimaryEventFile = "Event.event",
        CheckedOutAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public void ActiveCheckoutStore_PersistsRaceStarts()
    {
        var path = Path.Combine(Path.GetTempPath(), "s2f-active-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new ActiveCheckoutStore(path);
            var active = Sample().WithRaceStarts(
            [
                new RaceStartEntry { Category = "5K", StartTimeOfDay = new TimeSpan(8, 0, 0) },
                new RaceStartEntry { Category = "10K", StartTimeOfDay = new TimeSpan(8, 30, 0) }
            ]);
            store.Save(active);
            var loaded = store.Load();
            Assert.NotNull(loaded);
            Assert.Equal(2, loaded!.RaceStarts.Count);
            Assert.Equal("5K", loaded.RaceStarts[0].Category);
            Assert.Equal(new TimeSpan(8, 0, 0), loaded.RaceStarts[0].StartTimeOfDay);
            Assert.Equal("10K", loaded.RaceStarts[1].Category);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void WithPendingFinishUpload_PreservesRaceStarts()
    {
        var active = Sample().WithRaceStarts(
        [
            new RaceStartEntry { Category = "5K", StartTimeOfDay = new TimeSpan(8, 0, 0) }
        ]);
        var next = active.WithPendingFinishUpload(["RUNSCORE/file.db"]);
        Assert.Single(next.RaceStarts);
        Assert.Equal("5K", next.RaceStarts[0].Category);
        Assert.NotNull(next.PendingFinishUploadRelativePaths);
    }
}
