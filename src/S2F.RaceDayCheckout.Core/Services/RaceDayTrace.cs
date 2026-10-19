using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// Checkout-lock problem log. Healthy heartbeats are not written.
/// File: %AppData%\S2FRaceDayCheckout\lock-trace.log
/// </summary>
public static class RaceDayTrace
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2_000_000;

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "S2FRaceDayCheckout",
        "lock-trace.log");

    public static void Write(string message)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {OneLine(message)}{Environment.NewLine}";
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                {
                    File.Copy(LogPath, LogPath + ".previous", overwrite: true);
                    File.Delete(LogPath);
                }

                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // Diagnosis only — never affect checkout.
        }
    }

    public static string FormatLock(CheckoutLock? lockInfo)
    {
        if (lockInfo is null)
            return "none";

        return
            $"status={lockInfo.Status} machineId={lockInfo.MachineId} machine={lockInfo.MachineName} " +
            $"holder={lockInfo.HolderName} user={lockInfo.WindowsUser} " +
            $"lastSeen={lockInfo.LastSeenAt:yyyy-MM-ddTHH:mm:ssZ} checkedOut={lockInfo.CheckedOutAt:yyyy-MM-ddTHH:mm:ssZ}";
    }

    private static string OneLine(string message) =>
        message.Replace('\r', ' ').Replace('\n', ' ');
}
