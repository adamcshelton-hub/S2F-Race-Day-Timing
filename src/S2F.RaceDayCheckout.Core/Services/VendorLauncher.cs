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

    private readonly RunScorePreferencesService _prefs;

    public VendorLauncher(RunScorePreferencesService? prefs = null)
    {
        _prefs = prefs ?? new RunScorePreferencesService();
    }

    public LaunchResult LaunchRunScore(ActiveCheckout active, AppSettings settings)
    {
        var runScoreDir = Path.Combine(active.ActivePath, PathService.RunScoreFolderName);
        if (!Directory.Exists(runScoreDir))
            return new LaunchResult { Success = false, Message = $"Active RUNSCORE missing: {runScoreDir}" };

        // Zero timer steps: enforce company SOP prefs (alternate Backup folder, 1-min auto-save, etc.)
        // before RSServer loads the registry into memory.
        var prefResult = _prefs.ApplySop(active.BackupPath, settings.RaceDayRoot);
        try
        {
            AutosaveMirrorService.RecoverMisplacedBackupSiblingsAsync(
                    active.BackupPath, active.AutosavePath)
                .GetAwaiter().GetResult();
        }
        catch
        {
            // Non-fatal — live mirror will retry.
        }

        var prefNote = prefResult.Success
            ? " " + prefResult.Message
            : " Preference SOP warning: " + prefResult.Message;

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
                              + prefNote
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
                Message = (raceFile is null
                    ? $"Launched RunScore. Open the race under {runScoreDir}."
                    : $"Launched RunScore with {Path.GetFileName(raceFile)}.")
                    + prefNote
            };
        }
        catch (Exception ex)
        {
            return new LaunchResult { Success = false, Message = "Failed to launch RunScore: " + ex.Message };
        }
    }

    /// <param name="bare">
    /// When true, always start empty T&amp;S (no .event) so staff can build a new file and Save As —
    /// used for first .event or announcer / video secondaries. When false, open an existing .event
    /// (<paramref name="eventFileOverride"/> or resolved primary).
    /// </param>
    /// <param name="eventFileOverride">Optional .event file name under Active (Open Current).</param>
    public LaunchResult LaunchTimingAndScoring(
        ActiveCheckout active,
        AppSettings settings,
        bool bare = false,
        string? eventFileOverride = null)
    {
        Directory.CreateDirectory(active.ActivePath);

        try
        {
            var exe = ResolveTimingAndScoringExe(settings);

            if (!bare)
            {
                var fileName = SessionRoleStore.NormalizeFileName(eventFileOverride)
                               ?? ResolvePrimaryEventFile(active);
                var eventPath = string.IsNullOrWhiteSpace(fileName)
                    ? null
                    : Path.Combine(active.ActivePath, fileName);

                if (eventPath is not null && File.Exists(eventPath))
                {
                    if (exe is not null)
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exe,
                            Arguments = $"\"{eventPath}\"",
                            WorkingDirectory = active.ActivePath,
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
                        Message = $"Launched Timing & Scoring with {Path.GetFileName(eventPath)}."
                    };
                }

                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    return new LaunchResult
                    {
                        Success = false,
                        Message = $"Timing & Scoring file not in Active yet: {fileName}.\n\n"
                                  + "Checkout / Refresh Event Files, or use Create a New Timing & Scoring and Save As."
                    };
                }
            }

            // Bare / no .event: launch empty T&S so staff can Start a new event → Save As.
            if (exe is null)
            {
                return new LaunchResult
                {
                    Success = false,
                    Message = "Timing & Scoring path is not set.\n\n"
                              + "Set it in Admin (top-right), then try again."
                };
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = active.ActivePath,
                UseShellExecute = true
            });

            return new LaunchResult
            {
                Success = true,
                Message = bare
                    ? "Launched Create a New Timing & Scoring — Save As into the Active folder."
                    : "Launched Timing & Scoring (no .event yet)."
            };
        }
        catch (Exception ex)
        {
            return new LaunchResult { Success = false, Message = "Failed to launch T&S: " + ex.Message };
        }
    }

    /// <summary>Suggested Save As filename when creating a .event mid-week.</summary>
    public static string SuggestEventFileName(ActiveCheckout active) =>
        SuggestEventFileNameFromRaceName(
            string.IsNullOrWhiteSpace(active.SessionName) ? "Race" : active.SessionName);

    /// <summary>Suggested .event name from race name or session folder (strips year prefix).</summary>
    public static string SuggestEventFileNameFromRaceName(string raceOrSessionName)
    {
        var baseName = string.IsNullOrWhiteSpace(raceOrSessionName) ? "Race" : raceOrSessionName.Trim();
        // Session folders are often "2026 - Race Name" — strip year prefix for a cleaner .event name.
        var dash = baseName.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0
            && dash <= 4
            && int.TryParse(baseName[..dash], out _))
            baseName = baseName[(dash + 3)..].Trim();

        foreach (var c in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(c, '-');
        return baseName + ".event";
    }

    /// <summary>
    /// Visual folder map for the T&S Save dialog (This PC → drive → … → Active).
    /// Clipboard paste does not work in T&S Save As — staff must click through.
    /// </summary>
    public static string FormatSaveAsFolderGuide(ActiveCheckout active) =>
        FormatSaveAsFolderGuide(active.ActivePath, SuggestEventFileName(active));

    public static string FormatSaveAsFolderGuide(string activePath, string suggestedEventFileName) =>
        BuildSaveAsFolderGuide(
            activePath,
            suggestedEventFileName,
            "No Timing & Scoring .event yet (common for Templates).",
            "Checkout anyway — after you build in T&S, Save As here",
            "(T&S often opens Documents — don't leave it there):");

    /// <summary>
    /// Compact Save As path for Admin (no “no .event yet” copy, no vertical folder tree).
    /// One Windows path line + suggested file name — T&amp;S Save As still needs This PC click-through.
    /// </summary>
    public static string FormatSaveAsPathInline(string activePath, string suggestedEventFileName)
    {
        var folder = NormalizeWindowsPathDisplay(activePath);
        var suggested = string.IsNullOrWhiteSpace(suggestedEventFileName)
            ? "Race.event"
            : Path.GetFileName(suggestedEventFileName.Trim());
        if (string.IsNullOrWhiteSpace(suggested))
            suggested = "Race.event";

        return folder + "\\" + suggested;
    }

    /// <summary>
    /// Same folder map as checkout, worded for the Active screen (stays under Finish).
    /// </summary>
    public static string FormatActiveSaveAsReminder(ActiveCheckout active) =>
        BuildSaveAsFolderGuide(
            active.ActivePath,
            SuggestEventFileName(active),
            "Save the Timing & Scoring .event here (not Documents).",
            "Save announcer / video .event files into this same folder.",
            "T&S often opens Documents — don't leave it there.");

    private static string NormalizeWindowsPathDisplay(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var trimmed = path.Trim().TrimEnd('\\', '/');
        var parts = trimmed.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return string.Empty;

        // Keep drive letter as C: then join with backslashes for Windows Save As.
        return string.Join("\\", parts);
    }

    private static string BuildSaveAsFolderGuide(
        string activePath,
        string suggestedEventFileName,
        params string[] introLines)
    {
        var suggested = string.IsNullOrWhiteSpace(suggestedEventFileName)
            ? "Race.event"
            : suggestedEventFileName.Trim();
        // Split on both separators so guides match Windows Save As even when unit-tested on macOS.
        var parts = activePath
            .Trim()
            .TrimEnd('\\', '/')
            .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        var lines = new List<string>();
        lines.AddRange(introLines);
        lines.Add("");
        lines.Add("In the Save dialog (left side):");
        lines.Add("  click This PC  —  not Documents");
        lines.Add("");
        lines.Add("Then open folders in this order:");

        var indent = "  ";
        lines.Add(indent + "This PC");
        indent += "  ";

        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var label = part.Length == 2 && part[1] == ':'
                ? $"Local Disk ({part})"
                : part;
            var isTargetFolder = i == parts.Count - 1;
            lines.Add(indent + label + (isTargetFolder ? "   ← open this folder" : ""));
            indent += "  ";
        }

        lines.Add(indent + suggested + "   ← File name");
        return string.Join("\n", lines);
    }

    public static string? ResolvePrimaryEventFile(ActiveCheckout active)
    {
        if (!string.IsNullOrWhiteSpace(active.PrimaryEventFile)
            && File.Exists(Path.Combine(active.ActivePath, active.PrimaryEventFile)))
            return active.PrimaryEventFile;

        return EventFileSelector.ChoosePrimary(EventFileSelector.ListEventFiles(active.ActivePath));
    }

    private static string? ResolveTimingAndScoringExe(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.TimingAndScoringExePath)
            && File.Exists(settings.TimingAndScoringExePath))
            return settings.TimingAndScoringExePath;

        var names = new[]
        {
            "mylaps_ts_launch.exe",
            "TimingAndScoring.exe",
            "TimingAndScoring64.exe",
            "MyLaps Timing and Scoring.exe"
        };
        var roots = new List<string>();
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var root in new[] { pf, pf86 })
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;
            roots.Add(root);
            try
            {
                roots.AddRange(Directory.GetDirectories(root, "*MYLAPS*", SearchOption.TopDirectoryOnly));
                roots.AddRange(Directory.GetDirectories(root, "*MyLaps*", SearchOption.TopDirectoryOnly));
            }
            catch
            {
                // permission
            }
        }

        foreach (var root in roots)
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(root, name);
                if (File.Exists(candidate))
                    return candidate;
            }

            try
            {
                var hit = Directory.EnumerateFiles(root, "mylaps_ts_launch.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (hit is not null)
                    return hit;
            }
            catch
            {
                // permission
            }
        }

        return null;
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
