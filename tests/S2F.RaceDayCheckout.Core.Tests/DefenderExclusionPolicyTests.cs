using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class DefenderExclusionPolicyTests
{
    [Fact]
    public void NeedsOffer_WhenNotConfigured()
    {
        var settings = new AppSettings { DefenderExclusionsConfigured = false };
        Assert.True(DefenderExclusionPolicy.NeedsOffer(settings));
    }

    [Fact]
    public void NeedsOffer_False_AfterConfigured()
    {
        var settings = new AppSettings();
        DefenderExclusionPolicy.MarkConfigured(settings);
        Assert.True(settings.DefenderExclusionsConfigured);
        Assert.False(DefenderExclusionPolicy.NeedsOffer(settings));
    }

    [Fact]
    public void MarkApplied_SetsDiagnosticsFlagOnly()
    {
        var settings = new AppSettings();
        DefenderExclusionPolicy.MarkApplied(settings);
        Assert.True(settings.DefenderExclusionsApplied);
        Assert.False(settings.DefenderExclusionsConfigured);
        Assert.True(DefenderExclusionPolicy.NeedsOffer(settings));
    }

    [Fact]
    public void ExclusionPaths_AreActiveAndBackupUnderRaceDayRoot()
    {
        var root = Path.Combine("C:", "RaceDay");
        var paths = DefenderExclusionPolicy.ExclusionPaths(root);
        Assert.Equal(2, paths.Count);
        Assert.Equal(Path.Combine(root, "Active"), paths[0]);
        Assert.Equal(Path.Combine(root, "Backup"), paths[1]);
    }

    [Fact]
    public void EnsureRaceDayFolders_CreatesActiveAndBackup()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-raceday-" + Guid.NewGuid().ToString("N"));
        try
        {
            DefenderExclusionPolicy.EnsureRaceDayFolders(root);
            Assert.True(Directory.Exists(Path.Combine(root, "Active")));
            Assert.True(Directory.Exists(Path.Combine(root, "Backup")));
            DefenderExclusionPolicy.EnsureRaceDayFolders(root);
            Assert.True(Directory.Exists(Path.Combine(root, "Active")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void SettingsStore_PersistsDefenderExclusionFlags()
    {
        var path = Path.Combine(Path.GetTempPath(), "s2f-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings
            {
                DefenderExclusionsConfigured = true,
                DefenderExclusionsApplied = true
            });
            var loaded = store.Load();
            Assert.True(loaded.DefenderExclusionsConfigured);
            Assert.True(loaded.DefenderExclusionsApplied);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }
}
