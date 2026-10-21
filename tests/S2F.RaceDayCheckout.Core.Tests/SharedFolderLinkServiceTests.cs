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

        Assert.True(links.DeleteActiveTreeSafe(active));
        Assert.False(Directory.Exists(active));
        Assert.True(File.Exists(Path.Combine(library, "DB's", "keep.csv")));
        Assert.True(File.Exists(Path.Combine(library, "DB's", "via-link.csv")));
    }

    [Fact]
    public void DeleteActiveTreeSafe_DoesNotThrow_WhenFileLocked()
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
            // Must not throw — Close Local Copy clears active.json even when Windows holds a handle.
            var cleared = links.DeleteActiveTreeSafe(active);
            if (OperatingSystem.IsWindows())
            {
                // Windows often cannot delete/rename a folder with an exclusive lock inside.
                Assert.True(cleared || Directory.Exists(active));
            }
            else
            {
                // Unix advisory locks usually allow delete; Active path should be gone.
                Assert.True(cleared);
                Assert.False(Directory.Exists(active));
            }
        }

        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
            // locked remnant on Windows — ignore
        }
    }
}
