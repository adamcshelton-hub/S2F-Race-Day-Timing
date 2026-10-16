namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Forces OneDrive Files On-Demand placeholders to download (hydrate) before we copy.
/// Reading the full file content triggers Windows/OneDrive to pull bytes locally.
/// </summary>
public interface IOneDriveHydrationService
{
    Task HydrateFileAsync(string filePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    Task HydrateDirectoryAsync(string directoryPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

public sealed class OneDriveHydrationService : IOneDriveHydrationService
{
    // Win32 attributes used by OneDrive / Cloud Files placeholders
    private const int FileAttributeOffline = 0x00001000;
    private const int FileAttributeRecallOnOpen = 0x00040000;
    private const int FileAttributeRecallOnDataAccess = 0x00400000;

    public async Task HydrateDirectoryAsync(
        string directoryPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directoryPath))
            return;

        foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await HydrateFileAsync(file, progress, cancellationToken);
        }
    }

    public async Task HydrateFileAsync(
        string filePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            return;

        if (!LooksCloudOnly(filePath))
            return;

        progress?.Report($"Downloading from OneDrive… {Path.GetFileName(filePath)}");

        var buffer = new byte[1024 * 256];
        // Full read forces hydration of the placeholder.
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: buffer.Length,
            options: FileOptions.SequentialScan | FileOptions.Asynchronous);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0)
                break;
        }
    }

    /// <summary>True when the file looks like an unhydrated OneDrive / cloud placeholder.</summary>
    public static bool LooksCloudOnly(string filePath)
    {
        try
        {
            var attrs = (int)File.GetAttributes(filePath);
            return (attrs & (FileAttributeOffline | FileAttributeRecallOnOpen | FileAttributeRecallOnDataAccess)) != 0;
        }
        catch
        {
            return false;
        }
    }
}
