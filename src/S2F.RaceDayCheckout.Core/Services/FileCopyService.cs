using System.Diagnostics;

namespace S2F.RaceDayCheckout.Core.Services;

public sealed class CopyProgress
{
    public long FilesCopied { get; set; }
    public long BytesCopied { get; set; }
    public string? CurrentFile { get; set; }
}

public interface IFileCopyService
{
    Task CopyDirectoryAsync(
        string source,
        string destination,
        IProgress<CopyProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <returns>Absolute destination paths that were copied (empty = nothing newer).</returns>
    Task<IReadOnlyList<string>> MirrorNewerFilesAsync(
        string source,
        string destination,
        IProgress<CopyProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Always copy every file from source over destination (reclaim _autosave overlay).
    /// Ignores mtimes — Graph downloads stamp local files with "now", so newer-only would skip.
    /// </summary>
    Task<IReadOnlyList<string>> OverlayAllFilesAsync(
        string source,
        string destination,
        IProgress<CopyProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class FileCopyService : IFileCopyService
{
    private static readonly HashSet<string> SkipFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "_checkout.json",
        "_checkout_history.jsonl"
    };

    public async Task CopyDirectoryAsync(
        string source,
        string destination,
        IProgress<CopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Source not found: {source}");

        Directory.CreateDirectory(destination);

        if (OperatingSystem.IsWindows() && await TryRobocopyAsync(source, destination, mirror: false, cancellationToken))
            return;

        await CopyRecursiveAsync(source, destination, onlyNewer: false, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> MirrorNewerFilesAsync(
        string source,
        string destination,
        IProgress<CopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(source))
            return Array.Empty<string>();

        Directory.CreateDirectory(destination);
        var (_, paths) = await CopyRecursiveAsync(source, destination, onlyNewer: true, progress, cancellationToken);
        return paths;
    }

    public async Task<IReadOnlyList<string>> OverlayAllFilesAsync(
        string source,
        string destination,
        IProgress<CopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(source))
            return Array.Empty<string>();

        Directory.CreateDirectory(destination);
        var (_, paths) = await CopyRecursiveAsync(source, destination, onlyNewer: false, progress, cancellationToken);
        return paths;
    }

    private static async Task<bool> TryRobocopyAsync(string source, string destination, bool mirror, CancellationToken ct)
    {
        try
        {
            // Never bulk-copy the live autosave mirror folder as part of a full tree replace.
            var tombstone = PathService.AutosaveFolderName + PathService.AutosaveTombstoneSuffix;
            var args =
                $"\"{source}\" \"{destination}\" /E /R:2 /W:2 /NFL /NDL /NJH /NJS /NP /XD \"{PathService.AutosaveFolderName}\" \"{tombstone}\" \"{PathService.AppMetaFolderName}\" \"._checkin_backup\"";
            if (mirror)
                args += " /XO"; // exclude older

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "robocopy",
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.Start();
            await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            // robocopy exit codes 0-7 are success-ish
            return process.ExitCode < 8;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(int Count, IReadOnlyList<string> DestPaths)> CopyRecursiveAsync(
        string source,
        string destination,
        bool onlyNewer,
        IProgress<CopyProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stats = new CopyProgress();
        var copiedPaths = new List<string>();
        Directory.CreateDirectory(destination);

        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, dir);
            // Skip app metadata (locks / finish backups) during full replace copy
            if (IsAppMetaRelative(relative))
                continue;
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            if (IsAppMetaRelative(relative))
                continue;
            var fileName = Path.GetFileName(file);
            if (SkipFileNames.Contains(fileName))
                continue;
            var destFile = Path.Combine(destination, relative);
            var destDir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            if (onlyNewer && File.Exists(destFile))
            {
                var srcInfo = new FileInfo(file);
                var dstInfo = new FileInfo(destFile);
                if (srcInfo.LastWriteTimeUtc <= dstInfo.LastWriteTimeUtc && srcInfo.Length == dstInfo.Length)
                    continue;
            }

            // Overlay/reclaim: Entries.DTA is often locked by RSServer — retry, then skip that
            // file so the rest of the overlay still applies (do not abort the whole reclaim).
            if (!await TryCopyFileWithRetriesAsync(file, destFile, cancellationToken).ConfigureAwait(false))
                continue;

            stats.BytesCopied += new FileInfo(destFile).Length;
            stats.FilesCopied++;
            stats.CurrentFile = relative;
            copiedPaths.Add(destFile);
            progress?.Report(new CopyProgress
            {
                FilesCopied = stats.FilesCopied,
                BytesCopied = stats.BytesCopied,
                CurrentFile = stats.CurrentFile
            });
        }

        return ((int)stats.FilesCopied, copiedPaths);
    }

    private static async Task<bool> TryCopyFileWithRetriesAsync(
        string sourceFile,
        string destFile,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Write via temp + replace so a locked destination is less likely mid-stream,
                // and a partial write cannot leave a truncated Entries.DTA.
                var destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);

                var temp = destFile + ".s2f-tmp";
                try
                {
                    if (File.Exists(temp))
                    {
                        File.SetAttributes(temp, FileAttributes.Normal);
                        File.Delete(temp);
                    }

                    await using (var src = File.OpenRead(sourceFile))
                    await using (var dst = File.Create(temp))
                    {
                        await src.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);
                    }

                    File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(sourceFile));

                    if (File.Exists(destFile))
                    {
                        File.SetAttributes(destFile, FileAttributes.Normal);
                        File.Replace(temp, destFile, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    }
                    else
                    {
                        File.Move(temp, destFile);
                    }

                    return true;
                }
                finally
                {
                    try
                    {
                        if (File.Exists(temp))
                        {
                            File.SetAttributes(temp, FileAttributes.Normal);
                            File.Delete(temp);
                        }
                    }
                    catch
                    {
                        // best-effort
                    }
                }
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsAppMetaRelative(string relative) =>
        IsExcludedTree(relative, PathService.AppMetaFolderName)
        || IsExcludedTree(relative, PathService.AutosaveFolderName)
        || IsExcludedTree(relative, PathService.AutosaveFolderName + PathService.AutosaveTombstoneSuffix)
        || IsExcludedTree(relative, "._checkin_backup");

    private static bool IsExcludedTree(string relative, string folderName) =>
        relative.Equals(folderName, StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith(folderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith(folderName + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
