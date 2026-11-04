using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class LaptopClockSyncStatusTests
{
    [Fact]
    public void ForReading_HidesStartupWhenUnderWarnThreshold()
    {
        var fine = ClockHeaderNotice.ForReading(
            ClockSyncReading.Confirmed, TimeSpan.FromMilliseconds(200), attemptedCorrection: false, manual: false);
        var close = ClockHeaderNotice.ForReading(
            ClockSyncReading.Close, TimeSpan.FromSeconds(1.4), attemptedCorrection: true, manual: false);

        Assert.Equal(ClockHeaderKind.Hidden, fine.Kind);
        Assert.Equal("", fine.Label);
        Assert.Equal(ClockHeaderKind.Hidden, close.Kind);
    }

    [Fact]
    public void ForReading_ManualUnderWarnThreshold_ShowsSyncedThenClears()
    {
        var notice = ClockHeaderNotice.ForReading(
            ClockSyncReading.Close, TimeSpan.FromSeconds(1.4), attemptedCorrection: true, manual: true);

        Assert.Equal(ClockHeaderKind.Synced, notice.Kind);
        Assert.Equal("Synced", notice.Label);
        Assert.True(notice.Transient);
    }

    [Fact]
    public void ForReading_OffBeforeCorrection_IsCheckClock()
    {
        var notice = ClockHeaderNotice.ForReading(
            ClockSyncReading.Off, TimeSpan.FromSeconds(5), attemptedCorrection: false, manual: false);

        Assert.Equal(ClockHeaderKind.CheckClock, notice.Kind);
        Assert.Equal("Check Clock", notice.Label);
        Assert.False(notice.Transient);
    }

    [Fact]
    public void ForReading_OffAfterCorrection_StaysVisible()
    {
        var notice = ClockHeaderNotice.ForReading(
            ClockSyncReading.Off, TimeSpan.FromSeconds(4), attemptedCorrection: true, manual: true);

        Assert.Equal(ClockHeaderKind.StillOff, notice.Kind);
        Assert.Equal("Still off by 4 seconds", notice.Label);
        Assert.False(notice.Transient);
    }

    [Fact]
    public void ForReading_Unknown_StaysQuietUnlessTheClickAsked()
    {
        var startup = ClockHeaderNotice.ForReading(
            ClockSyncReading.Unknown, null, attemptedCorrection: false, manual: false);
        var manual = ClockHeaderNotice.ForReading(
            ClockSyncReading.Unknown, null, attemptedCorrection: true, manual: true);

        Assert.Equal(ClockHeaderKind.Hidden, startup.Kind);
        Assert.Equal(ClockHeaderKind.Unconfirmed, manual.Kind);
        Assert.True(manual.Transient);
    }

    [Fact]
    public void FormatDuration_UsesSingularAndMinutes()
    {
        Assert.Equal("1 second", ClockHeaderNotice.FormatDuration(1.2));
        Assert.Equal("4 seconds", ClockHeaderNotice.FormatDuration(4));
        Assert.Equal("90 seconds", ClockHeaderNotice.FormatDuration(90));
        Assert.Equal("2 minutes", ClockHeaderNotice.FormatDuration(120));
        Assert.Equal("3 minutes", ClockHeaderNotice.FormatDuration(150));
    }
}
