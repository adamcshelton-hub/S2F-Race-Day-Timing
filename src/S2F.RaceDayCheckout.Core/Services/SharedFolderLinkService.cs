using System.Diagnostics;
using System.Runtime.InteropServices;

namespace S2F.RaceDayCheckout.Core.Services;

public sealed class LinkResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool Created { get; init; }
}

/// <summary>
/// Links Active session shared folders (DB's) to OneDrive so RunScore relative
/// exports like <c>~..\DB's\%race name% backupsave.csv</c> still hit the cloud pack.
/// </summary>
public sealed class SharedFolderLinkService
{
    public const string DbsFolderName = "DB's";

    /// <summary>
    /// Ensure <c>{activeSession}\DB's</c> is a link/junction to <c>{librarySession}\DB's</c>.
    /// </summary>
    public LinkResult EnsureDbsLink(string activeSessionPath, string librarySessionPath)
    {
        if (string.IsNullOrWhiteSpace(activeSessionPath) || string.IsNullOrWhiteSpace(librarySessionPath))
        {
            return new LinkResult { Success = false, Message = "Active or library session path is empty." };
        }

        var target = Path.Combine(librarySessionPath, DbsFolderName);
        var link = Path.Combine(activeSessionPath, DbsFolderName);

        try
        {
            Directory.CreateDirectory(activeSessionPath);
            Directory.CreateDirectory(target);
        }
        catch (Exception ex)
        {
            return new LinkResult
            {
                Success = false,
                Message = $"Could not prepare DB's folders: {ex.Message}"
            };
        }

        try
        {
            if (Directory.Exists(link) || File.Exists(link))
            {
                if (IsReparsePoint(link) && PathsEqual(ResolveLinkTarget(link) ?? link, target))
                {
                    return new LinkResult
                    {
                        Success = true,
                        Created = false,
                        Message = "Active DB's already linked to OneDrive."
                    };
                }

                if (IsReparsePoint(link))
                {
                    // Wrong / stale link — remove link only, never the target tree.
                    Directory.Delete(link);
                }
                else
                {
                    // Real folder under Active would hide OneDrive DB's — refuse to wipe it.
                    return new LinkResult
                    {
                        Success = false,
                        Message =
                            $"Active already has a real '{DbsFolderName}' folder (not a link): {link}. "
                            + "Remove it so checkout can link to OneDrive DB's."
                    };
                }
            }

            if (!TryCreateDirectoryLink(link, target, out var createError))
            {
                return new LinkResult
                {
                    Success = false,
                    Message = "Could not link Active DB's → OneDrive: " + createError
                };
            }

            return new LinkResult
            {
                Success = true,
                Created = true,
                Message = $"Linked Active DB's → OneDrive ({target})."
            };
        }
        catch (Exception ex)
        {
            return new LinkResult { Success = false, Message = "DB's link failed: " + ex.Message };
        }
    }

    /// <summary>
    /// Graph mode: real local DB's folder under Active (app publishes CSVs via Graph).
    /// Replaces the OneDrive junction used when Desktop sync is the transport.
    /// </summary>
    public LinkResult EnsureLocalDbsFolder(string activeSessionPath, string? seedFromLibrarySession = null)
    {
        if (string.IsNullOrWhiteSpace(activeSessionPath))
        {
            return new LinkResult { Success = false, Message = "Active session path is empty." };
        }

        var link = Path.Combine(activeSessionPath, DbsFolderName);
        try
        {
            Directory.CreateDirectory(activeSessionPath);

            if (Directory.Exists(link) || File.Exists(link))
            {
                if (IsReparsePoint(link))
                {
                    Directory.Delete(link);
                }
                else
                {
                    return new LinkResult
                    {
                        Success = true,
                        Created = false,
                        Message = "Active DB's is a local folder (Graph publish)."
                    };
                }
            }

            Directory.CreateDirectory(link);

            if (!string.IsNullOrWhiteSpace(seedFromLibrarySession))
            {
                var seed = Path.Combine(seedFromLibrarySession, DbsFolderName);
                if (Directory.Exists(seed) && !IsReparsePoint(seed))
                {
                    foreach (var file in Directory.EnumerateFiles(seed, "*", SearchOption.TopDirectoryOnly))
                    {
                        var dest = Path.Combine(link, Path.GetFileName(file));
                        if (!File.Exists(dest))
                            File.Copy(file, dest);
                    }
                }
            }

            return new LinkResult
            {
                Success = true,
                Created = true,
                Message = "Active DB's is a local folder; app will publish to OneDrive via Graph."
            };
        }
        catch (Exception ex)
        {
            return new LinkResult { Success = false, Message = "Could not create local DB's: " + ex.Message };
        }
    }

    /// <summary>
    /// Remove Active DB's link only (never delete OneDrive DB's contents).
    /// Call before wiping the Active tree on Finish.
    /// </summary>
    public void RemoveDbsLinkIfPresent(string activeSessionPath)
    {
        if (string.IsNullOrWhiteSpace(activeSessionPath))
            return;

        var link = Path.Combine(activeSessionPath, DbsFolderName);
        try
        {
            if (!Directory.Exists(link) && !File.Exists(link))
                return;

            if (IsReparsePoint(link))
            {
                Directory.Delete(link);
                return;
            }

            // Real folder — leave it; recursive Active delete is the caller's problem.
        }
        catch
        {
            // Finish still proceeds; worst case Active cleanup reports leftover.
        }
    }

    /// <summary>
    /// Delete Active tree safely: drop DB's reparse link first so recursive delete
    /// cannot walk into OneDrive DB's.
    /// </summary>
    public void DeleteActiveTreeSafe(string activeSessionPath)
    {
        if (string.IsNullOrWhiteSpace(activeSessionPath) || !Directory.Exists(activeSessionPath))
            return;

        RemoveDbsLinkIfPresent(activeSessionPath);
        Directory.Delete(activeSessionPath, recursive: true);
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath, out string error)
    {
        error = string.Empty;
        var fullTarget = Path.GetFullPath(targetPath);
        var fullLink = Path.GetFullPath(linkPath);

        try
        {
            Directory.CreateSymbolicLink(fullLink, fullTarget);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        // Windows fallback: junction (no admin required).
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c mklink /J \"{fullLink}\" \"{fullTarget}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (process is null)
                {
                    error = "Could not start mklink.";
                    return false;
                }

                process.WaitForExit(15_000);
                if (process.ExitCode == 0 && (Directory.Exists(fullLink) || File.Exists(fullLink)))
                    return true;

                var stderr = process.StandardError.ReadToEnd();
                error = string.IsNullOrWhiteSpace(stderr)
                    ? $"mklink exit {process.ExitCode}"
                    : stderr.Trim();
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }

        return false;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return attrs.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveLinkTarget(string linkPath)
    {
        try
        {
            var info = new DirectoryInfo(linkPath);
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
