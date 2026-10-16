using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class OneDriveHydrationServiceTests
{
    [Fact]
    public void LooksCloudOnly_NormalLocalFile_IsFalse()
    {
        var path = Path.Combine(Path.GetTempPath(), "s2f-hydrate-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "local");
        try
        {
            Assert.False(OneDriveHydrationService.LooksCloudOnly(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task HydrateFileAsync_LocalFile_NoThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), "s2f-hydrate-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "already local");
        try
        {
            var svc = new OneDriveHydrationService();
            await svc.HydrateFileAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
