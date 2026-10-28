using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class RaceStartClockTests
{
    [Theory]
    [InlineData("8", 8, 0)]
    [InlineData("08", 8, 0)]
    [InlineData("8:00", 8, 0)]
    [InlineData("08:00", 8, 0)]
    [InlineData("830", 8, 30)]
    [InlineData("8:30", 8, 30)]
    [InlineData("08:30", 8, 30)]
    [InlineData("0830", 8, 30)]
    [InlineData("17:45", 17, 45)]
    [InlineData("0:05", 0, 5)]
    [InlineData("23:59", 23, 59)]
    [InlineData("24:00", 24, 0)]
    [InlineData("2400", 24, 0)]
    public void TryParse24h_AcceptsCommonForms(string input, int hour, int minute)
    {
        Assert.True(RaceStartClock.TryParse24h(input, out var tod));
        Assert.Equal(new TimeSpan(hour, minute, 0), tod);
    }

    [Fact]
    public void FormatRelative_24_00_IsCountdownToTonightMidnight_NotCountUpFromMorning()
    {
        // 23:10 with start 24:00 → ~50 min countdown (not +23h from 00:00 AM).
        var now = new DateTime(2026, 10, 27, 23, 10, 3, DateTimeKind.Local);
        var start = new TimeSpan(24, 0, 0);
        Assert.Equal("−0:49:57", RaceStartClock.FormatRelative(now, start));
        Assert.False(RaceStartClock.IsCountUp(now, start));
        Assert.Equal("24:00", RaceStartClock.FormatStartHhMm(start));
    }

    [Theory]
    [InlineData("8:00:17", 8, 0, 17, 0)]
    [InlineData("08:00:17.12", 8, 0, 17, 120)]
    [InlineData("08:00:17.1", 8, 0, 17, 100)]
    public void TryParseGun_AcceptsHhMmSsAndCentiseconds(
        string input, int hour, int minute, int second, int milliseconds)
    {
        Assert.True(RaceStartClock.TryParseGun(input, out var tod));
        Assert.Equal(new TimeSpan(0, hour, minute, second, milliseconds), tod);
    }

    [Fact]
    public void FormatGunHhMmSs_AlwaysIncludesCentiseconds()
    {
        Assert.Equal("08:00:17.00", RaceStartClock.FormatGunHhMmSs(new TimeSpan(8, 0, 17)));
        Assert.Equal("08:00:17.12", RaceStartClock.FormatGunHhMmSs(new TimeSpan(0, 8, 0, 17, 120)));
    }

    [Fact]
    public void FormatRelative_WithCentiseconds_TracksGunPrecision()
    {
        var gun = new TimeSpan(0, 8, 0, 0, 0);
        var now = new DateTime(2026, 10, 27, 8, 0, 1, 230, DateTimeKind.Local);
        Assert.Equal("+0:00:01.23", RaceStartClock.FormatRelative(now, gun, centiseconds: true));
        Assert.True(RaceStartClock.IsCountUp(now, gun, centiseconds: true));
    }

    [Fact]
    public void CaptureGunTimeOfDay_TruncatesToCentiseconds()
    {
        var now = new DateTime(2026, 10, 27, 8, 0, 17, 129, DateTimeKind.Local);
        Assert.Equal(new TimeSpan(0, 8, 0, 17, 120), RaceStartClock.CaptureGunTimeOfDay(now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("24:01")]
    [InlineData("24:30")]
    [InlineData("8:60")]
    [InlineData("abc")]
    [InlineData("8:00am")]
    public void TryParse24h_RejectsInvalid(string input) =>
        Assert.False(RaceStartClock.TryParse24h(input, out _));

    [Fact]
    public void FormatRelative_Countdown_Then_CountUp()
    {
        var day = new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Local);
        var start = new TimeSpan(8, 0, 0);

        var before = day.AddHours(7).AddMinutes(59).AddSeconds(1);
        Assert.Equal("−0:00:59", RaceStartClock.FormatRelative(before, start));
        Assert.False(RaceStartClock.IsCountUp(before, start));

        var after = day.AddHours(8).AddMinutes(1).AddSeconds(5);
        Assert.Equal("+0:01:05", RaceStartClock.FormatRelative(after, start));
        Assert.True(RaceStartClock.IsCountUp(after, start));
    }

    [Fact]
    public void FormatStartHhMm_PadsHours()
    {
        Assert.Equal("08:00", RaceStartClock.FormatStartHhMm(new TimeSpan(8, 0, 0)));
        Assert.Equal("17:05", RaceStartClock.FormatStartHhMm(new TimeSpan(17, 5, 0)));
    }

    [Fact]
    public void FormatRelative_MatchesWholeSecondClock_IgnoringMilliseconds()
    {
        // Header shows 22:47:56; remaining to 23:00 must be 12:04 even if Now has .700ms.
        var now = new DateTime(2026, 10, 27, 22, 47, 56, 700, DateTimeKind.Local);
        var start = new TimeSpan(23, 0, 0);
        Assert.Equal("−0:12:04", RaceStartClock.FormatRelative(now, start));
        Assert.Equal(
            RaceStartClock.FormatRelative(RaceStartClock.TruncateToSecond(now), start),
            RaceStartClock.FormatRelative(now, start));
    }
}
