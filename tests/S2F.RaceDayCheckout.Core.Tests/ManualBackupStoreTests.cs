using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class ManualBackupStoreTests
{
    [Fact]
    public void Append_CreateNewFile_PreservesArchive_AndResetsCursor()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-mb-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new ManualBackupStore();
            store.SaveManifest(root, new Models.ManualBackupManifest
            {
                Enabled = true,
                Locations = { "Finish" }
            });

            store.AppendTap(root, "Finish", "dev-a", "Phone A", "101");
            store.AppendTap(root, "Finish", "dev-a", "Phone A", "102");
            store.SetPlayCursor(root, "Finish", "dev-a", 1);

            var archive = store.CreateNewFile(root, "Finish", "dev-a");
            Assert.False(string.IsNullOrEmpty(archive));
            var archives = store.ListArchiveFiles(root, "Finish", "dev-a");
            Assert.Contains(archive!, archives);

            var current = store.ReadCurrentTaps(root, "Finish", "dev-a");
            Assert.Empty(current);
            var state = store.LoadOrCreateDevice(root, "Finish", "dev-a", "Phone A");
            Assert.Equal(0, state.PlayCursor);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Void_OnlyUnplayed()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-mb-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new ManualBackupStore();
            var a = store.AppendTap(root, "5K", "dev", "D", "1");
            var b = store.AppendTap(root, "5K", "dev", "D", "2");
            store.SetPlayCursor(root, "5K", "dev", 1);

            Assert.False(store.TryVoidTap(root, "5K", "dev", a.Id));
            Assert.True(store.TryVoidTap(root, "5K", "dev", b.Id));
            var taps = store.ReadCurrentTaps(root, "5K", "dev");
            Assert.False(taps[0].Voided);
            Assert.True(taps[1].Voided);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void FormatRsbi_MeetsMinLength()
    {
        var line = RunScoreOpenClient.FormatRsbi("1", "9:00:00.00", "F");
        Assert.True(RunScoreOpenClient.EnsureMinLength(line).Length >= 20);
    }

    [Fact]
    public void FormatTodLocal_HasCentiseconds()
    {
        var tod = ManualBackupStore.FormatTodLocal(
            new DateTimeOffset(2026, 10, 28, 14, 5, 6, 120, TimeSpan.Zero));
        Assert.Contains(":", tod);
        Assert.Contains(".", tod);
    }

    [Fact]
    public void OfficeCatalog_UpsertAndRemoveUnder()
    {
        var lib = Path.Combine(Path.GetTempPath(), "s2f-mb-cat-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ManualBackupStore();
            store.UpsertOfficeCatalogSession(lib, "Memphis", "RaceA", "RaceA", new[] { "Finish" }, true);
            store.UpsertOfficeCatalogSession(lib, "Memphis", "Group/RaceB", "RaceB", new[] { "5K" }, true);
            store.UpsertOfficeCatalogSession(lib, "Memphis", "Keep", "Keep", new[] { "Finish" }, true);

            store.RemoveOfficeCatalogSessionsUnder(lib, "Memphis", "Group");
            var cat = store.LoadOfficeCatalog(lib, "Memphis");
            Assert.DoesNotContain(cat.Sessions, s => s.RelativePath.StartsWith("Group", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(cat.Sessions, s => s.RelativePath == "RaceA");
            Assert.Contains(cat.Sessions, s => s.RelativePath == "Keep");

            store.RemoveOfficeCatalogSessionsUnder(lib, "Memphis", "RaceA");
            cat = store.LoadOfficeCatalog(lib, "Memphis");
            Assert.DoesNotContain(cat.Sessions, s => s.RelativePath == "RaceA");
            Assert.Single(cat.Sessions);
        }
        finally
        {
            try { Directory.Delete(lib, recursive: true); } catch { /* ignore */ }
        }
    }
}

