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
    public void FormatArchiveDisplayName_TodayTimeOnly_OlderIncludesDate()
    {
        var now = new DateTimeOffset(2026, 10, 28, 18, 0, 0, TimeSpan.FromHours(-5));
        // 2026-10-28 17:00:56Z → local -5 = 1:00:56 PM same day
        var today = ManualBackupStore.FormatArchiveDisplayName("20261028T170056Z.jsonl", now);
        Assert.DoesNotContain("Oct", today, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(":", today);
        Assert.Equal(today, ManualBackupStore.FormatArchiveDisplayName("20261028T170056Z-1.jsonl", now));

        // Previous calendar day in same year
        var earlier = ManualBackupStore.FormatArchiveDisplayName("20261027T170056Z.jsonl", now);
        Assert.Contains("Oct", earlier, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(":", earlier);
        Assert.DoesNotContain("2026", earlier);

        // Prior year includes year
        var priorYear = ManualBackupStore.FormatArchiveDisplayName("20251027T170056Z.jsonl", now);
        Assert.Contains("2025", priorYear);
    }

    [Fact]
    public void CreateNewFile_EmptyCurrent_IsNoOp()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-mb-empty-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new ManualBackupStore();
            store.SaveManifest(root, new Models.ManualBackupManifest
            {
                Enabled = true,
                Locations = { "Finish" }
            });
            store.LoadOrCreateDevice(root, "Finish", "dev-a", "Phone A");
            Assert.Null(store.CreateNewFile(root, "Finish", "dev-a"));
            Assert.Empty(store.ListArchiveFiles(root, "Finish", "dev-a"));
            Assert.Equal(0, store.CreateNewFileAll(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void DeduplicateIdenticalArchives_KeepsNewestStamp()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-mb-dedupe-" + Guid.NewGuid().ToString("N"));
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
            var first = store.CreateNewFile(root, "Finish", "dev-a");
            Assert.False(string.IsNullOrEmpty(first));

            // Twin Prior with identical bytes (old dual-rotate bug).
            var dir = store.GetDeviceDir(root, "Finish", "dev-a");
            var twin = "20990101T000000Z.jsonl";
            File.Copy(Path.Combine(dir, first!), Path.Combine(dir, twin), overwrite: true);

            Assert.Equal(2, store.ListArchiveFiles(root, "Finish", "dev-a").Count);
            Assert.Equal(1, store.DeduplicateIdenticalArchives(root));
            var left = store.ListArchiveFiles(root, "Finish", "dev-a");
            Assert.Single(left);
            Assert.Equal(twin, left[0]); // newest stamp kept
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void DeleteArchiveFile_RemovesPrior_NotCurrent()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-mb-del-" + Guid.NewGuid().ToString("N"));
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
            var archive = store.CreateNewFile(root, "Finish", "dev-a");
            Assert.False(string.IsNullOrEmpty(archive));
            store.AppendTap(root, "Finish", "dev-a", "Phone A", "201");

            Assert.False(store.DeleteArchiveFile(root, "Finish", "dev-a", ManualBackupStore.CurrentFileName));
            Assert.True(store.DeleteArchiveFile(root, "Finish", "dev-a", archive!));
            Assert.Empty(store.ListArchiveFiles(root, "Finish", "dev-a"));
            Assert.Single(store.ReadCurrentTaps(root, "Finish", "dev-a"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void FormatFirstLastTapLines_SkipsVoided_AndFormatsBibTime()
    {
        var empty = ManualBackupStore.FormatFirstLastTapLines(Array.Empty<Models.ManualBackupTap>());
        Assert.Equal("First —", empty.FirstLine);
        Assert.Equal("Last —", empty.LastLine);

        var taps = new[]
        {
            new Models.ManualBackupTap
            {
                Bib = "101",
                TimestampUtc = new DateTimeOffset(2026, 10, 28, 14, 5, 6, TimeSpan.Zero),
                Voided = false
            },
            new Models.ManualBackupTap
            {
                Bib = "999",
                TimestampUtc = new DateTimeOffset(2026, 10, 28, 14, 6, 0, TimeSpan.Zero),
                Voided = true
            },
            new Models.ManualBackupTap
            {
                Bib = "145",
                TimestampUtc = new DateTimeOffset(2026, 10, 28, 14, 7, 8, TimeSpan.Zero),
                Voided = false
            },
        };
        var range = ManualBackupStore.FormatFirstLastTapLines(taps);
        Assert.StartsWith("First  ", range.FirstLine);
        Assert.StartsWith("Last  ", range.LastLine);
        Assert.DoesNotContain("#", range.FirstLine);
        Assert.DoesNotContain("#", range.LastLine);
        Assert.DoesNotContain("101", range.FirstLine);
        Assert.DoesNotContain("145", range.LastLine);
        Assert.Contains(":", range.FirstLine);
        Assert.Contains(":", range.LastLine);
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

    [Fact]
    public void MergeTreePreferLongerJsonl_KeepsLongerCurrent_AndExistingDeviceJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-mb-merge-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = Path.Combine(root, "Library");
            var active = Path.Combine(root, "Active");
            var store = new ManualBackupStore();
            store.SaveManifest(library, new Models.ManualBackupManifest
            {
                Enabled = true,
                Locations = { "Finish" }
            });
            store.AppendTap(library, "Finish", "dev-a", "Phone", "101");
            store.AppendTap(active, "Finish", "dev-a", "Phone", "101");
            store.AppendTap(active, "Finish", "dev-a", "Phone", "102");
            store.AppendTap(active, "Finish", "dev-a", "Phone", "103");
            store.SetPlayCursor(active, "Finish", "dev-a", 2);

            // Shorter Library current must not clobber Active; PlayCursor must stay.
            ManualBackupStore.MergeTreePreferLongerJsonl(library, active);
            Assert.Equal(3, store.ReadCurrentTaps(active, "Finish", "dev-a").Count);
            Assert.Equal(2, store.LoadOrCreateDevice(active, "Finish", "dev-a", "Phone").PlayCursor);

            // Longer Library current does merge in.
            store.AppendTap(library, "Finish", "dev-a", "Phone", "102");
            store.AppendTap(library, "Finish", "dev-a", "Phone", "103");
            store.AppendTap(library, "Finish", "dev-a", "Phone", "104");
            ManualBackupStore.MergeTreePreferLongerJsonl(library, active);
            Assert.Equal(4, store.ReadCurrentTaps(active, "Finish", "dev-a").Count);
            Assert.Equal(2, store.LoadOrCreateDevice(active, "Finish", "dev-a", "Phone").PlayCursor);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void CreateNewFileForDevice_SealsOnlyThatDevice()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-mb-seal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ManualBackupStore();
            store.SaveManifest(root, new Models.ManualBackupManifest
            {
                Enabled = true,
                Locations = { "Finish", "5K" }
            });
            store.AppendTap(root, "Finish", "laptop", "Laptop", "1");
            store.AppendTap(root, "5K", "laptop", "Laptop", "2");
            store.AppendTap(root, "Finish", "phone", "Phone", "9");

            Assert.Equal(2, store.CreateNewFileForDevice(root, "laptop"));
            Assert.Empty(store.ReadCurrentTaps(root, "Finish", "laptop"));
            Assert.Empty(store.ReadCurrentTaps(root, "5K", "laptop"));
            Assert.Single(store.ReadCurrentTaps(root, "Finish", "phone"));
            Assert.Single(store.ListArchiveFiles(root, "Finish", "laptop"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}

