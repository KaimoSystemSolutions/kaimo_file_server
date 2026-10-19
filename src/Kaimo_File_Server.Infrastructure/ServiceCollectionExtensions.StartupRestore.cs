using Kaimo_File_Server.Infrastructure.Backup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Restores a backup on startup when <c>Backup:RestoreFromPath</c> points
    /// at an existing dump that has not yet been restored (no <c>.done</c>
    /// marker). Returns <c>true</c> when a restore was performed. Runs under
    /// the caller's advisory lock, before migrations.
    /// </summary>
    private static async Task<bool> TryRestoreOnStartupAsync(IHost host, ILogger logger)
    {
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        var requested = configuration["Backup:RestoreFromPath"];
        if (string.IsNullOrWhiteSpace(requested))
            return false;

        var backup = host.Services.GetRequiredService<IDatabaseBackupService>();

        // Accept whatever form the operator put in KAIMO_DB_RESTORE_FROM: an
        // absolute container path, a leftover host path, or just the file
        // name — with or without the ".dump" extension. Everything resolves
        // to a file inside the backup folder.
        var restorePath = ResolveRestoreTarget(requested, backup.BackupRootPath);
        if (restorePath is null)
        {
            // The operator explicitly asked for a restore, so do NOT boot
            // normally as if nothing was requested. Fail fast (before any
            // migration) with the list of backups that ARE present.
            var available = backup.ListBackups();
            var list = available.Count == 0
                ? "(none found in the backup folder)"
                : string.Join(", ", available.Select(b => b.FileName));
            throw new InvalidOperationException(
                $"KAIMO_DB_RESTORE_FROM (Backup:RestoreFromPath) is set to '{requested}', but no matching " +
                $"backup was found in the container's backup folder '{backup.BackupRootPath}'. Set it to the " +
                $"file name of a backup that exists there (the '.dump' extension is optional). " +
                $"Available backups: {list}.");
        }

        var markerPath = restorePath + ".done";
        if (File.Exists(markerPath))
        {
            logger.LogInformation(
                "Startup restore for '{Path}' was already applied (marker present). Skipping.",
                restorePath);
            return false;
        }

        logger.LogWarning(
            "Startup restore requested from '{Path}'. Restoring now — this OVERWRITES the current database.",
            restorePath);

        await backup.RestoreAsync(restorePath);

        try
        {
            await File.WriteAllTextAsync(
                markerPath,
                $"Restored at {DateTimeOffset.UtcNow:O}. Delete this marker to restore the same file again.");
        }
        catch (Exception ex)
        {
            // A missing marker would re-restore on the next boot. Fail loudly
            // rather than risk a restore loop.
            throw new InvalidOperationException(
                $"Restore succeeded but the .done marker could not be written at '{markerPath}'. " +
                "Refusing to continue to avoid restoring again on the next start.", ex);
        }

        logger.LogWarning("Startup restore from '{Path}' completed; wrote marker '{Marker}'.",
            restorePath, markerPath);
        return true;
    }

    /// <summary>
    /// Resolves the operator-supplied restore value to an existing dump file.
    /// Accepts the path exactly as given (absolute container path or relative
    /// to the working directory) or, more forgivingly, just the file name
    /// looked up inside <paramref name="backupRoot"/> — with or without the
    /// <c>.dump</c> extension, ignoring any stale host-path prefix. Returns
    /// <c>null</c> when nothing matches.
    /// </summary>
    internal static string? ResolveRestoreTarget(string requested, string backupRoot)
    {
        var value = requested.Trim();

        // 1) Exactly as given (correct absolute container path, or relative).
        if (File.Exists(value))
            return value;

        // 2) File name only, resolved against the backup folder. Using just
        //    the name means a leftover host-path prefix does not matter.
        var name = Path.GetFileName(value);
        if (string.IsNullOrEmpty(name))
            return null;

        var candidates = name.EndsWith(BackupFileNaming.Extension, StringComparison.Ordinal)
            ? new[] { name }
            : new[] { name + BackupFileNaming.Extension, name };

        foreach (var candidate in candidates)
        {
            var path = Path.Combine(backupRoot, candidate);
            if (File.Exists(path))
                return path;
        }

        return null;
    }
}
