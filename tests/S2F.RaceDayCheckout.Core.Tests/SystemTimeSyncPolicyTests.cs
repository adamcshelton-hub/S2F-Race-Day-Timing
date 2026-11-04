using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class SystemTimeSyncPolicyTests
{
    private static readonly DateTimeOffset NoonUtc = new(2026, 10, 27, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ShouldAttemptCorrection_WhenSkewAtOrAboveThreshold()
    {
        Assert.True(SystemTimeSyncPolicy.ShouldAttemptCorrection(SystemTimeSyncPolicy.CorrectThreshold));
        Assert.False(SystemTimeSyncPolicy.ShouldAttemptCorrection(
            SystemTimeSyncPolicy.CorrectThreshold - TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void ShouldAttemptCorrection_UnknownSkew_DoesNotElevate()
    {
        Assert.False(SystemTimeSyncPolicy.ShouldAttemptCorrection(null));
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
    public void ShouldAttemptCorrection_DoesNotSkipAfterUacDecline()
    {
        var settings = new AppSettings();
        SystemTimeSyncPolicy.MarkUacDeclined(settings, NoonUtc);
        Assert.True(SystemTimeSyncPolicy.WasUacDeclinedToday(settings, NoonUtc));
        Assert.True(SystemTimeSyncPolicy.ShouldAttemptCorrection(TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public void ClassifySkew_BandsMatchThresholds()
    {
        Assert.Equal(ClockSyncReading.Unknown, SystemTimeSyncPolicy.ClassifySkew(null));
        Assert.Equal(
            ClockSyncReading.Confirmed,
            SystemTimeSyncPolicy.ClassifySkew(SystemTimeSyncPolicy.CorrectThreshold - TimeSpan.FromMilliseconds(1)));
        Assert.Equal(ClockSyncReading.Close, SystemTimeSyncPolicy.ClassifySkew(SystemTimeSyncPolicy.CorrectThreshold));
        Assert.Equal(
            ClockSyncReading.Close,
            SystemTimeSyncPolicy.ClassifySkew(SystemTimeSyncPolicy.WarnThreshold - TimeSpan.FromMilliseconds(1)));
        Assert.Equal(ClockSyncReading.Off, SystemTimeSyncPolicy.ClassifySkew(SystemTimeSyncPolicy.WarnThreshold));
        Assert.False(SystemTimeSyncPolicy.ShouldPersistSuccess(ClockSyncReading.Unknown));
        Assert.False(SystemTimeSyncPolicy.ShouldPersistSuccess(ClockSyncReading.Off));
        Assert.True(SystemTimeSyncPolicy.ShouldPersistSuccess(ClockSyncReading.Confirmed));
        Assert.True(SystemTimeSyncPolicy.ShouldPersistSuccess(ClockSyncReading.Close));
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
        SystemTimeSyncPolicy.MarkSkewWarned(settings, NoonUtc);
        SystemTimeSyncPolicy.MarkSuccess(settings, NoonUtc);
        Assert.Null(settings.TimeSyncUacDeclinedLocalDate);
        Assert.Null(settings.TimeSyncSkewWarnedLocalDate);
        Assert.Equal(NoonUtc, settings.LastSuccessfulTimeSyncUtc);
        Assert.Equal(
            LaptopClockSyncKind.Synced,
            LaptopClockSyncStatus.Evaluate(settings, NoonUtc, NoonUtc));
    }

    [Fact]
    public void ClockSyncTaskScript_RoundTripsAndDoesNotClaimSuccessUnconditionally()
    {
        var sync = ClockSyncTaskScript.BuildSyncScript();
        var reg = ClockSyncTaskScript.BuildRegistrationScript();

        Assert.True(sync.All(c => c < 128));
        Assert.True(reg.All(c => c < 128));
        Assert.DoesNotContain("Invoke-Expression", sync, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-Expression", reg, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalAppData", sync, StringComparison.Ordinal);
        Assert.DoesNotContain("Temp", sync, StringComparison.Ordinal);

        Assert.Contains("Set-Date -Date $local", sync, StringComparison.Ordinal);
        Assert.Contains("AddSeconds($ageSec)", sync, StringComparison.Ordinal);
        Assert.Contains("WaitForExit(8000)", sync, StringComparison.Ordinal);
        Assert.Contains(ClockSyncTaskScript.StateDirectory, sync, StringComparison.Ordinal);
        Assert.Equal(0, CountLines(sync, "exit 0"));
        Assert.Contains("if ($set) { exit 0 }", sync, StringComparison.Ordinal);
        var setDate = sync.IndexOf("Set-Date -Date $local", StringComparison.Ordinal);
        var w32 = sync.IndexOf("w32tm.exe", StringComparison.Ordinal);
        var exit0 = sync.IndexOf("exit 0", StringComparison.Ordinal);
        Assert.True(setDate > 0 && w32 > setDate && exit0 > w32);

        Assert.Contains("SYSTEM", reg, StringComparison.Ordinal);
        Assert.Contains("0x1200A9;;;BU", reg, StringComparison.Ordinal);
        Assert.Contains("icacls.exe", reg, StringComparison.Ordinal);
        Assert.Contains(@"System32\WindowsPowerShell\v1.0\powershell.exe", reg, StringComparison.Ordinal);
        Assert.Contains(ClockSyncTaskScript.TaskQueryName, reg, StringComparison.Ordinal);
        Assert.Contains(ClockSyncTaskScript.TaskVersionToken, reg, StringComparison.Ordinal);
        Assert.Equal(1, CountLines(reg, "exit 0"));
        Assert.EndsWith("exit 0", reg.TrimEnd(), StringComparison.Ordinal);

        var idPattern = System.Text.RegularExpressions.Regex.Match(sync, @"-match '([^']+)'\) \{ \$id");
        var utcPattern = System.Text.RegularExpressions.Regex.Match(sync, @"-match '([^']+)'\) \{ \$utc");
        Assert.True(idPattern.Success);
        Assert.True(utcPattern.Success);
        var request = ClockSyncTaskScript.BuildRequestJson(new string('a', 32), NoonUtc);
        Assert.NotNull(request);
        Assert.Matches(idPattern.Groups[1].Value, request);
        Assert.Matches(utcPattern.Groups[1].Value, request);

        const string marker = "-EncodedCommand ";
        var start = reg.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start > 0);
        start += marker.Length;
        var end = reg.IndexOf('\'', start);
        var encoded = reg.Substring(start, end - start);
        Assert.InRange(encoded.Length, 1, 7000);
        var decoded = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.Equal(sync, decoded);
    }

    [Fact]
    public void ClockSyncTaskScript_RequestJsonMatchesTaskParser()
    {
        var id = new string('a', 32);
        var json = ClockSyncTaskScript.BuildRequestJson(id, NoonUtc);
        Assert.NotNull(json);
        Assert.Matches("\"id\"\\s*:\\s*\"([a-f0-9]{32})\"", json);
        Assert.Matches(
            "\"utc\"\\s*:\\s*\"(\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(?:\\.\\d+)?Z)\"",
            json);
        Assert.Contains("2026-10-27T17:00:00.0000000Z", json, StringComparison.Ordinal);
        Assert.Null(ClockSyncTaskScript.BuildRequestJson("A".PadRight(32, 'a'), NoonUtc));
        Assert.Null(ClockSyncTaskScript.BuildRequestJson("abc", null));

        var resultJson =
            "{\"ok\":true,\"detail\":\"set\",\"id\":\"" + id + "\",\"finishedUtc\":\"2026-10-27T17:00:01.0000000Z\"}";
        Assert.True(ClockSyncTaskScript.TryParseResult(resultJson, out var parsed));
        Assert.True(parsed.Ok);
        Assert.Equal(id, parsed.Id);
        Assert.Equal("set", parsed.Detail);
        Assert.False(ClockSyncTaskScript.TryParseResult("not-json", out _));
    }

    private static int CountLines(string script, string exactLine)
    {
        return script
            .Split('\n')
            .Count(line => string.Equals(line.Trim(), exactLine, StringComparison.Ordinal));
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
