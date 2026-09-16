using System.Text.RegularExpressions;
using Kaimo_File_Server.Core.Helpers;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Maintenance;

/// <summary>Outcome of one sweep unit.</summary>
public readonly record struct ArtifactSweepResult(int Examined, int Deleted, int Failed)
{
    public static ArtifactSweepResult operator +(ArtifactSweepResult a, ArtifactSweepResult b)
        => new(a.Examined + b.Examined, a.Deleted + b.Deleted, a.Failed + b.Failed);
}

/// <summary>
/// Deletes abandoned filesystem artifacts left behind by interrupted writes and moves:
/// per-share upload temporaries (".{name}.kaimo-{guid}.tmp", at any depth) and cross-pool
/// move staging directories ("&lt;name&gt;.kaimo-moving-{guid}", directly below a pool root).
/// Both are only ever deleted once older than a grace period, and the shape matching is
/// exact so a user-chosen name can never be swept.
/// </summary>
public sealed class ShareTempArtifactSweeper(
    ILogger<ShareTempArtifactSweeper> logger,
    TimeProvider timeProvider)
{
    // "<name>.kaimo-moving-{32 hex}" — the staging directory ShareListViewModel creates
    // for a cross-pool move.
    private static readonly Regex MoveStagingPattern = new(
        @"^.+\.kaimo-moving-[0-9a-fA-F]{32}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Deletes abandoned ".{name}.kaimo-{guid}.tmp" files at any depth in a share.</summary>
    public ArtifactSweepResult SweepShare(string shareRootFullPath, TimeSpan grace, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(shareRootFullPath) || !Directory.Exists(shareRootFullPath))
            return default;

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        int examined = 0, deleted = 0, failed = 0;

        // SafeDirectoryWalk never follows symlinks and is depth/cycle bounded.
        SafeDirectoryWalk.EnumerateFiles(shareRootFullPath, file =>
        {
            var name = Path.GetFileName(file.FullPath);
            if (!ShareEntryPolicy.IsTransientWriteArtifact(name))
                return;

            examined++;
            DateTime lastWrite;
            try { lastWrite = File.GetLastWriteTimeUtc(file.FullPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                return;
            }

            if (nowUtc - lastWrite < grace)
                return; // still within grace — could be an in-flight write

            try
            {
                File.Delete(file.FullPath);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                logger.LogDebug(ex, "Could not delete abandoned temp artifact {Path}.", file.FullPath);
            }
        }, cancellationToken: ct);

        return new ArtifactSweepResult(examined, deleted, failed);
    }

    /// <summary>
    /// Deletes abandoned "&lt;name&gt;.kaimo-moving-{guid}" staging directories directly
    /// below a storage pool root. Age is the NEWEST write time among the directory and its
    /// immediate children, so an in-flight copy (which keeps touching children) is not
    /// deleted without paying for a full recursive walk.
    /// </summary>
    public ArtifactSweepResult SweepPool(string poolRootFullPath, TimeSpan grace, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(poolRootFullPath) || !Directory.Exists(poolRootFullPath))
            return default;

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        int examined = 0, deleted = 0, failed = 0;

        string[] children;
        try { children = Directory.GetDirectories(poolRootFullPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }

        foreach (var dir in children)
        {
            ct.ThrowIfCancellationRequested();
            if (!MoveStagingPattern.IsMatch(Path.GetFileName(dir)))
                continue;

            examined++;
            var info = new DirectoryInfo(dir);
            // Never follow a link masquerading as a staging directory.
            if (SafeDirectoryWalk.IsLink(info))
                continue;

            DateTime effective;
            try { effective = EffectiveLastWriteUtc(info); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                continue;
            }

            if (nowUtc - effective < grace)
                continue;

            try
            {
                Directory.Delete(dir, recursive: true);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                logger.LogDebug(ex, "Could not delete abandoned move-staging directory {Path}.", dir);
            }
        }

        return new ArtifactSweepResult(examined, deleted, failed);
    }

    /// <summary>
    /// Newest write time among <paramref name="directory"/> and its immediate children.
    /// </summary>
    internal static DateTime EffectiveLastWriteUtc(DirectoryInfo directory)
    {
        var newest = directory.LastWriteTimeUtc;
        foreach (var child in directory.EnumerateFileSystemInfos())
        {
            if (child.LastWriteTimeUtc > newest)
                newest = child.LastWriteTimeUtc;
        }
        return newest;
    }
}
