using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class RaceStartsStoreTests
{
    [Fact]
    public void Save_Load_RoundTripsOrderedStarts()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-race-starts-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            RaceStartsStore.Save(root,
            [
                new RaceStartEntry { Category = "10K", StartTimeOfDay = new TimeSpan(8, 30, 0) },
                new RaceStartEntry { Category = "5K", StartTimeOfDay = new TimeSpan(8, 0, 0) }
            ]);

            Assert.True(File.Exists(RaceStartsStore.GetPath(root)));
            var loaded = RaceStartsStore.Load(root);
            Assert.Equal(2, loaded.Count);
            Assert.Equal("5K", loaded[0].Category);
            Assert.Equal(new TimeSpan(8, 0, 0), loaded[0].StartTimeOfDay);
            Assert.Equal("10K", loaded[1].Category);
            Assert.Equal(Path.Combine("._checkin", "race-starts.json"), RaceStartsStore.RelativePath);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Normalize_TrimsDedupes_AndKeepsUnlabeledStarts()
    {
        var normalized = RaceStartsStore.Normalize(
        [
            new RaceStartEntry { Category = " 5K ", StartTimeOfDay = new TimeSpan(8, 0, 15) },
            new RaceStartEntry { Category = "5K", StartTimeOfDay = new TimeSpan(8, 0, 0) },
            new RaceStartEntry { Category = "  ", StartTimeOfDay = new TimeSpan(9, 0, 0) }
        ]);
        Assert.Equal(2, normalized.Count);
        Assert.Equal("5K", normalized[0].Category);
        Assert.Equal(new TimeSpan(8, 0, 0), normalized[0].StartTimeOfDay);
        Assert.Equal(string.Empty, normalized[1].Category);
        Assert.Equal(new TimeSpan(9, 0, 0), normalized[1].StartTimeOfDay);
    }

    [Fact]
    public void Save_Load_PreservesGunStartWithCentiseconds()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-race-starts-gun-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var gun = new TimeSpan(0, 8, 0, 17, 120); // 08:00:17.12
            RaceStartsStore.Save(root,
            [
                new RaceStartEntry
                {
                    Category = "5K",
                    StartTimeOfDay = new TimeSpan(8, 0, 0),
                    GunStartTimeOfDay = gun
                }
            ]);

            var loaded = RaceStartsStore.Load(root);
            Assert.Single(loaded);
            Assert.Equal(gun, loaded[0].GunStartTimeOfDay);
            Assert.Equal("08:00:17.12", RaceStartClock.FormatGunHhMmSs(loaded[0].GunStartTimeOfDay!.Value));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}
