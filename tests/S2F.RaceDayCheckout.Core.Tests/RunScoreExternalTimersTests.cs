using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class RunScoreExternalTimersTests
{
    [Fact]
    public void CreateDefaults_IncludesCompanyExternalTimers()
    {
        var profile = RunScoreSopProfile.CreateDefaults();

        Assert.Equal("MYLAPS_BIBTAG", profile.Strings["Timer_1"]);
        Assert.Equal(3097, profile.Ints["COM_1"]);
        Assert.True(profile.Bools["TCP_1"]);
        Assert.True(profile.Bools["ETS_1"]);
        Assert.Equal("*", profile.Strings["Lane_1"]);

        Assert.Equal("CHRONOTRACK", profile.Strings["Timer_2"]);
        Assert.Equal(61611, profile.Ints["COM_2"]);
        Assert.True(profile.Bools["TCP_2"]);
        Assert.False(profile.Bools["ETS_2"]);

        Assert.Equal("IPICO_SPORT", profile.Strings["Timer_3"]);
        Assert.Equal(10000, profile.Ints["COM_3"]);
        Assert.Equal("192.168.8.199", profile.Strings["TCP server address_3"]);
        Assert.Equal("1", profile.Strings["Lane_3"]);
        Assert.False(profile.Bools["ETS_3"]);

        Assert.Equal("TIMEMACH", profile.Strings["Timer_8"]);
        Assert.Equal(0, profile.Ints["COM_8"]);
        Assert.False(profile.Bools["TCP_8"]);
    }

    [Fact]
    public void EnsureExternalTimers_WritesRegistryRows()
    {
        var store = new MemoryRunScorePreferenceStore();
        var sopPath = Path.Combine(Path.GetTempPath(), "s2f-sop-timers-" + Guid.NewGuid().ToString("N") + ".json");
        var nativePath = Path.Combine(Path.GetTempPath(), "s2f-native-timers-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var sopStore = new RunScoreSopStore(sopPath);
            sopStore.Save(RunScoreSopProfile.CreateDefaults());
            var svc = new RunScorePreferencesService(store, sopStore, new NativeRunScorePrefsStore(nativePath));

            var result = svc.EnsureExternalTimers();
            Assert.True(result.Success);
            Assert.True(result.UpdatedCount > 0);

            Assert.Equal("MYLAPS_BIBTAG", store.TryGetString("Timer_1"));
            Assert.Equal(3097, store.TryGetInt("COM_1"));
            Assert.True(store.TryGetBool("TCP_1"));
            Assert.True(store.TryGetBool("ETS_1"));
            Assert.Equal("CHRONOTRACK", store.TryGetString("Timer_2"));
            Assert.Equal("IPICO_SPORT", store.TryGetString("Timer_3"));
            Assert.Equal("192.168.8.199", store.TryGetString("TCP server address_3"));

            var again = svc.EnsureExternalTimers();
            Assert.True(again.Success);
            Assert.Equal(0, again.UpdatedCount);
            Assert.True(again.AlreadyMatchedCount > 0);
        }
        finally
        {
            try { File.Delete(sopPath); } catch { /* ignore */ }
            try { File.Delete(nativePath); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void MergeMissing_FillsOlderSopWithoutTimers()
    {
        var profile = new RunScoreSopProfile
        {
            Bools = new Dictionary<string, bool>(StringComparer.Ordinal) { ["Auto login"] = true },
            Ints = new Dictionary<string, int>(StringComparer.Ordinal) { ["TCP server port"] = 51760 },
            Strings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RunScoreSopProfile.ExternalEditorName] = RunScoreSopProfile.NotepadPlusPlusExePath
            }
        };

        Assert.True(RunScoreExternalTimers.MergeMissingCompanyDefaults(profile));
        Assert.Equal("MYLAPS_BIBTAG", profile.Strings["Timer_1"]);
        Assert.False(RunScoreExternalTimers.MergeMissingCompanyDefaults(profile));
    }

    [Theory]
    [InlineData("Timer_1", true)]
    [InlineData("COM_3", true)]
    [InlineData("TCP server address_3", true)]
    [InlineData("ETS_2", true)]
    [InlineData("ADJUST_DAY_1", true)]
    [InlineData("Timer", false)]
    [InlineData("TCP server port", false)]
    [InlineData("External Editor", false)]
    public void IsExternalTimerPrefName_MatchesSuffixedRows(string name, bool expected) =>
        Assert.Equal(expected, RunScoreExternalTimers.IsExternalTimerPrefName(name));
}
