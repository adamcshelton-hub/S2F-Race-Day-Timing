using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Tests;

public class RemotePcDeployConfigTests
{
    [Fact]
    public void BuildMsiexecArguments_IncludesMandatoryIdAndSilentFlags()
    {
        var cfg = new RemotePcDeployConfig
        {
            ConfigurationId = "abc123Config",
            GroupName = "Timing Laptops",
            ShowConfirm = false,
            HideTray = false,
            ConnectPermission = 0
        };

        var args = cfg.BuildMsiexecArguments(@"C:\Temp\RemotePC.msi");
        Assert.Contains("/i \"C:\\Temp\\RemotePC.msi\" /qn", args, StringComparison.Ordinal);
        Assert.Contains("ID=abc123Config", args, StringComparison.Ordinal);
        Assert.Contains("GROUPNAME=\"Timing Laptops\"", args, StringComparison.Ordinal);
        Assert.Contains("SHOW_CONFIRM=0", args, StringComparison.Ordinal);
        Assert.Contains("HIDETRAY=0", args, StringComparison.Ordinal);
        Assert.Contains("CONNECT_PERMISSION=0", args, StringComparison.Ordinal);
        Assert.DoesNotContain("PERSONALKEY=", args, StringComparison.Ordinal);
    }

    [Fact]
    public void TryLoad_ReadsExampleShape()
    {
        var path = Path.Combine(Path.GetTempPath(), "s2f-rpc-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """
            {
              "configurationId": "test-id-xyz",
              "groupName": "Memphis",
              "showConfirm": false
            }
            """);
        try
        {
            var cfg = RemotePcDeployConfig.TryLoad(path);
            Assert.NotNull(cfg);
            Assert.Equal("test-id-xyz", cfg!.ConfigurationId);
            Assert.Equal("Memphis", cfg.GroupName);
            Assert.False(cfg.ShowConfirm);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void TryLoad_RejectsMissingConfigurationId()
    {
        var path = Path.Combine(Path.GetTempPath(), "s2f-rpc-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{ "groupName": "Timing" }""");
        try
        {
            Assert.Null(RemotePcDeployConfig.TryLoad(path));
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }
}
