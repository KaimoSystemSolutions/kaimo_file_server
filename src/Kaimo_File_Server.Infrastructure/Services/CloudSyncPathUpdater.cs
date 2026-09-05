using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Services;

/// <summary>
/// Updates the local-path keys inside a share's persisted CloudSettings JSON.
/// </summary>
public sealed class CloudSyncPathUpdater(
    IDbContextFactory<ApplicationDbContext> dbFactory) : ICloudSyncPathUpdater
{
    public async Task RenamePathAsync(
        Guid shareId,
        string oldRelativePath,
        string newRelativePath)
    {
        string oldPath = ShareRelativePath.Normalize(oldRelativePath);
        string newPath = ShareRelativePath.Normalize(newRelativePath);

        if (oldPath.Length == 0 ||
            string.Equals(oldPath, newPath, StringComparison.Ordinal))
            return;

        await using var db = await dbFactory.CreateDbContextAsync();

        // Keep BOTH the legacy CloudSettings.Folders map and the first-class
        // SyncDefinition rows aligned. A share may have only first-class syncs, so
        // neither update may short-circuit the other.
        bool changed = UpdateLegacyFolders(
            await db.ShareDefinitions.FindAsync(shareId), oldPath, newPath);
        changed |= await UpdateSyncDefinitionsAsync(db, shareId, oldPath, newPath);

        if (changed)
            await db.SaveChangesAsync();
    }

    private static bool UpdateLegacyFolders(
        ShareDefinition? share, string oldPath, string newPath)
    {
        if (share?.CloudSettings?.Folders is not { Count: > 0 } folders)
            return false;

        var affected = folders.Keys
            .Where(path => IsSameOrDescendant(oldPath, path))
            .Select(path => new
            {
                OldPath = path,
                NewPath = ReplacePrefix(oldPath, newPath, path),
                Folder = folders[path]
            })
            .ToList();

        if (affected.Count == 0)
            return false;

        var affectedKeys = affected
            .Select(item => item.OldPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in affected)
        {
            bool conflicts = folders.Keys.Any(existing =>
                !affectedKeys.Contains(existing) &&
                string.Equals(existing, item.NewPath,
                    StringComparison.OrdinalIgnoreCase));
            if (conflicts)
            {
                throw new InvalidOperationException(
                    $"Cannot move cloud-sync path '{item.OldPath}' to " +
                    $"'{item.NewPath}' because that path is already configured.");
            }
        }

        var updated = new Dictionary<string, SyncedFolder>(folders);
        foreach (var item in affected)
            updated.Remove(item.OldPath);
        foreach (var item in affected)
            updated[item.NewPath] = item.Folder;

        // Assign a new value so EF detects the converted JSON property change.
        share.CloudSettings = new CloudSettings(updated);
        return true;
    }

    private static async Task<bool> UpdateSyncDefinitionsAsync(
        ApplicationDbContext db, Guid shareId, string oldPath, string newPath)
    {
        // Ignore tombstones left by the first-class editor; they are bookkeeping,
        // not live syncs, and never need to follow a filesystem rename.
        var definitions = await db.SyncDefinitions
            .Where(definition => definition.LocalShareId == shareId
                && definition.MigrationSource != SyncDefinition.DeletedByFirstClassEditorSource)
            .ToListAsync();

        var affected = definitions
            .Where(definition => IsSameOrDescendant(
                oldPath, ShareRelativePath.Normalize(definition.LocalPath)))
            .Select(definition => new
            {
                Definition = definition,
                NewPath = ReplacePrefix(
                    oldPath, newPath, ShareRelativePath.Normalize(definition.LocalPath))
            })
            .ToList();

        if (affected.Count == 0)
            return false;

        var movedIds = affected.Select(item => item.Definition.Id).ToHashSet();
        foreach (var item in affected)
        {
            // Guard the unique (LocalShareId, LocalPath) index against colliding
            // with a definition that is not itself being moved.
            bool conflicts = definitions.Any(other =>
                !movedIds.Contains(other.Id) &&
                string.Equals(
                    ShareRelativePath.Normalize(other.LocalPath), item.NewPath,
                    StringComparison.OrdinalIgnoreCase));
            if (conflicts)
            {
                throw new InvalidOperationException(
                    $"Cannot move cloud-sync path " +
                    $"'{ShareRelativePath.Normalize(item.Definition.LocalPath)}' to " +
                    $"'{item.NewPath}' because that path is already configured.");
            }
        }

        foreach (var item in affected)
        {
            item.Definition.LocalPath = item.NewPath;
            item.Definition.UpdatedAtUtc = DateTime.UtcNow;
        }

        return true;
    }

    private static bool IsSameOrDescendant(string ancestor, string candidate)
        => string.Equals(ancestor, candidate, StringComparison.OrdinalIgnoreCase)
           || candidate.StartsWith(ancestor + "/",
               StringComparison.OrdinalIgnoreCase);

    private static string ReplacePrefix(
        string oldPath,
        string newPath,
        string candidate)
    {
        if (candidate.Length == oldPath.Length)
            return newPath;

        return ShareRelativePath.Combine(
            newPath,
            candidate[(oldPath.Length + 1)..]);
    }
}
