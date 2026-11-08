using System.Net;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class GraphDriveSyncServiceTests
{
    [Theory]
    [InlineData(@"C:\S2F\Library", @"C:\S2F\Library\Memphis\2026 - Race", "", "Memphis/2026 - Race")]
    [InlineData(@"C:\S2F\Library", @"C:\S2F\Library\Memphis\2026 - Race", "RaceDay", "RaceDay/Memphis/2026 - Race")]
    [InlineData(@"C:\S2F\Library", @"C:\S2F\Library", "RaceDay", "RaceDay")]
    [InlineData(@"C:\S2F\Library", @"C:\Other\Memphis", "", null)]
    public void ToDriveRelative_MapsLibraryPaths(string libraryRoot, string local, string driveRoot, string? expected)
    {
        var actual = GraphDriveSyncService.ToDriveRelative(local, libraryRoot, driveRoot);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void JoinDrivePath_NormalizesSeparators()
    {
        Assert.Equal("a/b/c", GraphDriveSyncService.JoinDrivePath(@"a\b", "c"));
        Assert.Equal("c", GraphDriveSyncService.JoinDrivePath("", "c"));
        Assert.Equal("a/b", GraphDriveSyncService.JoinDrivePath("a/b", ""));
    }

    [Fact]
    public async Task NullCloud_IsNotAvailable()
    {
        var cloud = new NullCloudLibrarySync();
        Assert.False(cloud.IsAvailable);
        var result = await cloud.DownloadSessionAsync(@"C:\x", @"C:\x", "");
        Assert.True(result.Success);
    }

    [Fact]
    public void FormatOfficeListMessage_ReportsFoundAndRemoved()
    {
        Assert.Equal(
            "Found 6 office folder(s). 1 removed (not on OneDrive).",
            GraphDriveSyncService.FormatOfficeListMessage(6, 1));
        Assert.Equal(
            "No office folders found on OneDrive. Check Graph folder path in Admin.",
            GraphDriveSyncService.FormatOfficeListMessage(0, 0));
    }

    [Fact]
    public void FormatOfficeSessionsRefreshMessage_AlreadyInSync_DoesNotSayNoEvents()
    {
        // Bug: created=0 pruned=0 used to mean “no RUNSCORE on OneDrive” even when remoteCount > 0.
        var msg = GraphDriveSyncService.FormatOfficeSessionsRefreshMessage("Memphis", 0, 0, 12);
        Assert.Contains("12 races", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no races", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RUNSCORE", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatOfficeSessionsRefreshMessage_EmptyRemote()
    {
        Assert.Equal(
            "Memphis: no races on OneDrive.",
            GraphDriveSyncService.FormatOfficeSessionsRefreshMessage("Memphis", 0, 0, 0));
    }

    [Fact]
    public void FormatOfficeSessionsRefreshMessage_CreatedAndPruned()
    {
        var msg = GraphDriveSyncService.FormatOfficeSessionsRefreshMessage("Memphis", 2, 1, 10);
        Assert.Equal("Memphis: 2 new, 1 removed (moved/gone on OneDrive).", msg);
    }

    [Fact]
    public async Task UploadSession_OversizeRunScoreLog_UploadsWhenGraphAccepts()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-up-" + Guid.NewGuid().ToString("N"));
        var session = Path.Combine(root, "St. Louis", "2026 - Race");
        var rel = Path.Combine("RUNSCORE", "Logs", "2026-11-08_runscore.log");
        var log = Path.Combine(session, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        try
        {
            using (var fs = new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.Read))
                fs.SetLength(GraphSyncPolicy.FinishMaxFileBytes + 1);

            var handler = new RecordingHandler();
            var cloud = new GraphDriveSyncService(new SignedInTokens(), new HttpClient(handler));
            var result = await cloud.UploadSessionAsync(
                session, root, graphDriveRoot: "", onlyRelativePaths: new[] { rel });

            Assert.True(result.Success);
            Assert.Empty(result.FailedRelativePaths);
            Assert.Null(result.SkippedNote);
            Assert.True(result.FilesTransferred >= 1);
            Assert.Contains(handler.Requests, r =>
                r.Method == HttpMethod.Post
                && r.RequestUri!.AbsoluteUri.Contains("createUploadSession", StringComparison.Ordinal)
                && r.RequestUri.AbsoluteUri.Contains("2026-11-08_runscore.log", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task UploadSession_OversizeRunScoreLog_BudgetTimeout_StaysOnLaptop()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-up-" + Guid.NewGuid().ToString("N"));
        var session = Path.Combine(root, "St. Louis", "2026 - Race");
        var rel = Path.Combine("RUNSCORE", "Logs", "2026-11-08_runscore.log");
        var log = Path.Combine(session, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        try
        {
            using (var fs = new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.Read))
                fs.SetLength(GraphSyncPolicy.FinishMaxFileBytes + 1);

            using var cts = new CancellationTokenSource();
            var handler = new RecordingHandler(onSend: () => cts.Cancel());
            var cloud = new GraphDriveSyncService(new SignedInTokens(), new HttpClient(handler));
            var result = await cloud.UploadSessionAsync(
                session, root, graphDriveRoot: "", onlyRelativePaths: new[] { rel }, cancellationToken: cts.Token);

            Assert.True(result.Success);
            Assert.Empty(result.FailedRelativePaths);
            Assert.NotEmpty(handler.Requests);
            Assert.Contains("2026-11-08_runscore.log", result.SkippedNote);
            Assert.Contains("did not finish uploading in time", result.SkippedNote);
            Assert.Contains("stayed on this laptop", result.SkippedNote);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task UploadSession_OversizeRaceFile_StillBlocksFinish()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-up-" + Guid.NewGuid().ToString("N"));
        var session = Path.Combine(root, "St. Louis", "2026 - Race");
        var rel = Path.Combine("RUNSCORE", "ENTRIES.DTA");
        var file = Path.Combine(session, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read))
                fs.SetLength(GraphSyncPolicy.FinishMaxFileBytes + 1);

            var handler = new RecordingHandler();
            var cloud = new GraphDriveSyncService(new SignedInTokens(), new HttpClient(handler));
            var result = await cloud.UploadSessionAsync(
                session, root, graphDriveRoot: "", onlyRelativePaths: new[] { rel });

            Assert.False(result.Success);
            Assert.Contains(result.FailedRelativePaths, p => p.EndsWith("ENTRIES.DTA", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(handler.Requests);
            Assert.Null(result.SkippedNote);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task UploadSession_SmallRunScoreLog_StillUploads()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-up-" + Guid.NewGuid().ToString("N"));
        var session = Path.Combine(root, "St. Louis", "2026 - Race");
        var rel = Path.Combine("RUNSCORE", "Logs", "2026-11-07_runscore.log");
        var log = Path.Combine(session, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        try
        {
            await File.WriteAllTextAsync(log, "small daily log");
            var handler = new RecordingHandler();
            var cloud = new GraphDriveSyncService(new SignedInTokens(), new HttpClient(handler));
            var result = await cloud.UploadSessionAsync(
                session, root, graphDriveRoot: "", onlyRelativePaths: new[] { rel });

            Assert.True(result.Success);
            Assert.Empty(result.FailedRelativePaths);
            Assert.Null(result.SkippedNote);
            Assert.Contains(handler.Requests, r =>
                r.Method == HttpMethod.Put
                && r.RequestUri!.AbsoluteUri.Contains("2026-11-07_runscore.log", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task UploadSession_LogAlreadyOnOneDrive_KeepsBothNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-up-" + Guid.NewGuid().ToString("N"));
        var session = Path.Combine(root, "St. Louis", "2026 - Race");
        var rel = Path.Combine("RUNSCORE", "Logs", "2026-11-08_runscore.log");
        var log = Path.Combine(session, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        try
        {
            await File.WriteAllTextAsync(log, "this laptop");
            var handler = new RecordingHandler(logAlreadyOnDrive: true);
            var cloud = new GraphDriveSyncService(new SignedInTokens(), new HttpClient(handler));
            var result = await cloud.UploadSessionAsync(
                session, root, graphDriveRoot: "", onlyRelativePaths: new[] { rel });

            Assert.True(result.Success);
            Assert.Contains(handler.Requests, r =>
                r.Method == HttpMethod.Put
                && r.RequestUri!.AbsoluteUri.Contains("2026-11-08_runscore_", StringComparison.Ordinal));
            Assert.DoesNotContain(handler.Requests, r =>
                r.Method == HttpMethod.Put
                && r.RequestUri!.AbsoluteUri.Contains("2026-11-08_runscore.log", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task UploadSession_EventAlreadyOnOneDrive_KeepsBothNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-up-" + Guid.NewGuid().ToString("N"));
        var session = Path.Combine(root, "St. Louis", "2026 - Race");
        var rel = Path.Combine("RUNSCORE", "Events", "2026-11-08_events.txt");
        var file = Path.Combine(session, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            await File.WriteAllTextAsync(file, "this laptop");
            var handler = new RecordingHandler(logAlreadyOnDrive: true);
            var cloud = new GraphDriveSyncService(new SignedInTokens(), new HttpClient(handler));
            var result = await cloud.UploadSessionAsync(
                session, root, graphDriveRoot: "", onlyRelativePaths: new[] { rel });

            Assert.True(result.Success);
            Assert.Contains(handler.Requests, r =>
                r.Method == HttpMethod.Put
                && r.RequestUri!.AbsoluteUri.Contains("2026-11-08_events_", StringComparison.Ordinal));
            Assert.DoesNotContain(handler.Requests, r =>
                r.Method == HttpMethod.Put
                && r.RequestUri!.AbsoluteUri.Contains("2026-11-08_events.txt", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task UploadSession_IgnoredChipReadAlreadyOnOneDrive_KeepsBothNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-up-" + Guid.NewGuid().ToString("N"));
        var session = Path.Combine(root, "St. Louis", "2026 - Race");
        var name = DateTime.Today.ToString("yyyy-MM-dd") + "_IgnoredRawChipReads.txt";
        var rel = Path.Combine("RUNSCORE", name);
        var file = Path.Combine(session, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            await File.WriteAllTextAsync(file, "this laptop");
            var handler = new RecordingHandler(logAlreadyOnDrive: true);
            var cloud = new GraphDriveSyncService(new SignedInTokens(), new HttpClient(handler));
            var result = await cloud.UploadSessionAsync(
                session, root, graphDriveRoot: "", onlyRelativePaths: new[] { rel });

            Assert.True(result.Success);
            Assert.Contains(handler.Requests, r =>
                r.Method == HttpMethod.Put
                && r.RequestUri!.AbsoluteUri.Contains("_IgnoredRawChipReads_", StringComparison.Ordinal));
            Assert.DoesNotContain(handler.Requests, r =>
                r.Method == HttpMethod.Put
                && r.RequestUri!.AbsoluteUri.Contains(name, StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task DownloadSession_DoesNotPullRunScoreLogs()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2f-dl-" + Guid.NewGuid().ToString("N"));
        var session = Path.Combine(root, "Office", "Race");
        Directory.CreateDirectory(session);
        try
        {
            var handler = new SessionListHandler();
            var cloud = new GraphDriveSyncService(new SignedInTokens(), new HttpClient(handler));
            var result = await cloud.DownloadSessionAsync(session, root, graphDriveRoot: "");

            Assert.True(result.Success);
            Assert.Contains(handler.Urls, u => u.Contains("ENTRIES.DTA", StringComparison.Ordinal));
            Assert.DoesNotContain(handler.Urls, u => u.Contains("/Logs", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(handler.Urls, u => u.Contains("/Events", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(handler.Urls, u => u.Contains("IgnoredRawChipReads", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(handler.Urls, u => u.Contains("IgnoredCloudChipReads", StringComparison.OrdinalIgnoreCase));
            Assert.False(File.Exists(Path.Combine(session, "RUNSCORE", "Logs", "2026-11-04_runscore.log")));
            Assert.False(File.Exists(Path.Combine(session, "RUNSCORE", "Events", "2026-11-08_events.txt")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }

    private sealed class SignedInTokens : IGraphTokenProvider
    {
        public bool IsSignedIn => true;
        public string? AccountEmail => "timing@example.com";
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("token");
        public Task<bool> SignInAsync(IntPtr? parentWindow = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
        public Task SignOutAsync() => Task.CompletedTask;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Action? _onSend;
        private readonly bool _logAlreadyOnDrive;

        public RecordingHandler(Action? onSend = null, bool logAlreadyOnDrive = false)
        {
            _onSend = onSend;
            _logAlreadyOnDrive = logAlreadyOnDrive;
        }

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            _onSend?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            var url = request.RequestUri?.AbsoluteUri ?? "";
            if (request.Method == HttpMethod.Get
                && !url.Contains("/content", StringComparison.Ordinal)
                && !url.Contains("children", StringComparison.Ordinal))
            {
                if (!_logAlreadyOnDrive)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"id\":\"existing\"}")
                });
            }

            if (url.Contains("createUploadSession", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"uploadUrl\":\"https://upload.example/session\"}")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }

    private sealed class SessionListHandler : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.AbsoluteUri ?? "";
            Urls.Add(url);
            if (url.Contains("/content", StringComparison.Ordinal))
            {
                var bytes = url.Contains("ENTRIES.DTA", StringComparison.Ordinal)
                    ? new byte[] { 1, 2, 3, 4 }
                    : new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                });
            }

            var json = url.Contains("RUNSCORE/Logs", StringComparison.OrdinalIgnoreCase)
                ? """{"value":[{"name":"2026-11-04_runscore.log","file":{},"size":10}]}"""
                : url.Contains("RUNSCORE", StringComparison.Ordinal)
                    ? """{"value":[{"name":"Logs","folder":{}},{"name":"Events","folder":{}},{"name":"2026-11-04_IgnoredRawChipReads.txt","file":{},"size":4},{"name":"ENTRIES.DTA","file":{},"size":4}]}"""
                    : """{"value":[{"name":"RUNSCORE","folder":{}},{"name":"race.event","file":{},"size":8}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
        }
    }
}
