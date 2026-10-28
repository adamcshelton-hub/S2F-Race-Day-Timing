using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class SystemTimeSyncPolicyTests
{
    private static readonly DateTimeOffset NoonUtc = new(2026, 10, 27, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ShouldAttemptCorrection_WhenSkewAtOrAboveThreshold()
    {
        var settings = new AppSettings();
        Assert.True(SystemTimeSyncPolicy.ShouldAttemptCorrection(
            SystemTimeSyncPolicy.CorrectThreshold,
            settings,
            NoonUtc));
        Assert.False(SystemTimeSyncPolicy.ShouldAttemptCorrection(
            SystemTimeSyncPolicy.CorrectThreshold - TimeSpan.FromMilliseconds(1),
            settings,
            NoonUtc));
    }

    [Fact]
    public void ShouldAttemptCorrection_UnknownSkew_DoesNotElevate()
    {
        var settings = new AppSettings();
        Assert.False(SystemTimeSyncPolicy.ShouldAttemptCorrection(null, settings, NoonUtc));
    }

    [Fact]
    public void HasRecentSuccess_WithinWindow()
    {
        var settings = new AppSettings();
        Assert.False(SystemTimeSyncPolicy.HasRecentSuccess(settings, NoonUtc));
        SystemTimeSyncPolicy.MarkSuccess(settings, NoonUtc.AddHours(-1));
        Assert.True(SystemTimeSyncPolicy.HasRecentSuccess(settings, NoonUtc));
        Assert.False(SystemTimeSyncPolicy.HasRecentSuccess(settings, NoonUtc.AddHours(20)));
    }

    [Fact]
    public void ShouldAttemptCorrection_False_AfterUacDeclinedToday()
    {
        var settings = new AppSettings();
        SystemTimeSyncPolicy.MarkUacDeclined(settings, NoonUtc);
        Assert.True(SystemTimeSyncPolicy.WasUacDeclinedToday(settings, NoonUtc));
        Assert.False(SystemTimeSyncPolicy.ShouldAttemptCorrection(
            TimeSpan.FromMinutes(3),
            settings,
            NoonUtc));
    }

    [Fact]
    public void ShouldWarnAboutSkew_OncePerLocalDay()
    {
        var settings = new AppSettings();
        Assert.True(SystemTimeSyncPolicy.ShouldWarnAboutSkew(
            SystemTimeSyncPolicy.WarnThreshold,
            settings,
            NoonUtc));

        SystemTimeSyncPolicy.MarkSkewWarned(settings, NoonUtc);
        Assert.False(SystemTimeSyncPolicy.ShouldWarnAboutSkew(
            TimeSpan.FromMinutes(10),
            settings,
            NoonUtc));
    }

    [Fact]
    public void MarkSuccess_ClearsUacDecline()
    {
        var settings = new AppSettings();
        SystemTimeSyncPolicy.MarkUacDeclined(settings, NoonUtc);
        SystemTimeSyncPolicy.MarkSuccess(settings, NoonUtc);
        Assert.Null(settings.TimeSyncUacDeclinedLocalDate);
        Assert.Equal(NoonUtc, settings.LastSuccessfulTimeSyncUtc);
    }

    [Fact]
    public void BuildElevatedSyncScript_IncludesResyncAndOptionalSetDate()
    {
        var withSet = SystemTimeSyncPolicy.BuildElevatedSyncScript(NoonUtc);
        Assert.Contains("w32tm.exe /resync /force", withSet, StringComparison.Ordinal);
        Assert.Contains("Set-Date", withSet, StringComparison.Ordinal);
        Assert.Contains("2026-10-27", withSet, StringComparison.Ordinal);

        var without = SystemTimeSyncPolicy.BuildElevatedSyncScript(null);
        Assert.Contains("w32tm.exe /resync /force", without, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Date", without, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsStore_PersistsTimeSyncFields()
    {
        var path = Path.Combine(Path.GetTempPath(), "s2f-settings-time-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings
            {
                LastSuccessfulTimeSyncUtc = NoonUtc,
                TimeSyncUacDeclinedLocalDate = "2026-10-27",
                TimeSyncSkewWarnedLocalDate = "2026-10-26"
            });
            var loaded = store.Load();
            Assert.Equal(NoonUtc, loaded.LastSuccessfulTimeSyncUtc);
            Assert.Equal("2026-10-27", loaded.TimeSyncUacDeclinedLocalDate);
            Assert.Equal("2026-10-26", loaded.TimeSyncSkewWarnedLocalDate);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }
}
