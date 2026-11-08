using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class GraphSyncPolicyTests
{
    [Theory]
    [InlineData("Logs", false)]
    [InlineData("Events", false)]
    [InlineData("_autosave", true)]
    [InlineData("RUNSCORE", false)]
    [InlineData("DB's", false)]
    public void ShouldSkipFolder_CellularNoise(string name, bool skip) =>
        Assert.Equal(skip, GraphSyncPolicy.ShouldSkipFolder(name));

    [Fact]
    public void FinishBudget_MatchesSessionDownload_ForGraphLatency()
    {
        Assert.Equal(GraphSyncPolicy.SessionDownloadBudget, GraphSyncPolicy.FinishUploadBudget);
        Assert.True(GraphSyncPolicy.FinishUploadBudget <= TimeSpan.FromMinutes(3));
        // Past Races year probes reuse download gate (parallel FolderExists).
        Assert.True(GraphSyncPolicy.DownloadParallelism >= 2);
        Assert.True(GraphSyncPolicy.DownloadParallelismFloor >= 1);
        Assert.True(GraphSyncPolicy.DownloadParallelismFloor <= GraphSyncPolicy.DownloadParallelism);
        Assert.True(GraphSyncPolicy.HttpTimeout <= TimeSpan.FromSeconds(30));
        Assert.True(GraphSyncPolicy.OfficeListBudget <= TimeSpan.FromSeconds(20));
        Assert.True(GraphSyncPolicy.OfficeSessionsBudget <= TimeSpan.FromSeconds(75));
        Assert.True(GraphSyncPolicy.SessionDownloadBudget <= TimeSpan.FromMinutes(3));
        Assert.True(GraphSyncPolicy.MidRaceMaxFileBytes < GraphSyncPolicy.FinishMaxFileBytes);
    }

    [Fact]
    public void TransferParallelism_HasThrottleFloor()
    {
        Assert.Equal(8, GraphSyncPolicy.DownloadParallelism);
        Assert.Equal(GraphSyncPolicy.DownloadParallelism, GraphSyncPolicy.UploadParallelism);
        Assert.True(GraphSyncPolicy.DownloadParallelismFloor >= 2);
        Assert.Equal(GraphSyncPolicy.DownloadParallelismFloor, GraphSyncPolicy.UploadParallelismFloor);
        Assert.True(GraphSyncPolicy.DownloadParallelismFloor < GraphSyncPolicy.DownloadParallelism);
        Assert.True(GraphSyncPolicy.DownloadMaxAttempts >= 3);
        Assert.Equal(GraphSyncPolicy.DownloadMaxAttempts, GraphSyncPolicy.UploadMaxAttempts);
    }

    [Fact]
    public void NeedsSessionFileDownload_MissingLocal_True() =>
        Assert.True(GraphSyncPolicy.NeedsSessionFileDownload(
            localExists: false,
            localLength: 0,
            localLastWriteUtc: DateTime.MinValue,
            remoteSize: 996,
            remoteLastModifiedUtc: DateTime.UtcNow));

    [Fact]
    public void NeedsSessionFileDownload_SizeDiffers_True() =>
        Assert.True(GraphSyncPolicy.NeedsSessionFileDownload(
            localExists: true,
            localLength: 879,
            localLastWriteUtc: DateTime.UtcNow.AddDays(-1),
            remoteSize: 996,
            remoteLastModifiedUtc: DateTime.UtcNow));

    [Fact]
    public void NeedsSessionFileDownload_SameSizeRemoteNewer_True()
    {
        // Entries.ini timestamp edit: same 996 bytes, newer OneDrive version on 2nd laptop.
        var localWrite = new DateTime(2026, 10, 27, 15, 42, 0, DateTimeKind.Utc);
        var remoteWrite = new DateTime(2026, 10, 31, 15, 27, 0, DateTimeKind.Utc);
        Assert.True(GraphSyncPolicy.NeedsSessionFileDownload(
            localExists: true,
            localLength: 996,
            localLastWriteUtc: localWrite,
            remoteSize: 996,
            remoteLastModifiedUtc: remoteWrite));
    }

    [Fact]
    public void NeedsSessionFileDownload_SameSizeLocalCurrent_False()
    {
        var stamp = new DateTime(2026, 10, 31, 15, 27, 0, DateTimeKind.Utc);
        Assert.False(GraphSyncPolicy.NeedsSessionFileDownload(
            localExists: true,
            localLength: 996,
            localLastWriteUtc: stamp,
            remoteSize: 996,
            remoteLastModifiedUtc: stamp));
    }

    [Theory]
    [InlineData(@"RUNSCORE\Logs\2026-11-08_runscore.log", true)]
    [InlineData("RUNSCORE/Logs/2026-11-08_runscore.log", true)]
    [InlineData(@"RUNSCORE\Events\2026-11-08_events.txt", true)]
    [InlineData(@"RUNSCORE\ENTRIES.DTA", false)]
    [InlineData(@"Logs\2026-11-08_runscore.log", false)]
    public void IsRunScoreLogRelative_OnlyDailyLogs(string relative, bool isLog) =>
        Assert.Equal(isLog, GraphSyncPolicy.IsRunScoreLogRelative(relative));

    [Fact]
    public void IsOversizeRunScoreLog_OnlyWhenLogExceedsCap()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s2f-logcap-" + Guid.NewGuid().ToString("N"));
        var log = Path.Combine(dir, "2026-11-08_runscore.log");
        var db = Path.Combine(dir, "ENTRIES.DTA");
        Directory.CreateDirectory(dir);
        try
        {
            using (var fs = new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.Read))
                fs.SetLength(100);
            using (var fs = new FileStream(db, FileMode.Create, FileAccess.Write, FileShare.Read))
                fs.SetLength(100);

            var logRel = Path.Combine("RUNSCORE", "Logs", "2026-11-08_runscore.log");
            var dbRel = Path.Combine("RUNSCORE", "ENTRIES.DTA");
            Assert.True(GraphSyncPolicy.IsOversizeRunScoreLog(logRel, log, maxBytes: 50));
            Assert.False(GraphSyncPolicy.IsOversizeRunScoreLog(logRel, log, maxBytes: 100));
            Assert.False(GraphSyncPolicy.IsOversizeRunScoreLog(dbRel, db, maxBytes: 50));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }

    [Theory]
    [InlineData("Logs", true)]
    [InlineData("logs", true)]
    [InlineData("Events", true)]
    [InlineData("events", true)]
    [InlineData("_autosave", false)]
    public void ShouldSkipRunScoreDownloadFolder_SkipsLogsAndEvents(string name, bool skip) =>
        Assert.Equal(skip, GraphSyncPolicy.ShouldSkipRunScoreDownloadFolder(name));

    [Fact]
    public void DisambiguatedLogFileName_KeepsOriginalAndAddsMachine()
    {
        var name = GraphSyncPolicy.DisambiguatedLogFileName(
            "2026-11-08_runscore.log",
            "Timer 1",
            new DateTime(2026, 11, 8, 11, 45, 12));
        Assert.StartsWith("2026-11-08_runscore_Timer_1_", name);
        Assert.EndsWith(".log", name);
        Assert.NotEqual("2026-11-08_runscore.log", name);
    }

    [Fact]
    public void RemoveLocalRunScoreLogs_DeletesLogsAndLeavesRaceFiles()
    {
        var runScore = Path.Combine(Path.GetTempPath(), "s2f-logs-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(runScore, "Logs");
        var events = Path.Combine(runScore, "Events");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(events);
        var race = Path.Combine(runScore, "ENTRIES.DTA");
        try
        {
            File.WriteAllText(Path.Combine(logs, "2026-11-08_runscore.log"), "old");
            File.WriteAllText(Path.Combine(events, "2026-11-08_events.txt"), "old");
            File.WriteAllText(race, "race");
            GraphSyncPolicy.RemoveLocalRunScoreLogs(runScore);
            Assert.False(Directory.Exists(logs));
            Assert.False(Directory.Exists(events));
            Assert.True(File.Exists(race));
        }
        finally
        {
            try { Directory.Delete(runScore, recursive: true); } catch { /* temp */ }
        }
    }

    [Theory]
    [InlineData("2026-11-04_IgnoredRawChipReads.txt", true)]
    [InlineData("2026-11-05_IgnoredCloudChipReads.txt", true)]
    [InlineData("2026-11-08_events.txt", false)]
    [InlineData("ENTRIES.DTA", false)]
    public void IsIgnoredChipReadFile_MatchesDatedChipFilesOnly(string name, bool match) =>
        Assert.Equal(match, GraphSyncPolicy.IsIgnoredChipReadFile(name));

    [Fact]
    public void IsStaleIgnoredChipRead_SkipsPreviousDatesKeepsToday()
    {
        var today = new DateTime(2026, 11, 8);
        Assert.True(GraphSyncPolicy.IsStaleIgnoredChipRead("2025-11-10_IgnoredRawChipReads.txt", today));
        Assert.True(GraphSyncPolicy.IsStaleIgnoredChipRead("2026-11-05_IgnoredCloudChipReads.txt", today));
        Assert.False(GraphSyncPolicy.IsStaleIgnoredChipRead("2026-11-08_IgnoredRawChipReads.txt", today));
        Assert.False(GraphSyncPolicy.IsStaleIgnoredChipRead("ENTRIES.DTA", today));
    }

    [Fact]
    public void RemoveLocalIgnoredChipReads_DeletesChipFilesAndLeavesRaceFiles()
    {
        var runScore = Path.Combine(Path.GetTempPath(), "s2f-chip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runScore);
        var race = Path.Combine(runScore, "ENTRIES.DTA");
        var old = Path.Combine(runScore, "2026-11-04_IgnoredRawChipReads.txt");
        try
        {
            File.WriteAllText(race, "race");
            File.WriteAllText(old, "chip");
            GraphSyncPolicy.RemoveLocalIgnoredChipReads(runScore);
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(race));
        }
        finally
        {
            try { Directory.Delete(runScore, recursive: true); } catch { /* temp */ }
        }
    }
}
