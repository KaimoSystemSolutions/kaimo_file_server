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
        var share = await db.ShareDefinitions.FindAsync(shareId);
        if (share?.CloudSettings?.Folders is not { Count: > 0 } folders)
            return;

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
            return;

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
        await db.SaveChangesAsync();
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
