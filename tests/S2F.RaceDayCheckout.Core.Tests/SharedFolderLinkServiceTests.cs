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
}
