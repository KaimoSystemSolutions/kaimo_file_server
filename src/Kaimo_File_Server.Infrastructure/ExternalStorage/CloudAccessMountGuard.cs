using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

/// <summary>
/// Database-backed <see cref="ICloudAccessMountGuard"/>. A share has at most a handful of
/// mounts, so every operation loads the share's mount rows and compares in memory with
/// <see cref="StringComparison.OrdinalIgnoreCase"/>, which works the same on PostgreSQL and SQLite.
/// </summary>
public sealed class CloudAccessMountGuard(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<CloudAccessMountGuard> logger) : ICloudAccessMountGuard
{
    public async Task<bool> IsReservedAsync(Guid shareId, string relativePath, CancellationToken ct = default)
    {
        var path = ShareRelativePath.Normalize(relativePath);
        if (path.Length == 0)
            return false;

        var mountPaths = await GetMountPathsAsync(shareId, ct);
        return mountPaths.Any(m => ShareRelativePath.IsSameOrDescendant(m, path));
    }

    public async Task<IReadOnlyList<string>> GetMountPathsAsync(Guid shareId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.CloudAccessShares.AsNoTracking()
            .Where(s => s.MountShareId == shareId && s.Provisioning == CloudAccessProvisioning.Folder
                        && s.MountPath != null && s.MountPath != "")
            .Select(s => s.MountPath!)
            .ToListAsync(ct);
    }

    public async Task RenamePathAsync(Guid shareId, string oldRelativePath, string newRelativePath, int recycleRootDepth)
    {
        var oldPath = ShareRelativePath.Normalize(oldRelativePath);
        var newPath = ShareRelativePath.Normalize(newRelativePath);
        if (oldPath.Length == 0 || string.Equals(oldPath, newPath, StringComparison.Ordinal))
            return;

        // A mount inside the recycle bin would stay reachable through a deleted folder.
        if (ShareEntryPolicy.IsRecycleBinPath(newPath, recycleRootDepth))
        {
            await DetachAsync(shareId, oldPath);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var affected = (await LoadMountsAsync(db, shareId))
            .Where(s => ShareRelativePath.IsSameOrDescendant(oldPath, s.MountPath!))
            .ToList();
        if (affected.Count == 0)
            return;

        foreach (var share in affected)
        {
            share.MountPath = ShareRelativePath.ReplacePrefix(oldPath, newPath, share.MountPath!);
            share.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        logger.LogInformation(LogEvents.CloudAccessMountRebased, LogMessages.CloudAccessMountRebased,
            shareId, oldPath, newPath);
    }

    public async Task DetachAsync(Guid shareId, string relativePath)
    {
        var path = ShareRelativePath.Normalize(relativePath);
        if (path.Length == 0)
            return;

        await using var db = await dbFactory.CreateDbContextAsync();
        var affected = (await LoadMountsAsync(db, shareId))
            .Where(s => ShareRelativePath.IsSameOrDescendant(path, s.MountPath!))
            .ToList();
        if (affected.Count == 0)
            return;

        foreach (var share in affected)
        {
            // Provisioning stays Folder: an orphan must never fall back to a standalone share.
            share.MountShareId = null;
            share.MountPath = null;
            share.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        logger.LogWarning(LogEvents.CloudAccessMountDetached, LogMessages.CloudAccessMountDetached,
            path, shareId);
    }

    private static async Task<List<CloudAccessShare>> LoadMountsAsync(ApplicationDbContext db, Guid shareId)
        => (await db.CloudAccessShares
                .Where(s => s.MountShareId == shareId && s.Provisioning == CloudAccessProvisioning.Folder)
                .ToListAsync())
            .Where(s => !string.IsNullOrEmpty(s.MountPath))
            .ToList();
}
