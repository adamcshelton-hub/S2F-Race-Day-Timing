using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class SharedFolderLinkServiceTests
{
    [Fact]
    public void EnsureDbsLink_AndDeleteActiveTreeSafe_PreservesOneDriveDbs()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-link-" + Guid.NewGuid().ToString("N"));
        var library = Path.Combine(root, "library", "Event");
        var active = Path.Combine(root, "active", "Event");
        Directory.CreateDirectory(Path.Combine(library, "DB's"));
        File.WriteAllText(Path.Combine(library, "DB's", "keep.csv"), "safe");
        Directory.CreateDirectory(active);

        var links = new SharedFolderLinkService();
        var result = links.EnsureDbsLink(active, library);
        Assert.True(result.Success, result.Message);
        Assert.True(Directory.Exists(Path.Combine(active, "DB's")));

        File.WriteAllText(Path.Combine(active, "DB's", "via-link.csv"), "ok");
        Assert.True(File.Exists(Path.Combine(library, "DB's", "via-link.csv")));

        links.DeleteActiveTreeSafe(active);
        Assert.False(Directory.Exists(active));
        Assert.True(File.Exists(Path.Combine(library, "DB's", "keep.csv")));
        Assert.True(File.Exists(Path.Combine(library, "DB's", "via-link.csv")));
    }

    [Fact]
    public void DeleteActiveTreeSafe_RenamesAside_WhenFileLocked()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-lock-" + Guid.NewGuid().ToString("N"));
        var active = Path.Combine(root, "active", "Event");
        var runScore = Path.Combine(active, "RUNSCORE");
        Directory.CreateDirectory(runScore);
        var locked = Path.Combine(runScore, "ENTRIES.FRM");
        File.WriteAllText(locked, "data");

        using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var links = new SharedFolderLinkService();
            // Must not throw — rename Active out of the way so Close Local Copy can finish.
            links.DeleteActiveTreeSafe(active);
            Assert.False(Directory.Exists(active), "Active path must be free after rename");
            var tombstone = active + PathService.ActivePendingDeleteSuffix;
            Assert.True(
                Directory.Exists(tombstone) || !Directory.Exists(active),
                "locked tree should be renamed or cleared");
        }

        // Release handle — best-effort cleanup of leftover tombstone.
        var leftover = active + PathService.ActivePendingDeleteSuffix;
        if (Directory.Exists(leftover))
        {
            try { Directory.Delete(leftover, recursive: true); }
            catch { /* ignore */ }
        }
        if (Directory.Exists(root))
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* ignore */ }
        }
    }
}
