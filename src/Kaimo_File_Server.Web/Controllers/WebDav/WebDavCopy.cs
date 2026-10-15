using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Services.File;

namespace Kaimo_File_Server.Web.Controllers.WebDav;

/// <summary>
/// Recursive COPY/MOVE support over <see cref="IFileService"/>, which has no copy
/// primitive of its own. The recursion lives here in the DAV layer; every read and
/// write still goes through the ACL-checked file service, and source and
/// destination may be different shares (each with its own file service).
/// </summary>
public static class WebDavCopy
{
    /// <summary>
    /// Copies <paramref name="sourcePath"/> to <paramref name="destinationPath"/>.
    /// A file is copied by streaming its content; a collection is created and, when
    /// <paramref name="infinity"/> is set, its members are copied recursively
    /// (<c>Depth: 0</c> on a collection copies just the collection itself).
    /// With <paramref name="preserveTimes"/> the copy keeps the source modification times
    /// (folders stamped after their content, like <c>rsync -t</c>); an overwritten file only
    /// adopts a newer time, as for any upload. <paramref name="preserveCreated"/> also keeps the
    /// source creation times (a move, as opposed to a copy).
    /// </summary>
    public static async Task CopyAsync(
        IFileService sourceFs, string sourcePath,
        IFileService destinationFs, string destinationPath,
        UserContext user, bool infinity, CancellationToken ct, bool preserveTimes = false, bool preserveCreated = false)
    {
        var meta = await sourceFs.GetMetadataAsync(sourcePath, user);

        if (!meta.IsDirectory)
        {
            await using var content = await sourceFs.ReadFileAsync(sourcePath, user, preserveAccessTime: preserveTimes);
            await destinationFs.WriteFileAsync(
                destinationPath, content, user,
                preserveTimes ? meta.ModifiedAt.ToUniversalTime() : null,
                preserveCreated ? meta.CreatedAt.ToUniversalTime() : null, ct);
            return;
        }

        var merged = await EnsureCollectionAsync(destinationFs, destinationPath, user);

        if (!infinity)
            return;

        foreach (var child in await sourceFs.ListAsync(sourcePath, user))
        {
            await CopyAsync(
                sourceFs, ShareRelativePath.Combine(sourcePath, child.Name),
                destinationFs, ShareRelativePath.Combine(destinationPath, child.Name),
                user, infinity, ct, preserveTimes, preserveCreated);
        }

        DateTime? modified = preserveTimes ? meta.ModifiedAt.ToUniversalTime() : null;
        DateTime? created = preserveCreated ? meta.CreatedAt.ToUniversalTime() : null;
        if (merged is not null)
        {
            // Like an overwritten file, a merged folder only moves its time forward and its
            // creation time back.
            if (modified <= merged.ModifiedAt.ToUniversalTime()) modified = null;
            if (created >= merged.CreatedAt.ToUniversalTime()) created = null;
        }
        if (modified is not null || created is not null)
            await destinationFs.SetModifiedAtAsync(destinationPath, user, modified, created);
    }

    /// <summary>
    /// Creates the destination collection unless it already exists (so an overwrite copy merges);
    /// returns the existing collection's metadata, or <c>null</c> when it was created.
    /// </summary>
    private static async Task<FileMetadata?> EnsureCollectionAsync(IFileService fs, string path, UserContext user)
    {
        try
        {
            var existing = await fs.GetMetadataAsync(path, user);
            if (existing.IsDirectory)
                return existing;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }

        await fs.CreateDirectoryAsync(path, user);
        return null;
    }
}
