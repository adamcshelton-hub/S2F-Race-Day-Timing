using System.Diagnostics;
using S2F.RaceDayCheckout.Core.Models;

namespace S2F.RaceDayCheckout.Core.Services;

public sealed class LaunchResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class VendorLauncher
{
    private static readonly string[] RunScoreExeNames = ["rsserver.exe", "RSServer.exe", "RunScore.exe"];
    private static readonly string[] RaceFileNames = ["ENTRIES.FRM", "entries.frm", "ENTRIES.DTA"];

    public LaunchResult LaunchRunScore(ActiveCheckout active, AppSettings settings)
    {
        var runScoreDir = Path.Combine(active.ActivePath, PathService.RunScoreFolderName);
        if (!Directory.Exists(runScoreDir))
            return new LaunchResult { Success = false, Message = $"Active RUNSCORE missing: {runScoreDir}" };

        var raceFile = FindRaceFile(runScoreDir);
        var exe = ResolveRunScoreExe(settings);
        if (exe is null)
        {
            // Fall back: open the folder so staff can double-click
            try
            {
                Process.Start(new ProcessStartInfo { FileName = runScoreDir, UseShellExecute = true });
                return new LaunchResult
                {
                    Success = true,
                    Message = "RunScore exe not found — opened Active RUNSCORE folder. Set RunScore path in Settings for one-click launch."
                };
            }
            catch (Exception ex)
            {
                return new LaunchResult { Success = false, Message = ex.Message };
            }
        }

        try
        {
            var args = raceFile is null
                ? $"\"{runScoreDir}\""
                : $"\"{Path.GetFileName(raceFile)}\" \"{runScoreDir}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = runScoreDir,
                UseShellExecute = true
            });
            return new LaunchResult
            {
                Success = true,
                Message = raceFile is null
                    ? $"Launched RunScore. Open the race under {runScoreDir}."
                    : $"Launched RunScore with {Path.GetFileName(raceFile)}."
            };
        }
        catch (Exception ex)
        {
            return new LaunchResult { Success = false, Message = "Failed to launch RunScore: " + ex.Message };
        }
    }

    public LaunchResult LaunchTimingAndScoring(ActiveCheckout active, AppSettings settings)
    {
        var eventPath = Path.Combine(active.ActivePath, active.PrimaryEventFile);
        if (!File.Exists(eventPath))
            return new LaunchResult { Success = false, Message = $"Primary .event missing: {eventPath}" };

        try
        {
            if (!string.IsNullOrWhiteSpace(settings.TimingAndScoringExePath)
                && File.Exists(settings.TimingAndScoringExePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = settings.TimingAndScoringExePath,
                    Arguments = $"\"{eventPath}\"",
                    UseShellExecute = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = eventPath,
                    UseShellExecute = true
                });
            }

            return new LaunchResult
            {
                Success = true,
                Message = $"Launched Timing & Scoring with {active.PrimaryEventFile}."
            };
        }
        catch (Exception ex)
        {
            return new LaunchResult { Success = false, Message = "Failed to launch T&S: " + ex.Message };
        }
    }

    private static string? FindRaceFile(string runScoreDir)
    {
        foreach (var name in RaceFileNames)
        {
            var path = Path.Combine(runScoreDir, name);
            if (File.Exists(path))
                return path;
        }

        return Directory.GetFiles(runScoreDir, "*.FRM").FirstOrDefault()
               ?? Directory.GetFiles(runScoreDir, "*.frm").FirstOrDefault();
    }

    private static string? ResolveRunScoreExe(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.RunScoreExePath) && File.Exists(settings.RunScoreExePath))
            return settings.RunScoreExePath;

        var candidates = new List<string>();
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var root in new[] { pf, pf86, @"C:\RunScore", @"C:\Program Files\RunScore", @"C:\Program Files (x86)\RunScore" })
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;
            foreach (var name in RunScoreExeNames)
                candidates.Add(Path.Combine(root, name));
            try
            {
                candidates.AddRange(Directory.EnumerateFiles(root, "rsserver.exe", SearchOption.AllDirectories).Take(3));
            }
            catch
            {
                // permission
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}
