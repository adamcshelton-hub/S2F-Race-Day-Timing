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

    Task MirrorNewerFilesAsync(
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

        await CopyRecursiveAsync(source, destination, onlyNewer: false, progress, cancellationToken);
    }

    public async Task MirrorNewerFilesAsync(
        string source,
        string destination,
        IProgress<CopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(source))
            return;

        Directory.CreateDirectory(destination);
        await CopyRecursiveAsync(source, destination, onlyNewer: true, progress, cancellationToken);
    }

    private static async Task<bool> TryRobocopyAsync(string source, string destination, bool mirror, CancellationToken ct)
    {
        try
        {
            // Never bulk-copy the live autosave mirror folder as part of a full tree replace.
            var args =
                $"\"{source}\" \"{destination}\" /E /R:2 /W:2 /NFL /NDL /NJH /NJS /NP /XD \"{PathService.AutosaveFolderName}\" \"{PathService.AppMetaFolderName}\" \"._checkin_backup\"";
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

    private static async Task CopyRecursiveAsync(
        string source,
        string destination,
        bool onlyNewer,
        IProgress<CopyProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stats = new CopyProgress();
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

            await using (var src = File.OpenRead(file))
            await using (var dst = File.Create(destFile))
            {
                await src.CopyToAsync(dst, cancellationToken);
                stats.BytesCopied += src.Length;
            }

            File.SetLastWriteTimeUtc(destFile, File.GetLastWriteTimeUtc(file));
            stats.FilesCopied++;
            stats.CurrentFile = relative;
            progress?.Report(new CopyProgress
            {
                FilesCopied = stats.FilesCopied,
                BytesCopied = stats.BytesCopied,
                CurrentFile = stats.CurrentFile
            });
        }
    }

    private static bool IsAppMetaRelative(string relative) =>
        IsExcludedTree(relative, PathService.AppMetaFolderName)
        || IsExcludedTree(relative, PathService.AutosaveFolderName)
        || IsExcludedTree(relative, "._checkin_backup");

    private static bool IsExcludedTree(string relative, string folderName) =>
        relative.Equals(folderName, StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith(folderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith(folderName + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
