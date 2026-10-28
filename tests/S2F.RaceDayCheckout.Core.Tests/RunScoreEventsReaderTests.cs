using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class RunScoreEventsReaderTests
{
    [Fact]
    public void ListEventNames_ReadsEventsXmlInOrder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s2f-rs-events-" + Guid.NewGuid().ToString("N"));
        var rs = Path.Combine(dir, PathService.RunScoreFolderName);
        Directory.CreateDirectory(rs);
        try
        {
            File.WriteAllText(Path.Combine(rs, "Events.xml"), """
                <events>
                  <event name="status" distance_type="meters"></event>
                  <event name="Time" distance_type="meters"></event>
                  <event name="Finish" type="finish" distance_type="meters"></event>
                  <event name="Backup" distance_type="meters"></event>
                </events>
                """);

            var names = RunScoreEventsReader.ListEventNames(dir);
            Assert.Equal(new[] { "status", "Time", "Finish", "Backup" }, names);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ListEventNames_FallsBackToFn2WhenXmlMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s2f-rs-fn2-" + Guid.NewGuid().ToString("N"));
        var rs = Path.Combine(dir, PathService.RunScoreFolderName);
        Directory.CreateDirectory(rs);
        try
        {
            File.WriteAllBytes(Path.Combine(rs, "Finish.FN2"), Array.Empty<byte>());
            File.WriteAllBytes(Path.Combine(rs, "Backup.FN2"), Array.Empty<byte>());

            var names = RunScoreEventsReader.ListEventNames(dir);
            Assert.Contains("Backup", names);
            Assert.Contains("Finish", names);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ListEventNames_IgnoresEventsDirectoryNamedEvents()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s2f-rs-dir-" + Guid.NewGuid().ToString("N"));
        var rs = Path.Combine(dir, PathService.RunScoreFolderName);
        Directory.CreateDirectory(Path.Combine(rs, "Events"));
        try
        {
            File.WriteAllBytes(Path.Combine(rs, "Start.FN2"), Array.Empty<byte>());
            var names = RunScoreEventsReader.ListEventNames(dir);
            Assert.Equal(new[] { "Start" }, names);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ListEventNames_MissingFolder_ReturnsEmpty()
    {
        Assert.Empty(RunScoreEventsReader.ListEventNames(
            Path.Combine(Path.GetTempPath(), "s2f-missing-" + Guid.NewGuid().ToString("N"))));
    }
}
