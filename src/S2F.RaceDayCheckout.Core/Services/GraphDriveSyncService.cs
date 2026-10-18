using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Personal OneDrive sync via Microsoft Graph REST (<c>/me/drive</c>).
/// Tuned for race-day cellular: small payloads, short timeouts, parallel session download.
/// </summary>
public sealed class GraphDriveSyncService : ICloudLibrarySync
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string GraphRoot = "https://graph.microsoft.com/v1.0";

    private readonly IGraphTokenProvider _tokens;
    private readonly HttpClient _http;

    public GraphDriveSyncService(IGraphTokenProvider tokens, HttpClient? http = null)
    {
        _tokens = tokens;
        // Per-op CancellationToken budgets cancel long transfers (parallel pack download).
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public bool IsAvailable => _tokens.IsSignedIn;

    public async Task<CloudSyncResult> RefreshOfficeListAsync(
        string libraryRoot,
        string graphDriveRoot,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        if (!_tokens.IsSignedIn)
            return new CloudSyncResult { Success = false, Message = "Sign in to OneDrive first." };

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(GraphSyncPolicy.OfficeListBudget);

        try
        {
            Directory.CreateDirectory(libraryRoot);
            var rootPath = NormalizeDrivePath(graphDriveRoot);
            status?.Report(string.IsNullOrEmpty(rootPath)
                ? "Checking office folders…"
                : $"Listing offices under {rootPath}…");

            var found = 0;
            if (!string.IsNullOrEmpty(rootPath))
            {
                // Cellular: one children list under Start 2 - Personal (~9 folders), not OD root.
                var remote = await ListChildFoldersAsync(rootPath, budget.Token).ConfigureAwait(false);
                foreach (var officeName in PathService.AllowedOfficeFolders)
                {
                    budget.Token.ThrowIfCancellationRequested();
                    if (!remote.Contains(officeName, StringComparer.OrdinalIgnoreCase))
                        continue;
                    Directory.CreateDirectory(Path.Combine(libraryRoot, officeName));
                    found++;
                }
            }
            else
            {
                // No GraphDriveRoot: probe known names only — never list entire OneDrive root.
                foreach (var officeName in PathService.AllowedOfficeFolders)
                {
                    budget.Token.ThrowIfCancellationRequested();
                    status?.Report($"Checking {officeName}…");
                    if (!await FolderExistsAsync(officeName, budget.Token).ConfigureAwait(false))
                        continue;
                    Directory.CreateDirectory(Path.Combine(libraryRoot, officeName));
                    found++;
                }
            }

            return new CloudSyncResult
            {
                Success = true,
                FilesTransferred = found,
                Message = found > 0
                    ? $"Found {found} office folder(s)."
                    : "No office folders found on OneDrive. Check Graph folder path in Admin."
            };
        }
        catch (OperationCanceledException)
        {
            return new CloudSyncResult
            {
                Success = false,
                Message = "Office check timed out (weak signal). Try again or pick an office already on this laptop."
            };
        }
        catch (Exception ex)
        {
            return new CloudSyncResult { Success = false, Message = "Could not list offices: " + ex.Message };
        }
    }

    public async Task<CloudSyncResult> RefreshOfficeSessionsAsync(
        string libraryRoot,
        string graphDriveRoot,
        string officeName,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default,
        string? protectSessionPath = null)
    {
        if (!_tokens.IsSignedIn)
            return new CloudSyncResult { Success = false, Message = "Sign in to OneDrive first." };

        if (string.IsNullOrWhiteSpace(officeName)
            || !PathService.AllowedOfficeFolders.Contains(officeName, StringComparer.OrdinalIgnoreCase))
        {
            return new CloudSyncResult { Success = false, Message = "Unknown office." };
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(GraphSyncPolicy.OfficeSessionsBudget);

        try
        {
            var rootPath = NormalizeDrivePath(graphDriveRoot);
            var officeLocal = Path.Combine(libraryRoot, officeName);
            Directory.CreateDirectory(officeLocal);
            var officeCloud = JoinDrivePath(rootPath, officeName);
            status?.Report($"Loading events for {officeName}…");

            if (!await FolderExistsAsync(officeCloud, budget.Token).ConfigureAwait(false))
            {
                return new CloudSyncResult
                {
                    Success = false,
                    Message = $"“{officeName}” was not found under OneDrive “{rootPath}”. Check the Graph folder path in Admin."
                };
            }

            // Full remote list required before prune — never delete on timeout/partial walk
            // (staff move events to Past Races; stale local folders under the old office must go).
            var (created, pruned) = await SyncOfficeSessionPlaceholdersAsync(
                    officeLocal, officeCloud, protectSessionPath, budget.Token)
                .ConfigureAwait(false);

            var parts = new List<string>();
            if (created > 0)
                parts.Add($"{created} new");
            if (pruned > 0)
                parts.Add($"{pruned} removed (moved/gone on OneDrive)");
            return new CloudSyncResult
            {
                Success = true,
                FilesTransferred = created + pruned,
                Message = parts.Count > 0
                    ? $"{officeName}: {string.Join(", ", parts)}."
                    : created == 0 && pruned == 0
                        ? $"{officeName}: no events with a RUNSCORE folder found on OneDrive."
                        : $"{officeName}: event list up to date."
            };
        }
        catch (OperationCanceledException)
        {
            return new CloudSyncResult
            {
                Success = false,
                Message = $"Loading events for {officeName} timed out. Local list unchanged — go Back and try again when signal is better."
            };
        }
        catch (Exception ex)
        {
            return new CloudSyncResult { Success = false, Message = "Could not load events: " + ex.Message };
        }
    }

    public async Task<CloudSyncResult> RefreshFolderChildrenAsync(
        string libraryRoot,
        string graphDriveRoot,
        string relativeUnderLibrary,
        bool probeSessionLeaves = false,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        if (!_tokens.IsSignedIn)
            return new CloudSyncResult { Success = false, Message = "Sign in to OneDrive first." };

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(probeSessionLeaves
            ? GraphSyncPolicy.OfficeSessionsBudget
            : GraphSyncPolicy.OfficeListBudget);

        try
        {
            var rootPath = NormalizeDrivePath(graphDriveRoot);
            var relative = NormalizeDrivePath(relativeUnderLibrary);
            if (string.IsNullOrEmpty(relative))
                return new CloudSyncResult { Success = false, Message = "Folder path is required." };

            var localFolder = Path.Combine(libraryRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(localFolder);
            var cloudFolder = JoinDrivePath(rootPath, relative);
            status?.Report($"Listing {relative.Replace("/", " → ", StringComparison.Ordinal)}…");

            if (!await FolderExistsAsync(cloudFolder, budget.Token).ConfigureAwait(false))
            {
                return new CloudSyncResult
                {
                    Success = false,
                    Message = $"“{relative}” was not found on OneDrive."
                };
            }

            var children = await ListChildFoldersAsync(cloudFolder, budget.Token).ConfigureAwait(false);
            var created = 0;
            var sessions = 0;
            foreach (var name in children)
            {
                budget.Token.ThrowIfCancellationRequested();
                if (IsSkippedChildFolderName(name))
                    continue;

                var localChild = Path.Combine(localFolder, name);
                if (!Directory.Exists(localChild))
                {
                    Directory.CreateDirectory(localChild);
                    created++;
                }

                if (!probeSessionLeaves)
                    continue;

                status?.Report($"Checking {name}…");
                var runScoreCloud = JoinDrivePath(cloudFolder, name + "/" + PathService.RunScoreFolderName);
                if (!await FolderExistsAsync(runScoreCloud, budget.Token).ConfigureAwait(false))
                    continue;

                var runScoreLocal = Path.Combine(localChild, PathService.RunScoreFolderName);
                if (!Directory.Exists(runScoreLocal))
                {
                    Directory.CreateDirectory(runScoreLocal);
                    sessions++;
                }
            }

            return new CloudSyncResult
            {
                Success = true,
                FilesTransferred = created + sessions,
                Message = sessions > 0
                    ? $"Found {children.Count} item(s), {sessions} event(s)."
                    : $"Found {Math.Max(created, children.Count(c => !IsSkippedChildFolderName(c)))} folder(s)."
            };
        }
        catch (OperationCanceledException)
        {
            return new CloudSyncResult
            {
                Success = false,
                Message = "Folder list timed out. Try again when signal is better."
            };
        }
        catch (Exception ex)
        {
            return new CloudSyncResult { Success = false, Message = "Could not list folder: " + ex.Message };
        }
    }

    private static bool IsSkippedChildFolderName(string name) =>
        string.Equals(name, PathService.RunScoreFolderName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, SharedFolderLinkService.DbsFolderName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Pre-Race", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("._", StringComparison.Ordinal)
        || string.Equals(name, PathService.AutosaveFolderName, StringComparison.OrdinalIgnoreCase);

    public async Task<CloudSyncResult> DownloadSessionAsync(
        string librarySessionPath,
        string libraryRoot,
        string graphDriveRoot,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default,
        SessionDownloadOptions? options = null)
    {
        if (!_tokens.IsSignedIn)
            return new CloudSyncResult { Success = true, Message = "Graph not signed in — using local library." };

        try
        {
            var relative = ToDriveRelative(librarySessionPath, libraryRoot, graphDriveRoot);
            if (relative is null)
                return new CloudSyncResult { Success = false, Message = "Session path is outside LibraryRoot." };

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(GraphSyncPolicy.SessionDownloadBudget);

            // Graph does not support folder-as-ZIP on personal OneDrive (?format=zip is for Office→PDF).
            // Enumerate once, then download several files at a time.
            var slim = options?.Role == CheckoutRole.Announcer;
            status?.Report(slim ? "Listing announcer files…" : "Listing race files…");
            Directory.CreateDirectory(librarySessionPath);
            var jobs = new List<DownloadJob>();
            await CollectDownloadJobsAsync(
                    relative, librarySessionPath, librarySessionPath, jobs, budget.Token, options)
                .ConfigureAwait(false);

            var todo = jobs
                .Where(j => j.Size is null or <= GraphSyncPolicy.FinishMaxFileBytes)
                .Where(j =>
                {
                    if (!File.Exists(j.LocalPath) || j.Size is not long remoteSize)
                        return true;
                    try { return new FileInfo(j.LocalPath).Length != remoteSize; }
                    catch { return true; }
                })
                .ToList();

            if (todo.Count == 0)
            {
                return new CloudSyncResult
                {
                    Success = true,
                    FilesTransferred = 0,
                    Message = jobs.Count > 0
                        ? "Race files already on this laptop."
                        : "OneDrive session had no files (using local library if present)."
                };
            }

            status?.Report($"Downloading 0/{todo.Count}…");
            var done = 0;
            await using var gate = new AdaptiveTransferGate(
                GraphSyncPolicy.DownloadParallelism,
                GraphSyncPolicy.DownloadParallelismFloor);
            var tasks = todo.Select(async job =>
            {
                await using (await gate.AcquireAsync(budget.Token).ConfigureAwait(false))
                {
                    try
                    {
                        await DownloadFileWithGuardsAsync(job, gate, budget.Token).ConfigureAwait(false);
                        var n = Interlocked.Increment(ref done);
                        status?.Report($"Downloading {n}/{todo.Count}…");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // One bad file must not abort the pack after retries.
                    }
                }
            });

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            return new CloudSyncResult
            {
                Success = true,
                FilesTransferred = done,
                Message = $"Downloaded {done} of {todo.Count} file(s) from OneDrive."
            };
        }
        catch (OperationCanceledException)
        {
            return new CloudSyncResult
            {
                Success = false,
                Message = "Download timed out. Try again on a stronger signal — or checkout with what is already on this laptop."
            };
        }
        catch (Exception ex)
        {
            return new CloudSyncResult { Success = false, Message = "Graph download failed: " + ex.Message };
        }
    }

    public async Task<CloudSyncResult> DownloadSessionRolesAsync(
        IReadOnlyList<string> librarySessionPaths,
        string libraryRoot,
        string graphDriveRoot,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        if (!_tokens.IsSignedIn)
            return new CloudSyncResult { Success = true, Message = "Graph not signed in." };

        var sessions = librarySessionPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sessions.Count == 0)
            return new CloudSyncResult { Success = true, Message = "No events to check for announcer assignments." };

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(GraphSyncPolicy.OfficeSessionsBudget);

        try
        {
            status?.Report("Checking announcer assignments…");
            var downloaded = 0;
            var seen = 0;
            await using var gate = new AdaptiveTransferGate(
                GraphSyncPolicy.DownloadParallelism,
                GraphSyncPolicy.DownloadParallelismFloor);

            var tasks = sessions.Select(async session =>
            {
                await using (await gate.AcquireAsync(budget.Token).ConfigureAwait(false))
                {
                    var relative = ToDriveRelative(session, libraryRoot, graphDriveRoot);
                    if (relative is null)
                        return;

                    var cloud = JoinDrivePath(relative, PathService.AppMetaFolderName + "/" + SessionRoleStore.FileName);
                    var local = SessionRoleStore.GetPath(session);
                    try
                    {
                        if (await TryDownloadOptionalFileAsync(cloud, local, budget.Token).ConfigureAwait(false))
                            Interlocked.Increment(ref downloaded);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // One missing assignment must not fail the office list.
                    }

                    var n = Interlocked.Increment(ref seen);
                    if (n == sessions.Count || n % 8 == 0)
                        status?.Report($"Checking announcer assignments… {n}/{sessions.Count}");
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
            return new CloudSyncResult
            {
                Success = true,
                FilesTransferred = downloaded,
                Message = downloaded > 0
                    ? $"Announcer assignments updated ({downloaded})."
                    : "No announcer assignments on OneDrive for these events."
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CloudSyncResult
            {
                Success = false,
                Message = "Announcer assignment check timed out — showing assignments already on this laptop."
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CloudSyncResult
            {
                Success = false,
                Message = "Could not check announcer assignments: " + ex.Message
            };
        }
    }

    public async Task<IReadOnlyList<string>> ListRemoteEventFileNamesAsync(
        string librarySessionPath,
        string libraryRoot,
        string graphDriveRoot,
        CancellationToken cancellationToken = default)
    {
        if (!_tokens.IsSignedIn)
            return Array.Empty<string>();

        var relative = ToDriveRelative(librarySessionPath, libraryRoot, graphDriveRoot);
        if (relative is null)
            return Array.Empty<string>();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(GraphSyncPolicy.OfficeListBudget);
        try
        {
            var items = await ListChildItemsAsync(relative, budget.Token).ConfigureAwait(false);
            return items
                .Where(i => i.IsFile && !string.IsNullOrWhiteSpace(i.Name)
                            && i.Name.EndsWith(".event", StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public async Task<CloudSyncResult> UploadSessionRolesAsync(
        string librarySessionPath,
        string libraryRoot,
        string graphDriveRoot,
        CancellationToken cancellationToken = default)
    {
        if (!_tokens.IsSignedIn)
            return new CloudSyncResult { Success = true, Message = "Saved on this laptop. Sign in to OneDrive to sync the assignment." };

        var local = SessionRoleStore.GetPath(librarySessionPath);
        if (!File.Exists(local))
            return new CloudSyncResult { Success = false, Message = "No assignment file to upload." };

        var relative = ToDriveRelative(librarySessionPath, libraryRoot, graphDriveRoot);
        if (relative is null)
            return new CloudSyncResult { Success = false, Message = "Session path is outside LibraryRoot." };

        try
        {
            var cloud = JoinDrivePath(relative, PathService.AppMetaFolderName + "/" + SessionRoleStore.FileName);
            await UploadFileAsync(local, cloud, cancellationToken).ConfigureAwait(false);
            return new CloudSyncResult
            {
                Success = true,
                FilesTransferred = 1,
                Message = "Assignment saved to OneDrive."
            };
        }
        catch (Exception ex)
        {
            return new CloudSyncResult
            {
                Success = false,
                Message = "Saved on this laptop, but OneDrive upload failed: " + ex.Message
            };
        }
    }

    public async Task<CloudSyncResult> UploadSessionAsync(
        string librarySessionPath,
        string libraryRoot,
        string graphDriveRoot,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? onlyRelativePaths = null)
    {
        if (!_tokens.IsSignedIn)
            return new CloudSyncResult { Success = true, Message = "Graph not signed in." };

        try
        {
            var relative = ToDriveRelative(librarySessionPath, libraryRoot, graphDriveRoot);
            if (relative is null)
                return new CloudSyncResult { Success = false, Message = "Session path is outside LibraryRoot." };

            status?.Report("finish:upload");
            var count = 0;
            var raceFailures = 0;
            var eventFailures = 0;
            var plannedRaceFiles = onlyRelativePaths?.Count ?? -1;

            await using var uploadGate = new AdaptiveTransferGate(
                GraphSyncPolicy.UploadParallelism,
                GraphSyncPolicy.UploadParallelismFloor);

            if (onlyRelativePaths is not null)
            {
                // Finish: upload only Active-vs-Library diffs (caller compared before finish:copy).
                // Empty list = no race content changed — still push lock metadata.
                var jobs = new List<UploadJob>();
                foreach (var sessionRel in onlyRelativePaths)
                {
                    if (string.IsNullOrWhiteSpace(sessionRel))
                        continue;
                    var localFile = Path.Combine(librarySessionPath, sessionRel);
                    if (GraphSyncPolicy.ShouldSkipFile(localFile, GraphSyncPolicy.FinishMaxFileBytes))
                        continue;
                    var cloudRel = sessionRel.Replace('\\', '/');
                    jobs.Add(new UploadJob(
                        localFile,
                        JoinDrivePath(relative, cloudRel),
                        cloudRel.EndsWith(".event", StringComparison.OrdinalIgnoreCase)
                            ? "timing file"
                            : "changed"));
                }

                var uploaded = await UploadJobsParallelAsync(
                    jobs, uploadGate, status, cancellationToken).ConfigureAwait(false);
                count += uploaded.Succeeded;
                raceFailures += uploaded.Failed;
                eventFailures += uploaded.EventFailed;
            }
            else
            {
                // Full scoped tree — reserved for callers that did not pre-diff.
                var runScore = Path.Combine(librarySessionPath, PathService.RunScoreFolderName);
                if (Directory.Exists(runScore))
                {
                    count += await UploadTreeAsync(
                        runScore,
                        JoinDrivePath(relative, PathService.RunScoreFolderName),
                        GraphSyncPolicy.FinishMaxFileBytes,
                        status,
                        cancellationToken,
                        uploadGate,
                        progressLabel: "RUNSCORE").ConfigureAwait(false);
                }

                var dbs = Path.Combine(librarySessionPath, SharedFolderLinkService.DbsFolderName);
                if (Directory.Exists(dbs) && !IsReparsePoint(dbs))
                {
                    count += await UploadTreeAsync(
                        dbs,
                        JoinDrivePath(relative, SharedFolderLinkService.DbsFolderName),
                        GraphSyncPolicy.FinishMaxFileBytes,
                        status,
                        cancellationToken,
                        uploadGate,
                        progressLabel: "DB's").ConfigureAwait(false);
                }

                var eventJobs = new List<UploadJob>();
                if (Directory.Exists(librarySessionPath))
                {
                    foreach (var eventFile in Directory.EnumerateFiles(librarySessionPath, "*.event"))
                    {
                        if (GraphSyncPolicy.ShouldSkipFile(eventFile, GraphSyncPolicy.FinishMaxFileBytes))
                            continue;
                        eventJobs.Add(new UploadJob(
                            eventFile,
                            JoinDrivePath(relative, Path.GetFileName(eventFile)!),
                            "timing file"));
                    }
                }

                var eventResult = await UploadJobsParallelAsync(
                    eventJobs, uploadGate, status, cancellationToken).ConfigureAwait(false);
                count += eventResult.Succeeded;
                eventFailures += eventResult.EventFailed;
            }

            // Lock metadata only — never upload Finish backup trees under ._checkin (cellular).
            count += await UploadLockMetadataAsync(
                librarySessionPath,
                relative,
                status,
                cancellationToken).ConfigureAwait(false);

            if (plannedRaceFiles == 0)
            {
                return new CloudSyncResult
                {
                    Success = true,
                    FilesTransferred = count,
                    Message = count > 0
                        ? $"No race file changes — uploaded {count} lock file(s)."
                        : "No race file changes to upload."
                };
            }

            if (count == 0)
            {
                return new CloudSyncResult
                {
                    Success = false,
                    Message = "No race files could be uploaded to OneDrive (check cellular signal)."
                };
            }

            if (eventFailures > 0)
            {
                return new CloudSyncResult
                {
                    Success = false,
                    FilesTransferred = count,
                    Message =
                        $"Uploaded {count} file(s), but the Timing & Scoring .event file failed. "
                        + "Tap Finish again when signal improves."
                };
            }

            if (raceFailures > 0)
            {
                return new CloudSyncResult
                {
                    Success = false,
                    FilesTransferred = count,
                    Message =
                        $"Uploaded {count} file(s), but {raceFailures} changed file(s) failed. "
                        + "Tap Finish again when signal improves."
                };
            }

            return new CloudSyncResult
            {
                Success = true,
                FilesTransferred = count,
                Message = $"Uploaded {count} file(s) to OneDrive."
            };
        }
        catch (Exception ex)
        {
            return new CloudSyncResult { Success = false, Message = "Graph upload failed: " + ex.Message };
        }
    }

    private async Task<int> UploadLockMetadataAsync(
        string librarySessionPath,
        string sessionCloudPath,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        var meta = Path.Combine(librarySessionPath, PathService.AppMetaFolderName);
        if (!Directory.Exists(meta))
            return 0;

        var count = 0;
        foreach (var name in new[] { "_checkout.json", "_checkout_history.jsonl" })
        {
            var local = Path.Combine(meta, name);
            if (!File.Exists(local))
                continue;
            try
            {
                await UploadFileAsync(
                        local,
                        JoinDrivePath(sessionCloudPath, PathService.AppMetaFolderName + "/" + name),
                        cancellationToken)
                    .ConfigureAwait(false);
                count++;
            }
            catch
            {
                // Lock upload failure is surfaced via overall Finish retry if nothing else uploaded.
            }
        }

        return count;
    }

    public async Task<CloudSyncResult> UploadCheckoutLockAsync(
        string librarySessionPath,
        string libraryRoot,
        string graphDriveRoot,
        CancellationToken cancellationToken = default)
    {
        if (!_tokens.IsSignedIn)
            return new CloudSyncResult { Success = true, Message = "Graph not signed in." };

        try
        {
            var relative = ToDriveRelative(librarySessionPath, libraryRoot, graphDriveRoot);
            if (relative is null)
                return new CloudSyncResult { Success = false, Message = "Session path is outside LibraryRoot." };

            var count = await UploadLockMetadataAsync(librarySessionPath, relative, status: null, cancellationToken)
                .ConfigureAwait(false);
            return new CloudSyncResult
            {
                Success = true,
                FilesTransferred = count,
                Message = count > 0 ? $"Uploaded {count} lock file(s)." : "No lock files to upload."
            };
        }
        catch (Exception ex)
        {
            return new CloudSyncResult { Success = false, Message = "Lock upload failed: " + ex.Message };
        }
    }

    public async Task<CloudSyncResult> DownloadCheckoutLockAsync(
        string librarySessionPath,
        string libraryRoot,
        string graphDriveRoot,
        CancellationToken cancellationToken = default)
    {
        if (!_tokens.IsSignedIn)
            return new CloudSyncResult { Success = true, Message = "Graph not signed in." };

        try
        {
            var relative = ToDriveRelative(librarySessionPath, libraryRoot, graphDriveRoot);
            if (relative is null)
                return new CloudSyncResult { Success = false, Message = "Session path is outside LibraryRoot." };

            var cloud = JoinDrivePath(relative, PathService.AppMetaFolderName + "/_checkout.json");
            var local = new PathService().GetLockPath(librarySessionPath);
            var got = await TryDownloadOptionalFileAsync(cloud, local, cancellationToken).ConfigureAwait(false);
            return new CloudSyncResult
            {
                Success = true,
                FilesTransferred = got ? 1 : 0,
                Message = got ? "Checkout lock refreshed from OneDrive." : "No checkout lock on OneDrive."
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CloudSyncResult { Success = false, Message = "Lock download failed: " + ex.Message };
        }
    }

    public async Task<CloudSyncResult> UploadFolderAsync(
        string localFolderPath,
        string libraryRoot,
        string graphDriveRoot,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        if (!_tokens.IsSignedIn || !Directory.Exists(localFolderPath))
            return new CloudSyncResult { Success = true, Message = "Nothing to upload." };

        try
        {
            var relative = ToDriveRelative(localFolderPath, libraryRoot, graphDriveRoot);
            if (relative is null)
                return new CloudSyncResult { Success = false, Message = "Folder path is outside LibraryRoot." };

            await using var gate = new AdaptiveTransferGate(
                GraphSyncPolicy.UploadParallelism,
                GraphSyncPolicy.UploadParallelismFloor);
            var count = await UploadTreeAsync(
                    localFolderPath,
                    relative,
                    GraphSyncPolicy.MidRaceMaxFileBytes,
                    status,
                    cancellationToken,
                    gate)
                .ConfigureAwait(false);
            return new CloudSyncResult
            {
                Success = true,
                FilesTransferred = count,
                Message = $"Uploaded {count} file(s)."
            };
        }
        catch (Exception ex)
        {
            return new CloudSyncResult { Success = false, Message = "Graph folder upload failed: " + ex.Message };
        }
    }

    public async Task<CloudSyncResult> UploadLocalFilesAsync(
        IReadOnlyList<string> localFilePaths,
        string libraryRoot,
        string graphDriveRoot,
        CancellationToken cancellationToken = default)
    {
        if (!_tokens.IsSignedIn || localFilePaths.Count == 0)
            return new CloudSyncResult { Success = true, Message = "Nothing to upload." };

        try
        {
            var jobs = new List<UploadJob>();
            foreach (var local in localFilePaths)
            {
                if (string.IsNullOrWhiteSpace(local) || !File.Exists(local))
                    continue;
                if (GraphSyncPolicy.ShouldSkipFile(local, GraphSyncPolicy.MidRaceMaxFileBytes))
                    continue;
                var cloudRel = ToDriveRelative(local, libraryRoot, graphDriveRoot);
                if (cloudRel is null)
                    continue;
                jobs.Add(new UploadJob(local, cloudRel, "mid-race"));
            }

            if (jobs.Count == 0)
                return new CloudSyncResult { Success = true, Message = "No mid-race files to upload." };

            await using var gate = new AdaptiveTransferGate(
                GraphSyncPolicy.UploadParallelism,
                GraphSyncPolicy.UploadParallelismFloor);
            var result = await UploadJobsParallelAsync(jobs, gate, status: null, cancellationToken)
                .ConfigureAwait(false);
            return new CloudSyncResult
            {
                Success = true,
                FilesTransferred = result.Succeeded,
                Message = $"Uploaded {result.Succeeded} mid-race file(s)."
            };
        }
        catch (Exception ex)
        {
            return new CloudSyncResult { Success = false, Message = "Mid-race upload failed: " + ex.Message };
        }
    }

    public async Task<CloudSyncResult> ClearRemoteFolderAsync(
        string localFolderPath,
        string libraryRoot,
        string graphDriveRoot,
        CancellationToken cancellationToken = default)
    {
        if (!_tokens.IsSignedIn)
            return new CloudSyncResult { Success = true, Message = "Graph not signed in." };

        try
        {
            var relative = ToDriveRelative(localFolderPath, libraryRoot, graphDriveRoot);
            if (relative is null)
                return new CloudSyncResult { Success = true, Message = "Path outside library — skipped." };

            // Bound clear so Finish cannot sit on "clearing autosave" while Graph hangs.
            // Prefer one folder DELETE (fast) — child-by-child was N Graph calls for a full RUNSCORE mirror.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(GraphSyncPolicy.RemoteClearBudget);
            await DeleteRemoteFolderAsync(relative, timeout.Token).ConfigureAwait(false);
            return new CloudSyncResult { Success = true, Message = "Remote folder cleared." };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CloudSyncResult { Success = true, Message = "Remote autosave clear timed out — continuing Finish." };
        }
        catch (Exception ex)
        {
            // Non-fatal for Finish — local clear already ran.
            return new CloudSyncResult { Success = true, Message = "Remote clear skipped: " + ex.Message };
        }
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        var token = await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("No Graph access token. Sign in to OneDrive.");
        return token;
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string url,
        CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static string ItemPathUrl(string drivePath, string? suffix = null)
    {
        if (string.IsNullOrEmpty(drivePath))
        {
            return string.IsNullOrEmpty(suffix)
                ? $"{GraphRoot}/me/drive/root"
                : $"{GraphRoot}/me/drive/root/{suffix.TrimStart('/')}";
        }

        var encoded = EncodeDrivePath(drivePath);
        return string.IsNullOrEmpty(suffix)
            ? $"{GraphRoot}/me/drive/root:/{encoded}"
            : $"{GraphRoot}/me/drive/root:/{encoded}:/{suffix.TrimStart('/')}";
    }

    private static string EncodeDrivePath(string path) =>
        string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    private async Task<(int Created, int Pruned)> SyncOfficeSessionPlaceholdersAsync(
        string officeLocal,
        string officeCloud,
        string? protectSessionPath,
        CancellationToken cancellationToken)
    {
        var remote = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var created = 0;
        await foreach (var sessionRel in EnumerateRemoteSessionLeavesAsync(officeCloud, "", cancellationToken)
                           .ConfigureAwait(false))
        {
            var normalized = sessionRel.Replace('\\', '/').Trim('/');
            remote.Add(normalized);
            var localSession = Path.Combine(officeLocal, normalized.Replace('/', Path.DirectorySeparatorChar));
            var runScore = Path.Combine(localSession, PathService.RunScoreFolderName);
            if (!Directory.Exists(runScore))
            {
                Directory.CreateDirectory(runScore);
                created++;
            }
        }

        // Only after a complete remote enumeration (cancellation throws before here).
        // If remote is empty, still prune orphans (office verified to exist by caller) —
        // e.g. every event was moved to Past Races.
        var pruned = LibrarySessionCache.PruneSessionsNotOnRemote(officeLocal, remote, protectSessionPath);
        return (created, pruned);
    }

    private async IAsyncEnumerable<string> EnumerateRemoteSessionLeavesAsync(
        string officeCloud,
        string relativeUnderOffice,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cloudPath = string.IsNullOrEmpty(relativeUnderOffice)
            ? officeCloud
            : JoinDrivePath(officeCloud, relativeUnderOffice);
        var children = await ListChildItemsAsync(cloudPath, cancellationToken).ConfigureAwait(false);

        var hasRunScore = children.Any(c =>
            c.IsFolder
            && string.Equals(c.Name, PathService.RunScoreFolderName, StringComparison.OrdinalIgnoreCase));

        if (hasRunScore)
        {
            yield return relativeUnderOffice.Replace('\\', '/');
            yield break;
        }

        foreach (var child in children.Where(c => c.IsFolder && !string.IsNullOrWhiteSpace(c.Name)))
        {
            var name = child.Name!;
            if (string.Equals(name, PathService.RunScoreFolderName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, SharedFolderLinkService.DbsFolderName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Pre-Race", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("._", StringComparison.Ordinal)
                || name.Equals(PathService.AutosaveFolderName, StringComparison.OrdinalIgnoreCase))
                continue;

            var nextRel = string.IsNullOrEmpty(relativeUnderOffice)
                ? name
                : relativeUnderOffice + "/" + name;
            await foreach (var leaf in EnumerateRemoteSessionLeavesAsync(officeCloud, nextRel, cancellationToken)
                               .ConfigureAwait(false))
                yield return leaf;
        }
    }

    private async Task<bool> FolderExistsAsync(string drivePath, CancellationToken cancellationToken)
    {
        var url = ItemPathUrl(drivePath);
        using var req = await CreateRequestAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return false;
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var item = await JsonSerializer.DeserializeAsync<GraphDriveItem>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return item?.Folder is not null;
    }

    private async Task<List<string>> ListChildFoldersAsync(string parentPath, CancellationToken cancellationToken)
    {
        var items = await ListChildItemsAsync(parentPath, cancellationToken).ConfigureAwait(false);
        return items.Where(i => i.IsFolder && !string.IsNullOrWhiteSpace(i.Name)).Select(i => i.Name!).ToList();
    }

    private async Task<List<DriveChild>> ListChildItemsAsync(string parentPath, CancellationToken cancellationToken)
    {
        var results = new List<DriveChild>();
        // Cellular: skip unused Graph item facets on every children page.
        var url = ItemPathUrl(parentPath, "children?$select=id,name,size,folder,file");
        while (!string.IsNullOrEmpty(url))
        {
            using var req = await CreateRequestAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
            using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return results;
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var page = await JsonSerializer.DeserializeAsync<GraphChildrenPage>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (page?.Value is not null)
            {
                foreach (var item in page.Value)
                {
                    results.Add(new DriveChild
                    {
                        Id = item.Id,
                        Name = item.Name,
                        IsFolder = item.Folder is not null,
                        IsFile = item.File is not null,
                        Size = item.Size
                    });
                }
            }

            url = page?.OdataNextLink;
        }

        return results;
    }

    private async Task CollectDownloadJobsAsync(
        string cloudPath,
        string localPath,
        string sessionLocalRoot,
        List<DownloadJob> jobs,
        CancellationToken cancellationToken,
        SessionDownloadOptions? options = null)
    {
        List<DriveChild> children;
        try
        {
            children = await ListChildItemsAsync(cloudPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        Directory.CreateDirectory(localPath);
        var slim = options?.Role == CheckoutRole.Announcer;
        var underAppMeta = string.Equals(
            Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            PathService.AppMetaFolderName,
            StringComparison.OrdinalIgnoreCase);

        foreach (var item in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item.Name))
                continue;

            var childCloud = JoinDrivePath(cloudPath, item.Name);
            var childLocal = Path.Combine(localPath, item.Name);
            var relative = Path.GetRelativePath(sessionLocalRoot, childLocal);

            if (item.IsFolder)
            {
                if (slim)
                {
                    if (!AnnouncerDownloadPolicy.ShouldRecurse(relative))
                        continue;
                }
                else
                {
                    if (GraphSyncPolicy.ShouldSkipFolder(item.Name))
                        continue;
                    // ._checkin lock JSON only — Finish backup trees stay in the cloud until needed.
                    if (underAppMeta)
                        continue;
                }

                await CollectDownloadJobsAsync(
                        childCloud, childLocal, sessionLocalRoot, jobs, cancellationToken, options)
                    .ConfigureAwait(false);
            }
            else if (item.IsFile)
            {
                if (slim && !AnnouncerDownloadPolicy.ShouldIncludeFile(relative, options?.AnnouncerEventFile))
                    continue;
                jobs.Add(new DownloadJob(childCloud, childLocal, item.Name, item.Size));
            }
        }
    }

    private async Task DownloadFileWithGuardsAsync(
        DownloadJob job,
        AdaptiveTransferGate gate,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= GraphSyncPolicy.DownloadMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DownloadFileOnceAsync(job.CloudPath, job.LocalPath, job.Size, cancellationToken)
                    .ConfigureAwait(false);
                gate.NoteSuccess();
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex) when (IsTransientGraphFailure(ex, out var retryAfter))
            {
                last = ex;
                // Only shrink concurrency on real Graph throttle — network flakes just back off.
                if (ex.StatusCode is System.Net.HttpStatusCode.TooManyRequests
                    or System.Net.HttpStatusCode.ServiceUnavailable)
                    gate.NoteThrottle(retryAfter);
                if (attempt >= GraphSyncPolicy.DownloadMaxAttempts)
                    break;
                await Task.Delay(retryAfter ?? BackoffDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                last = ex;
                if (attempt >= GraphSyncPolicy.DownloadMaxAttempts)
                    break;
                await Task.Delay(BackoffDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                last = ex;
                break;
            }
        }

        throw last ?? new IOException("Download failed.");
    }

    /// <summary>GET one file. 404 returns false. Transient errors get one retry, then false.</summary>
    private async Task<bool> TryDownloadOptionalFileAsync(
        string cloudPath,
        string localPath,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DownloadFileOnceAsync(cloudPath, localPath, expectedSize: null, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return false;
            }
            catch (HttpRequestException ex) when (IsTransientGraphFailure(ex, out var retryAfter) && attempt < 2)
            {
                await Task.Delay(retryAfter ?? BackoffDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private async Task DownloadFileOnceAsync(
        string cloudPath,
        string localPath,
        long? expectedSize,
        CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var partial = localPath + ".partial";
        try
        {
            if (File.Exists(partial))
                File.Delete(partial);

            var url = ItemPathUrl(cloudPath, "content");
            using var req = await CreateRequestAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if ((int)resp.StatusCode is 429 or 503)
            {
                var retryAfter = ParseRetryAfter(resp);
                throw new HttpRequestException(
                    $"Graph throttled ({(int)resp.StatusCode}).",
                    null,
                    resp.StatusCode)
                {
                    Data = { ["RetryAfter"] = retryAfter }
                };
            }

            resp.EnsureSuccessStatusCode();
            await using (var src = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var dst = new FileStream(
                             partial,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await src.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);
                await dst.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var written = new FileInfo(partial).Length;
            if (expectedSize is long want && want > 0 && written != want)
            {
                File.Delete(partial);
                throw new IOException($"Size mismatch for {Path.GetFileName(localPath)}: got {written}, expected {want}.");
            }

            if (File.Exists(localPath))
                File.Delete(localPath);
            File.Move(partial, localPath);
        }
        finally
        {
            try
            {
                if (File.Exists(partial))
                    File.Delete(partial);
            }
            catch
            {
                // best-effort
            }
        }
    }

    private static bool IsTransientGraphFailure(HttpRequestException ex, out TimeSpan? retryAfter)
    {
        retryAfter = null;
        if (ex.Data["RetryAfter"] is TimeSpan ts)
            retryAfter = ts;
        if (ex.StatusCode is System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.ServiceUnavailable)
            return true;
        // No status (connection reset / DNS flake) — retry.
        return ex.StatusCode is null;
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage resp)
    {
        if (resp.Headers.RetryAfter?.Delta is { } delta)
            return ClampRetryAfter(delta);
        if (resp.Headers.RetryAfter?.Date is { } date)
            return ClampRetryAfter(date - DateTimeOffset.UtcNow);
        return null;
    }

    private static TimeSpan ClampRetryAfter(TimeSpan value)
    {
        if (value < TimeSpan.FromMilliseconds(200))
            return TimeSpan.FromMilliseconds(200);
        if (value > TimeSpan.FromSeconds(15))
            return TimeSpan.FromSeconds(15);
        return value;
    }

    private static TimeSpan BackoffDelay(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(4000, 250 * Math.Pow(2, attempt - 1)));

    private async Task<int> UploadTreeAsync(
        string localPath,
        string cloudPath,
        long maxFileBytes,
        IProgress<string>? status,
        CancellationToken cancellationToken,
        AdaptiveTransferGate gate,
        string? progressLabel = null)
    {
        if (!Directory.Exists(localPath))
            return 0;

        var jobs = new List<UploadJob>();
        CollectUploadJobs(localPath, cloudPath, maxFileBytes, progressLabel ?? "files", jobs);
        var result = await UploadJobsParallelAsync(jobs, gate, status, cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded;
    }

    private static void CollectUploadJobs(
        string localPath,
        string cloudPath,
        long maxFileBytes,
        string progressLabel,
        List<UploadJob> jobs)
    {
        if (!Directory.Exists(localPath))
            return;

        // ._checkin Finish backup trees stay local — only lock JSON at the meta root goes to Graph.
        var skipChildFolders = string.Equals(
            Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            PathService.AppMetaFolderName,
            StringComparison.OrdinalIgnoreCase);

        foreach (var dir in Directory.EnumerateDirectories(localPath))
        {
            if (skipChildFolders)
                continue;
            var name = Path.GetFileName(dir);
            if (GraphSyncPolicy.ShouldSkipFolder(name))
                continue;
            if (name.EndsWith(PathService.AutosaveTombstoneSuffix, StringComparison.OrdinalIgnoreCase))
                continue;

            CollectUploadJobs(dir, JoinDrivePath(cloudPath, name), maxFileBytes, progressLabel, jobs);
        }

        foreach (var file in Directory.EnumerateFiles(localPath))
        {
            if (GraphSyncPolicy.ShouldSkipFile(file, maxFileBytes))
                continue;
            jobs.Add(new UploadJob(file, JoinDrivePath(cloudPath, Path.GetFileName(file)!), progressLabel));
        }
    }

    private async Task<UploadBatchResult> UploadJobsParallelAsync(
        IReadOnlyList<UploadJob> jobs,
        AdaptiveTransferGate gate,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        if (jobs.Count == 0)
            return new UploadBatchResult(0, 0, 0);

        var succeeded = 0;
        var failed = 0;
        var eventFailed = 0;

        var tasks = jobs.Select(async job =>
        {
            await using (await gate.AcquireAsync(cancellationToken).ConfigureAwait(false))
            {
                var isEvent = job.ProgressLabel.Contains("timing", StringComparison.OrdinalIgnoreCase)
                              || job.LocalPath.EndsWith(".event", StringComparison.OrdinalIgnoreCase);
                try
                {
                    await UploadFileWithGuardsAsync(job, gate, cancellationToken).ConfigureAwait(false);
                    var n = Interlocked.Increment(ref succeeded);
                    status?.Report(isEvent
                        ? "finish:upload-progress:timing file"
                        : $"finish:upload-progress:{job.ProgressLabel}:{n}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    Interlocked.Increment(ref failed);
                    if (isEvent)
                        Interlocked.Increment(ref eventFailed);
                }
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return new UploadBatchResult(succeeded, failed, eventFailed);
    }

    private async Task UploadFileWithGuardsAsync(
        UploadJob job,
        AdaptiveTransferGate gate,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= GraphSyncPolicy.UploadMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await UploadFileAsync(job.LocalPath, job.CloudPath, cancellationToken).ConfigureAwait(false);
                gate.NoteSuccess();
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex) when (IsTransientGraphFailure(ex, out var retryAfter))
            {
                last = ex;
                if (ex.StatusCode is System.Net.HttpStatusCode.TooManyRequests
                    or System.Net.HttpStatusCode.ServiceUnavailable)
                    gate.NoteThrottle(retryAfter);
                if (attempt >= GraphSyncPolicy.UploadMaxAttempts)
                    break;
                await Task.Delay(retryAfter ?? BackoffDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                last = ex;
                if (attempt >= GraphSyncPolicy.UploadMaxAttempts)
                    break;
                await Task.Delay(BackoffDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                last = ex;
                break;
            }
        }

        throw last ?? new IOException("Upload failed.");
    }

    /// <summary>
    /// OneDrive folder-as-ZIP. Returns file count on success, null if ZIP is unsupported / failed
    /// (caller should fall back to per-file download).
    /// </summary>
    private async Task<int?> TryDownloadFolderAsZipAsync(
        string cloudPath,
        string localFolder,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        var zipPath = Path.Combine(Path.GetTempPath(), "s2f-pack-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var url = ItemPathUrl(cloudPath, "content");
            url += url.Contains('?', StringComparison.Ordinal) ? "&format=zip" : "?format=zip";

            status?.Report("Downloading race pack ZIP…");
            using var req = await CreateRequestAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (resp.StatusCode is System.Net.HttpStatusCode.BadRequest
                or System.Net.HttpStatusCode.NotFound
                or System.Net.HttpStatusCode.MethodNotAllowed)
                return null;
            resp.EnsureSuccessStatusCode();

            await using (var src = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var dst = File.Create(zipPath))
                await src.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);

            status?.Report("Unpacking race pack…");
            Directory.CreateDirectory(localFolder);
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, localFolder, overwriteFiles: true);

            // OneDrive sometimes nests entries under the folder name — flatten one level if needed.
            FlattenSingleRootZipIfNeeded(localFolder);

            return Directory.Exists(localFolder)
                ? Directory.EnumerateFiles(localFolder, "*", SearchOption.AllDirectories).Count()
                : 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(zipPath))
                    File.Delete(zipPath);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void FlattenSingleRootZipIfNeeded(string localFolder)
    {
        try
        {
            var kids = Directory.GetFileSystemEntries(localFolder);
            if (kids.Length != 1 || !Directory.Exists(kids[0]))
                return;
            var nested = kids[0];
            var nestedName = Path.GetFileName(nested);
            var parentName = Path.GetFileName(localFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.Equals(nestedName, parentName, StringComparison.OrdinalIgnoreCase))
                return;

            foreach (var entry in Directory.GetFileSystemEntries(nested))
            {
                var dest = Path.Combine(localFolder, Path.GetFileName(entry));
                if (Directory.Exists(entry))
                {
                    if (Directory.Exists(dest))
                        continue;
                    Directory.Move(entry, dest);
                }
                else
                {
                    if (File.Exists(dest))
                        File.Delete(dest);
                    File.Move(entry, dest);
                }
            }

            try { Directory.Delete(nested, recursive: true); } catch { /* ignore */ }
        }
        catch
        {
            // leave as extracted
        }
    }

    private static void StripSkippedFoldersAfterUnpack(string librarySessionPath)
    {
        foreach (var name in GraphSyncPolicy.SkipFolderNames)
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(
                             librarySessionPath, name, SearchOption.AllDirectories))
                {
                    try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private async Task UploadFileAsync(string localPath, string cloudPath, CancellationToken cancellationToken)
    {
        var info = new FileInfo(localPath);
        if (info.Length > 3_800_000)
        {
            await UploadLargeFileAsync(localPath, cloudPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        var url = ItemPathUrl(cloudPath, "content");
        await using var stream = File.OpenRead(localPath);
        using var req = await CreateRequestAsync(HttpMethod.Put, url, cancellationToken).ConfigureAwait(false);
        req.Content = new StreamContent(stream);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if ((int)resp.StatusCode is 429 or 503)
        {
            var retryAfter = ParseRetryAfter(resp);
            throw new HttpRequestException(
                $"Graph throttled upload ({(int)resp.StatusCode}).",
                null,
                resp.StatusCode)
            {
                Data = { ["RetryAfter"] = retryAfter }
            };
        }

        resp.EnsureSuccessStatusCode();
    }

    private async Task UploadLargeFileAsync(string localPath, string cloudPath, CancellationToken cancellationToken)
    {
        var sessionUrl = ItemPathUrl(cloudPath, "createUploadSession");
        using var sessionReq = await CreateRequestAsync(HttpMethod.Post, sessionUrl, cancellationToken)
            .ConfigureAwait(false);
        sessionReq.Content = new StringContent(
            """{"item":{"@microsoft.graph.conflictBehavior":"replace"}}""",
            Encoding.UTF8,
            "application/json");
        using var sessionResp = await _http.SendAsync(sessionReq, cancellationToken).ConfigureAwait(false);
        sessionResp.EnsureSuccessStatusCode();
        await using var sessionStream = await sessionResp.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var session = await JsonSerializer.DeserializeAsync<UploadSessionResponse>(sessionStream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(session?.UploadUrl))
            throw new InvalidOperationException("Could not start large file upload session.");

        var fileInfo = new FileInfo(localPath);
        const int fragmentSize = 320 * 1024 * 10;
        await using var fileStream = File.OpenRead(localPath);
        long offset = 0;
        while (offset < fileInfo.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = fileInfo.Length - offset;
            var chunk = (int)Math.Min(fragmentSize, remaining);
            var buffer = new byte[chunk];
            var read = await fileStream.ReadAsync(buffer.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                break;

            using var content = new ByteArrayContent(buffer, 0, read);
            content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + read - 1, fileInfo.Length);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var put = new HttpRequestMessage(HttpMethod.Put, session.UploadUrl) { Content = content };
            using var response = await _http.SendAsync(put, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            offset += read;
        }
    }

    /// <summary>
    /// Delete the folder in one Graph call. Falls back to per-child deletes if the folder
    /// DELETE is rejected (item missing / not a folder).
    /// </summary>
    private async Task DeleteRemoteFolderAsync(string cloudPath, CancellationToken cancellationToken)
    {
        try
        {
            // Trailing ':' required for path-addressed item ops (no :/content facet).
            var url = ItemPathUrl(cloudPath);
            if (!url.EndsWith(':'))
                url += ":";
            using var req = await CreateRequestAsync(HttpMethod.Delete, url, cancellationToken)
                .ConfigureAwait(false);
            using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode
                || resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Fall through to children.
        }

        await DeleteRemoteChildrenAsync(cloudPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteRemoteChildrenAsync(string cloudPath, CancellationToken cancellationToken)
    {
        List<DriveChild> children;
        try
        {
            children = await ListChildItemsAsync(cloudPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        // Parallel deletes — still a fallback path; primary is DeleteRemoteFolderAsync.
        using var gate = new SemaphoreSlim(Math.Min(8, Math.Max(1, children.Count)));
        var tasks = children.Select(async child =>
        {
            if (string.IsNullOrWhiteSpace(child.Id))
                return;
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var url = $"{GraphRoot}/me/drive/items/{Uri.EscapeDataString(child.Id)}";
                using var req = await CreateRequestAsync(HttpMethod.Delete, url, cancellationToken)
                    .ConfigureAwait(false);
                using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Best effort
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public static string? ToDriveRelative(string localPath, string libraryRoot, string graphDriveRoot)
    {
        if (string.IsNullOrWhiteSpace(localPath) || string.IsNullOrWhiteSpace(libraryRoot))
            return null;

        var fullLocal = Path.GetFullPath(localPath);
        var fullRoot = Path.GetFullPath(libraryRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!fullLocal.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            return null;

        var underLibrary = fullLocal.Length == fullRoot.Length
            ? ""
            : fullLocal[(fullRoot.Length + 1)..].Replace('\\', '/');

        var driveRoot = NormalizeDrivePath(graphDriveRoot);
        return string.IsNullOrEmpty(driveRoot)
            ? underLibrary
            : string.IsNullOrEmpty(underLibrary)
                ? driveRoot
                : driveRoot + "/" + underLibrary;
    }

    public static string NormalizeDrivePath(string? graphDriveRoot)
    {
        if (string.IsNullOrWhiteSpace(graphDriveRoot))
            return "";
        return graphDriveRoot.Replace('\\', '/').Trim().Trim('/');
    }

    public static string JoinDrivePath(string left, string right)
    {
        var l = NormalizeDrivePath(left);
        var r = right.Replace('\\', '/').Trim('/');
        if (string.IsNullOrEmpty(l))
            return r;
        if (string.IsNullOrEmpty(r))
            return l;
        return l + "/" + r;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private sealed record DownloadJob(string CloudPath, string LocalPath, string Name, long? Size);

    private sealed record UploadJob(string LocalPath, string CloudPath, string ProgressLabel);

    private readonly record struct UploadBatchResult(int Succeeded, int Failed, int EventFailed);

    /// <summary>
    /// Starts at full parallelism; on Graph 429/503 holds permits down to the floor and
    /// applies a shared cooldown so other workers wait. Successes release held permits back toward max.
    /// Shared by session download and Finish / mid-race upload.
    /// </summary>
    private sealed class AdaptiveTransferGate : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate;
        private readonly int _max;
        private readonly int _floor;
        private readonly object _sync = new();
        private int _held;
        private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;

        public AdaptiveTransferGate(int max, int floor)
        {
            _max = Math.Max(1, max);
            _floor = Math.Clamp(floor, 1, _max);
            _gate = new SemaphoreSlim(_max, _max);
        }

        public async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan wait;
                lock (_sync)
                    wait = _cooldownUntil - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);

                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

                lock (_sync)
                {
                    if (DateTimeOffset.UtcNow < _cooldownUntil)
                    {
                        _gate.Release();
                        continue;
                    }
                }

                return new GateLease(_gate);
            }
        }

        public void NoteThrottle(TimeSpan? retryAfter)
        {
            var delay = ClampRetryAfter(retryAfter ?? TimeSpan.FromSeconds(2));
            lock (_sync)
            {
                var until = DateTimeOffset.UtcNow + delay;
                if (until > _cooldownUntil)
                    _cooldownUntil = until;

                // Hold permits so effective concurrency drops toward floor (keep 8 when healthy).
                var targetHeld = _max - _floor;
                while (_held < targetHeld && _gate.Wait(0))
                    _held++;
            }
        }

        public void NoteSuccess()
        {
            lock (_sync)
            {
                if (_held <= 0)
                    return;
                if (DateTimeOffset.UtcNow < _cooldownUntil)
                    return;
                _held--;
                _gate.Release();
            }
        }

        public ValueTask DisposeAsync()
        {
            _gate.Dispose();
            return ValueTask.CompletedTask;
        }

        private sealed class GateLease : IAsyncDisposable
        {
            private SemaphoreSlim? _gate;

            public GateLease(SemaphoreSlim gate) => _gate = gate;

            public ValueTask DisposeAsync()
            {
                var g = Interlocked.Exchange(ref _gate, null);
                g?.Release();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class DriveChild
    {
        public string? Id { get; init; }
        public string? Name { get; init; }
        public bool IsFolder { get; init; }
        public bool IsFile { get; init; }
        public long? Size { get; init; }
    }

    private sealed class GraphChildrenPage
    {
        public List<GraphDriveItem>? Value { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("@odata.nextLink")]
        public string? OdataNextLink { get; set; }
    }

    private sealed class GraphDriveItem
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public long? Size { get; set; }
        public object? Folder { get; set; }
        public object? File { get; set; }
    }

    private sealed class UploadSessionResponse
    {
        public string? UploadUrl { get; set; }
    }
}
