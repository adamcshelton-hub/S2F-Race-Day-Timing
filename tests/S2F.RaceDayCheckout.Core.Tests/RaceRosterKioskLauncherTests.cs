using System.Diagnostics;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class RaceRosterKioskLauncherTests
{
    private const string ShortcutUrl =
        "https://raceroster.com/signin?redirect=https%3A%2F%2Ftimer.raceroster.com%2Fdashboard%2Ftimers";

    [Fact]
    public void BuildArguments_MatchesShortcutFlagsAndKeepsTimerUrlLast()
    {
        var profile = @"C:\Users\Adam Shelton\AppData\Local\S2F\RaceRosterKiosk";
        var args = RaceRosterKioskLauncher.BuildArguments(profile);

        Assert.StartsWith("--kiosk --kiosk-printing ", args);
        Assert.Contains("--user-data-dir=\"" + profile + "\"", args);
        Assert.EndsWith("\"" + ShortcutUrl + "\"", args);
        Assert.Equal(ShortcutUrl, RaceRosterKioskLauncher.SignInUrl);
        Assert.True(args.LastIndexOf(ShortcutUrl, StringComparison.Ordinal)
                    > args.IndexOf("--kiosk-printing", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildArguments_TrimsTrailingSlashOnProfile()
    {
        var args = RaceRosterKioskLauncher.BuildArguments(@"D:\Kiosk\");
        Assert.Contains("--user-data-dir=\"D:\\Kiosk\"", args);
        Assert.DoesNotContain("--user-data-dir=\"D:\\Kiosk\\\"", args);
    }

    [Fact]
    public void Resolve_PrefersChromeOverEdge()
    {
        var chrome = Path.Combine("pf", "chrome.exe");
        var edge = Path.Combine("pf", "msedge.exe");
        var launcher = Launcher(
            exists: path => path == chrome || path == edge,
            candidates: () => [chrome, edge]);

        Assert.Equal(chrome, launcher.Resolve(out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Resolve_FallsBackToEdgeWhenChromeIsMissing()
    {
        var edge = Path.Combine("pf", "msedge.exe");
        var launcher = Launcher(
            exists: path => path == edge,
            candidates: () => [Path.Combine("pf", "chrome.exe"), edge]);

        Assert.Equal(edge, launcher.Resolve(out _));
    }

    [Fact]
    public void Resolve_UsesAppPathWhenInstallFoldersMiss()
    {
        var registered = @"D:\Apps\Chrome\chrome.exe";
        var launcher = Launcher(
            exists: path => path == registered,
            candidates: () => [@"C:\missing\chrome.exe"],
            appPath: name => name == "chrome.exe" ? "\"" + registered + "\"" : null);

        Assert.Equal(registered, launcher.Resolve(out _));
    }

    [Fact]
    public void Resolve_ReturnsNullWhenNothingIsInstalled()
    {
        var launcher = Launcher(
            exists: _ => false,
            candidates: () => [@"C:\missing\chrome.exe"],
            appPath: _ => null);

        Assert.Null(launcher.Resolve(out var error));
        Assert.Contains("Install Google Chrome", error);
    }

    [Fact]
    public void Launch_StartsKioskAndAsksForSignInOnFirstOpen()
    {
        var profile = Path.Combine(Path.GetTempPath(), "s2f-kiosk-" + Guid.NewGuid().ToString("N"));
        var exe = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        ProcessStartInfo? started = null;
        try
        {
            var launcher = Launcher(
                exists: path => path == exe,
                candidates: () => [exe],
                profile: () => profile,
                start: info =>
                {
                    started = info;
                    return true;
                });

            var result = launcher.Launch();

            Assert.True(result.Success);
            Assert.Contains("sign in once", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(started);
            Assert.Equal(exe, started!.FileName);
            Assert.False(started.UseShellExecute);
            Assert.Contains("--kiosk --kiosk-printing", started.Arguments);
            Assert.Contains(ShortcutUrl, started.Arguments);
            Assert.True(Directory.Exists(profile));
        }
        finally
        {
            try { Directory.Delete(profile, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Launch_ShortStatusWhenProfileAlreadyUsed()
    {
        var profile = Path.Combine(Path.GetTempPath(), "s2f-kiosk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "Local State"), "{}");
        try
        {
            var launcher = Launcher(
                exists: _ => true,
                candidates: () => ["chrome.exe"],
                profile: () => profile,
                start: _ => true);

            var result = launcher.Launch();

            Assert.True(result.Success);
            Assert.Equal("RaceRoster Kiosk opening…", result.Message);
        }
        finally
        {
            try { Directory.Delete(profile, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Launch_DoesNotStartWhenBrowserMissing()
    {
        var started = false;
        var launcher = Launcher(
            exists: _ => false,
            candidates: () => ["chrome.exe"],
            start: _ =>
            {
                started = true;
                return true;
            });

        var result = launcher.Launch();

        Assert.False(result.Success);
        Assert.False(started);
        Assert.Contains("Install Google Chrome", result.Message);
    }

    [Fact]
    public void Launch_ReportsStartFailure()
    {
        var launcher = Launcher(
            exists: _ => true,
            candidates: () => ["chrome.exe"],
            start: _ => throw new InvalidOperationException("blocked"));

        var result = launcher.Launch();

        Assert.False(result.Success);
        Assert.Contains("blocked", result.Message);
    }

    private static RaceRosterKioskLauncher Launcher(
        Func<string, bool> exists,
        Func<IEnumerable<string>> candidates,
        Func<string, string?>? appPath = null,
        Func<string>? profile = null,
        Func<ProcessStartInfo, bool>? start = null) =>
        new(
            exists: exists,
            candidates: candidates,
            appPath: appPath ?? (_ => null),
            start: start ?? (_ => true),
            profileDirectory: profile ?? (() => Path.Combine(Path.GetTempPath(), "s2f-kiosk-unused")));
}
