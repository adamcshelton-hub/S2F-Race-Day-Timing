using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class LaptopClockSyncStatusTests
{
    private static readonly DateTimeOffset NoonUtc = new(2026, 10, 27, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Evaluate_Synced_WhenRecentSuccess()
    {
        var settings = new AppSettings { LastSuccessfulTimeSyncUtc = NoonUtc.AddHours(-1) };
        Assert.Equal(
            LaptopClockSyncKind.Synced,
            LaptopClockSyncStatus.Evaluate(settings, NoonUtc, NoonUtc));
        Assert.Equal(string.Empty, LaptopClockSyncStatus.WarningLabel(LaptopClockSyncKind.Synced));
        Assert.Equal("Check Clock", LaptopClockSyncStatus.WarningLabel(LaptopClockSyncKind.CheckClock));
    }

    [Fact]
    public void Evaluate_CheckClock_WhenSkewWarnedToday()
    {
        var settings = new AppSettings { LastSuccessfulTimeSyncUtc = NoonUtc.AddHours(-1) };
        SystemTimeSyncPolicy.MarkSkewWarned(settings, NoonUtc);
        Assert.Equal(
            LaptopClockSyncKind.CheckClock,
            LaptopClockSyncStatus.Evaluate(settings, NoonUtc, NoonUtc));
    }

    [Fact]
    public void Evaluate_CheckClock_WhenNeverSynced()
    {
        var settings = new AppSettings();
        Assert.Equal(
            LaptopClockSyncKind.CheckClock,
            LaptopClockSyncStatus.Evaluate(settings, NoonUtc, NoonUtc));
    }
}
