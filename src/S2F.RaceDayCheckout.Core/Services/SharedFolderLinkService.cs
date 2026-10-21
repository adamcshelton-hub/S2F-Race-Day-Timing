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
/// Active session DB's helpers. Graph-only race day uses <see cref="EnsureLocalDbsFolder"/>;
/// <see cref="EnsureDbsLink"/> remains for legacy OneDrive-junction tests only.
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
    /// cannot walk into OneDrive DB's. Retries, then renames aside if a handle is still open.
    /// Returns false when files remain locked (Windows often cannot rename a folder that
    /// still has an open handle) — callers should still clear local checkout state.
    /// </summary>
    public bool DeleteActiveTreeSafe(string activeSessionPath)
    {
        if (string.IsNullOrWhiteSpace(activeSessionPath) || !Directory.Exists(activeSessionPath))
            return true;

        RemoveDbsLinkIfPresent(activeSessionPath);
        ClearReadOnlyRecursive(activeSessionPath);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                if (!Directory.Exists(activeSessionPath))
                    return true;
                TryDeleteContentsBestEffort(activeSessionPath);
                Directory.Delete(activeSessionPath, recursive: true);
                return true;
            }
            catch
            {
                Thread.Sleep(250 * attempt);
                ClearReadOnlyRecursive(activeSessionPath);
            }
        }

        // Rename out of Active when possible so the path is free for the next race.
        var tombstone = activeSessionPath.TrimEnd('\\', '/') + PathService.ActivePendingDeleteSuffix;
        try
        {
            if (Directory.Exists(tombstone))
            {
                try { Directory.Delete(tombstone, recursive: true); }
                catch { PathService.TryMarkHidden(tombstone); }
            }

            Directory.Move(activeSessionPath, tombstone);
            PathService.TryMarkHidden(tombstone);
            try { Directory.Delete(tombstone, recursive: true); }
            catch
            {
                // Leftover hidden folder is OK — Active path is free.
            }

            return !Directory.Exists(activeSessionPath);
        }
        catch
        {
            // Locked folder on Windows — leave remnant; AbandonLocalActive still clears active.json.
            PathService.TryMarkHidden(activeSessionPath);
            return false;
        }
    }

    private static void ClearReadOnlyRecursive(string root)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); }
                catch { /* continue */ }
            }
        }
        catch
        {
            // directory may already be gone
        }
    }

    private static void TryDeleteContentsBestEffort(string root)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            catch
            {
                // locked — rename of the tree may still succeed
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            try { Directory.Delete(dir, recursive: false); }
            catch { /* parent delete / rename may still work */ }
        }
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
