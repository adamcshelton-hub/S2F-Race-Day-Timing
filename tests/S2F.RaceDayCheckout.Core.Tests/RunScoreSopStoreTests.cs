using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class RunScoreSopStoreTests
{
    [Fact]
    public void Load_MissingFile_SeedsDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "s2f-sop-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.False(File.Exists(path));
            var store = new RunScoreSopStore(path);
            var result = store.Load();

            Assert.True(File.Exists(path));
            Assert.False(result.UsedFallback);
            Assert.Equal(1, result.Profile.AutoSaveMinutes);
            Assert.Equal(RunScoreSopProfile.BackupPathToken, result.Profile.SaveEntriesDir);
            Assert.True(result.Profile.Bools["Save all results"]);
            Assert.Contains("autoSaveMinutes", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Load_CustomJson_OverridesAutoSaveAndBool()
    {
        var path = Path.Combine(Path.GetTempPath(), "s2f-sop-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var custom = RunScoreSopProfile.CreateDefaults();
            custom.AutoSaveMinutes = 5;
            custom.Bools["Listings beep"] = true;
            var store = new RunScoreSopStore(path);
            store.Save(custom);

            var result = store.Load();
            Assert.False(result.UsedFallback);
            Assert.Equal(5, result.Profile.AutoSaveMinutes);
            Assert.True(result.Profile.Bools["Listings beep"]);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Load_InvalidJson_FallsBackToDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "s2f-sop-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{ not valid json");
            var store = new RunScoreSopStore(path);
            var result = store.Load();

            Assert.True(result.UsedFallback);
            Assert.NotNull(result.Warning);
            Assert.Equal(1, result.Profile.AutoSaveMinutes);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void EnsureFile_CreatesWhenMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), "s2f-sop-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new RunScoreSopStore(path);
            var ensured = store.EnsureFile();
            Assert.Equal(path, ensured);
            Assert.True(File.Exists(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
