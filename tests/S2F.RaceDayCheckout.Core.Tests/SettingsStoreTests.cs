using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void AdminPin_IsAlwaysFixedCompanyPin()
    {
        Assert.Equal("7690", AppSettings.DefaultAdminPin);
        Assert.Equal("7690", new AppSettings().AdminPin);
    }

    [Fact]
    public void Save_DoesNotPersistAdminPin()
    {
        var path = Path.Combine(Path.GetTempPath(), "s2f-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings { LibraryRoot = @"C:\OneDrive" });
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("AdminPin", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("7690", json);
            Assert.Equal("7690", store.Load().AdminPin);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Load_IgnoresAdminPinInJson()
    {
        var path = Path.Combine(Path.GetTempPath(), "s2f-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"LibraryRoot":"C:\\OneDrive","AdminPin":"1234"}""");
            var settings = new SettingsStore(path).Load();
            Assert.Equal("7690", settings.AdminPin);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }
}
