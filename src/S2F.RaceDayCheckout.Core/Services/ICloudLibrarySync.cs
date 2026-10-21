namespace S2F.RaceDayCheckout.Core.Services;

public sealed class CloudSyncResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public int FilesTransferred { get; init; }

    /// <summary>
    /// Session-relative paths that failed during Finish upload (partial failure).
    /// Empty on full success or when the whole planned set should be retried (timeout / hard fail).
    /// </summary>
    public IReadOnlyList<string> FailedRelativePaths { get; init; } = Array.Empty<string>();
}

/// <summary>
/// App-owned sync between local <c>LibraryRoot</c> and personal OneDrive via Microsoft Graph.
/// Race-day laptops do not use the OneDrive desktop client — Graph is the only cloud path.
/// </summary>
public interface ICloudLibrarySync
{
    /// <summary>True when a Graph token is available (signed in).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// List remote <b>office</b> folders only and ensure empty local office dirs exist.
    /// Does not walk events (cellular — one level at a time).
    /// </summary>
    Task<CloudSyncResult> RefreshOfficeListAsync(
        string libraryRoot,
        string graphDriveRoot,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Index session leaves under one office (placeholders with RUNSCORE) so
    /// <see cref="LibraryScanner"/> can list events for that office only.
    /// </summary>
    Task<CloudSyncResult> RefreshOfficeSessionsAsync(
        string libraryRoot,
        string graphDriveRoot,
        string officeName,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default,
        string? protectSessionPath = null);

    /// <summary>
    /// List <b>one</b> folder’s children under the library (cellular — no recursion).
    /// Creates matching empty local dirs. When <paramref name="probeSessionLeaves"/> is true,
    /// also creates RUNSCORE placeholders for children that are session leaves on OneDrive.
    /// </summary>
    Task<CloudSyncResult> RefreshFolderChildrenAsync(
        string libraryRoot,
        string graphDriveRoot,
        string relativeUnderLibrary,
        bool probeSessionLeaves = false,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Download a session pack into the local library path.
    /// Timing (default): RUNSCORE, .event, ._checkin files, DB's. Skips _autosave.
    /// Reclaim (<see cref="SessionDownloadOptions.ForReclaimAutosaveOverlay"/>): only RUNSCORE\_autosave.
    /// Announcer (<see cref="SessionDownloadOptions"/>): assigned .event + DB's only.
    /// </summary>
    Task<CloudSyncResult> DownloadSessionAsync(
        string librarySessionPath,
        string libraryRoot,
        string graphDriveRoot,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default,
        SessionDownloadOptions? options = null);

    /// <summary>
    /// Download only <c>._checkin/s2f_session.json</c> for the given session leaves (parallel).
    /// 404 means that race has no assignment yet. Does not pull race files.
    /// </summary>
    Task<CloudSyncResult> DownloadSessionRolesAsync(
        IReadOnlyList<string> librarySessionPaths,
        string libraryRoot,
        string graphDriveRoot,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default);

    /// <summary>Name-only <c>*.event</c> children of one session folder. No file bytes.</summary>
    Task<IReadOnlyList<string>> ListRemoteEventFileNamesAsync(
        string librarySessionPath,
        string libraryRoot,
        string graphDriveRoot,
        CancellationToken cancellationToken = default);

    /// <summary>Upload <c>._checkin/s2f_session.json</c> only.</summary>
    Task<CloudSyncResult> UploadSessionRolesAsync(
        string librarySessionPath,
        string libraryRoot,
        string graphDriveRoot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upload scoped race files from the local library session to OneDrive.
    /// When <paramref name="onlyRelativePaths"/> is set (Finish), only those session-relative
    /// files are uploaded (+ lock metadata). Empty list = locks only (no race changes).
    /// </summary>
    Task<CloudSyncResult> UploadSessionAsync(
        string librarySessionPath,
        string libraryRoot,
        string graphDriveRoot,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? onlyRelativePaths = null);

    /// <summary>Upload a local folder tree to the matching cloud path (e.g. _autosave or DB's).</summary>
    Task<CloudSyncResult> UploadFolderAsync(
        string localFolderPath,
        string libraryRoot,
        string graphDriveRoot,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upload specific local files under LibraryRoot (mid-race: only paths just mirrored).
    /// Skips paths outside the library or over MidRaceMaxFileBytes.
    /// </summary>
    Task<CloudSyncResult> UploadLocalFilesAsync(
        IReadOnlyList<string> localFilePaths,
        string libraryRoot,
        string graphDriveRoot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upload only <c>._checkin/_checkout.json</c> (+ history) — heartbeat / checkout acquire.
    /// Never walks Finish backup trees under the meta folder.
    /// </summary>
    Task<CloudSyncResult> UploadCheckoutLockAsync(
        string librarySessionPath,
        string libraryRoot,
        string graphDriveRoot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Download only <c>._checkin/_checkout.json</c> into the local Library session (overwrite).
    /// Used before heartbeat / mirror so a sleeping laptop cannot re-upload a stale lock after soft takeover.
    /// 404 leaves local lock unchanged and returns Success with 0 files.
    /// </summary>
    Task<CloudSyncResult> DownloadCheckoutLockAsync(
        string librarySessionPath,
        string libraryRoot,
        string graphDriveRoot,
        CancellationToken cancellationToken = default);

    /// <summary>Delete all files under a remote folder (used to clear cloud _autosave after Finish).</summary>
    Task<CloudSyncResult> ClearRemoteFolderAsync(
        string localFolderPath,
        string libraryRoot,
        string graphDriveRoot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Move a top-level live-office folder (flat race or whole Race Group) into
    /// <c>Past Races/{Office} Past Races/{Year}/{item}</c> on OneDrive, then reconcile local Library.
    /// </summary>
    Task<CloudSyncResult> MoveOfficeItemToPastRacesAsync(
        string libraryRoot,
        string graphDriveRoot,
        string officeName,
        string itemFolderName,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default);
}

public sealed class NullCloudLibrarySync : ICloudLibrarySync
{
    public bool IsAvailable => false;

    public Task<CloudSyncResult> RefreshOfficeListAsync(
        string libraryRoot, string graphDriveRoot, IProgress<string>? status = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CloudSyncResult { Success = true, Message = "Graph not signed in." });

    public Task<CloudSyncResult> RefreshOfficeSessionsAsync(
        string libraryRoot, string graphDriveRoot, string officeName, IProgress<string>? status = null, CancellationToken cancellationToken = default, string? protectSessionPath = null) =>
        Task.FromResult(new CloudSyncResult { Success = true, Message = "Graph not signed in." });

    public Task<CloudSyncResult> RefreshFolderChildrenAsync(
        string libraryRoot, string graphDriveRoot, string relativeUnderLibrary, bool probeSessionLeaves = false, IProgress<string>? status = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CloudSyncResult { Success = true, Message = "Graph not signed in." });

    public Task<CloudSyncResult> DownloadSessionAsync(
        string librarySessionPath, string libraryRoot, string graphDriveRoot, IProgress<string>? status = null, CancellationToken cancellationToken = default, SessionDownloadOptions? options = null) =>
        Task.FromResult(new CloudSyncResult { Success = true, Message = "Graph not signed in — using local library." });

    public Task<CloudSyncResult> DownloadSessionRolesAsync(
        IReadOnlyList<string> librarySessionPaths, string libraryRoot, string graphDriveRoot, IProgress<string>? status = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CloudSyncResult { Success = true, Message = "Graph not signed in." });

    public Task<IReadOnlyList<string>> ListRemoteEventFileNamesAsync(
        string librarySessionPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<CloudSyncResult> UploadSessionRolesAsync(
        string librarySessionPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CloudSyncResult { Success = true, Message = "Graph not signed in — saved on this laptop." });

    public Task<CloudSyncResult> UploadSessionAsync(
        string librarySessionPath, string libraryRoot, string graphDriveRoot, IProgress<string>? status = null, CancellationToken cancellationToken = default, IReadOnlyList<string>? onlyRelativePaths = null) =>
        Task.FromResult(new CloudSyncResult { Success = false, Message = "Sign in to OneDrive to upload race files." });

    public Task<CloudSyncResult> UploadFolderAsync(
        string localFolderPath, string libraryRoot, string graphDriveRoot, IProgress<string>? status = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CloudSyncResult { Success = true, Message = "Graph not signed in." });

    public Task<CloudSyncResult> UploadLocalFilesAsync(
        IReadOnlyList<string> localFilePaths, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CloudSyncResult { Success = true, Message = "Graph not signed in." });

    public Task<CloudSyncResult> UploadCheckoutLockAsync(
        string librarySessionPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CloudSyncResult { Success = true, Message = "Graph not signed in." });

    public Task<CloudSyncResult> DownloadCheckoutLockAsync(
        string librarySessionPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CloudSyncResult { Success = true, Message = "Graph not signed in." });

    public Task<CloudSyncResult> ClearRemoteFolderAsync(
        string localFolderPath, string libraryRoot, string graphDriveRoot, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CloudSyncResult { Success = true, Message = "Graph not signed in." });

    public Task<CloudSyncResult> MoveOfficeItemToPastRacesAsync(
        string libraryRoot,
        string graphDriveRoot,
        string officeName,
        string itemFolderName,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CloudSyncResult { Success = false, Message = "Sign in to OneDrive first." });
}
