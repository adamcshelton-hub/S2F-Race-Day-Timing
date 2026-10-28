using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class RemotePcInstallProbeTests
{
    [Fact]
    public void IsRemotePcInstalledOnWindows_TrueWhenSuiteLauncherPresent()
    {
        var root86 = Path.Combine("PF86");
        var marker = Path.Combine(root86, "RemotePC", "SuiteLauncher.exe");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { marker };

        Assert.True(RemotePcInstallProbe.IsRemotePcInstalledOnWindows(
            fileExists: files.Contains,
            getFolderPath: folder => folder == Environment.SpecialFolder.ProgramFilesX86
                ? root86
                : "PF"));
    }

    [Fact]
    public void IsRemotePcInstalledOnWindows_TrueWhenHostServicePresent()
    {
        var root86 = Path.Combine("PF86");
        var marker = Path.Combine(root86, "RemotePC", "RemotePCService.exe");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { marker };

        Assert.True(RemotePcInstallProbe.IsRemotePcInstalledOnWindows(
            fileExists: files.Contains,
            getFolderPath: folder => folder == Environment.SpecialFolder.ProgramFilesX86
                ? root86
                : "PF"));
    }

    [Fact]
    public void IsRemotePcInstalledOnWindows_FalseWhenNothingInstalled()
    {
        Assert.False(RemotePcInstallProbe.IsRemotePcInstalledOnWindows(
            fileExists: _ => false,
            getFolderPath: folder => folder == Environment.SpecialFolder.ProgramFilesX86
                ? "PF86"
                : "PF"));
    }

    [Fact]
    public void WaitUntilRemotePcInstalledOnWindows_TrueWhenMarkerAppearsAfterDelay()
    {
        var root86 = "PF86";
        var marker = Path.Combine(root86, "RemotePC", "RemotePCService.exe");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clock = DateTime.UtcNow;
        var sleeps = 0;

        Assert.True(RemotePcInstallProbe.WaitUntilRemotePcInstalledOnWindows(
            timeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromSeconds(2),
            fileExists: files.Contains,
            getFolderPath: folder => folder == Environment.SpecialFolder.ProgramFilesX86
                ? root86
                : "PF",
            delay: d =>
            {
                sleeps++;
                clock += d;
                files.Add(marker);
            },
            utcNow: () => clock));

        Assert.Equal(1, sleeps);
    }

    [Fact]
    public void WaitUntilRemotePcInstalledOnWindows_TrueImmediatelyWhenAlreadyInstalled()
    {
        var root86 = "PF86";
        var marker = Path.Combine(root86, "RemotePC", "RemotePCService.exe");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { marker };
        var sleeps = 0;

        Assert.True(RemotePcInstallProbe.WaitUntilRemotePcInstalledOnWindows(
            timeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromSeconds(2),
            fileExists: files.Contains,
            getFolderPath: folder => folder == Environment.SpecialFolder.ProgramFilesX86
                ? root86
                : "PF",
            delay: _ => sleeps++,
            utcNow: () => DateTime.UtcNow));

        Assert.Equal(0, sleeps);
    }

    [Fact]
    public void WaitUntilRemotePcInstalledOnWindows_FalseWhenMarkersNeverAppear()
    {
        var clock = DateTime.UtcNow;
        var sleeps = 0;

        Assert.False(RemotePcInstallProbe.WaitUntilRemotePcInstalledOnWindows(
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromSeconds(2),
            fileExists: _ => false,
            getFolderPath: folder => folder == Environment.SpecialFolder.ProgramFilesX86
                ? "PF86"
                : "PF",
            delay: d =>
            {
                sleeps++;
                clock += d;
            },
            utcNow: () => clock));

        Assert.True(sleeps >= 2);
    }

    [Theory]
    [InlineData("Parallels Software International Inc.", true)]
    [InlineData("Parallels ARM Virtual Machine", true)]
    [InlineData("Dell Inc.", false)]
    [InlineData(null, false)]
    public void LooksLikeParallels_MatchesManufacturer(string? value, bool expected) =>
        Assert.Equal(expected, RemotePcInstallProbe.LooksLikeParallels(value));

    [Fact]
    public void IsParallelsGuest_TrueWhenToolsFolderExists()
    {
        Assert.True(RemotePcInstallProbe.IsParallelsGuest(
            directoryExists: path =>
                path.Contains("Parallels Tools", StringComparison.OrdinalIgnoreCase),
            tryReadBiosString: (_, _) => null));
    }

    [Fact]
    public void IsParallelsGuest_TrueWhenBiosSaysParallels()
    {
        Assert.True(RemotePcInstallProbe.IsParallelsGuest(
            directoryExists: _ => false,
            tryReadBiosString: (_, name) =>
                name == "SystemManufacturer" ? "Parallels Software International Inc." : null));
    }

    [Fact]
    public void IsParallelsGuest_FalseOnBareMetalMarkers()
    {
        Assert.False(RemotePcInstallProbe.IsParallelsGuest(
            directoryExists: _ => false,
            tryReadBiosString: (_, _) => "Dell Inc."));
    }
}
