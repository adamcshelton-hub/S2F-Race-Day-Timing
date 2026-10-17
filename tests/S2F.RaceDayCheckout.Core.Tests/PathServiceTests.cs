using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class PathServiceTests
{
    [Fact]
    public void EnsureAppMetaFolder_CreatesHiddenDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-meta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new PathService();
            var meta = paths.EnsureAppMetaFolder(root);

            Assert.True(Directory.Exists(meta));
            Assert.Equal(PathService.AppMetaFolderName, Path.GetFileName(meta));
            Assert.True((File.GetAttributes(meta) & FileAttributes.Hidden) != 0);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
