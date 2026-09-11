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
    /// </summary>
    public static async Task CopyAsync(
        IFileService sourceFs, string sourcePath,
        IFileService destinationFs, string destinationPath,
        UserContext user, bool infinity, CancellationToken ct)
    {
        var meta = await sourceFs.GetMetadataAsync(sourcePath, user);

        if (!meta.IsDirectory)
        {
            await using var content = await sourceFs.ReadFileAsync(sourcePath, user);
            await destinationFs.WriteFileAsync(destinationPath, content, user, ct);
            return;
        }

        await EnsureCollectionAsync(destinationFs, destinationPath, user);

        if (!infinity)
            return;

        foreach (var child in await sourceFs.ListAsync(sourcePath, user))
        {
            await CopyAsync(
                sourceFs, ShareRelativePath.Combine(sourcePath, child.Name),
                destinationFs, ShareRelativePath.Combine(destinationPath, child.Name),
                user, infinity, ct);
        }
    }

    /// <summary>Creates the destination collection unless it already exists (so an overwrite copy merges).</summary>
    private static async Task EnsureCollectionAsync(IFileService fs, string path, UserContext user)
    {
        try
        {
            var existing = await fs.GetMetadataAsync(path, user);
            if (existing.IsDirectory)
                return;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }

        await fs.CreateDirectoryAsync(path, user);
    }
}
