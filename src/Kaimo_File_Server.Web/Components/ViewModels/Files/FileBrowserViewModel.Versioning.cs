using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Web.Helpers;
using Kaimo_File_Server.Core.Language;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class FileBrowserViewModel
{
    // ==================== Versioning ====================

    /// <summary>Strips the share-root prefix, yielding a share-relative path.</summary>
    private string ToShareRelative(string path)
    {
        if (CurrentShare is not null && path.StartsWith(CurrentShare.Path))
            return path[CurrentShare.Path.Length..].TrimStart('/');
        return path;
    }

    /// <summary>All stored versions of a file (newest first), or empty on failure.</summary>
    public async Task<List<FileVersion>> GetFileVersionsAsync(FileMetadata file)
    {
        if (_fileService is null || CurrentShare is null) return new();

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null) return new();

        try
        {
            return await _fileService.GetFileVersionsAsync(ToShareRelative(file.Path), userContext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list versions for {Path}", file.Path);
            return new();
        }
    }

    /// <summary>Snapshot timestamps that exist for any file inside a folder (newest first).</summary>
    public async Task<List<DateTime>> GetFolderSnapshotTimestampsAsync(FileMetadata folder)
    {
        if (_fileService is null || CurrentShare is null) return new();

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null) return new();

        try
        {
            return await _fileService.GetFolderSnapshotTimestampsAsync(ToShareRelative(folder.Path), userContext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list folder snapshots for {Path}", folder.Path);
            return new();
        }
    }

    /// <summary>The files (with their versions) that made up a folder at a point in time.</summary>
    public async Task<List<FileVersion>> GetFolderSnapshotAsync(FileMetadata folder, DateTime asOfUtc)
    {
        if (_fileService is null || CurrentShare is null) return new();

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null) return new();

        try
        {
            return await _fileService.GetFolderSnapshotAsync(ToShareRelative(folder.Path), asOfUtc, userContext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read folder snapshot for {Path}", folder.Path);
            return new();
        }
    }

    /// <summary>
    /// Reads a version's content for inline preview. Returns null when versioning is
    /// unavailable, the version is larger than the preview cap, or the read fails.
    /// </summary>
    public async Task<(byte[] Data, string ContentType, PreviewKind Kind)?> ReadVersionForPreviewAsync(
        string shareRelativePath, DateTime snapshotTimestampUtc, string fileName, long size)
    {
        if (_fileService is null || CurrentShare is null) return null;
        if (size > MaxPreviewSizeBytes) return null;

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null) return null;

        try
        {
            await using var stream = await _fileService.ReadFileVersionAsync(
                shareRelativePath, snapshotTimestampUtc, userContext);
            var data = await ReadAllBytesAsync(stream);

            return (data, FileHelper.GetContentType(fileName), FileHelper.GetPreviewKind(fileName));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read version {Ts} of {Path}", snapshotTimestampUtc, shareRelativePath);
            return null;
        }
    }

    /// <summary>Reads a version's raw bytes for download, or null on failure.</summary>
    public async Task<byte[]?> ReadVersionBytesAsync(string shareRelativePath, DateTime snapshotTimestampUtc)
    {
        if (_fileService is null || CurrentShare is null) return null;

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null) return null;

        try
        {
            await using var stream = await _fileService.ReadFileVersionAsync(
                shareRelativePath, snapshotTimestampUtc, userContext);
            return await ReadAllBytesAsync(stream);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read version bytes {Ts} of {Path}", snapshotTimestampUtc,
                shareRelativePath);
            return null;
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        if (stream.Length > int.MaxValue)
            throw new IOException("The file is too large to return as a single byte array.");

        var data = GC.AllocateUninitializedArray<byte>((int)stream.Length);
        stream.Position = 0;
        await stream.ReadExactlyAsync(data);
        return data;
    }

    /// <summary>
    /// Restores a file to an earlier version. The current content is snapshotted
    /// first (via the file service), so the restore is itself reversible.
    /// </summary>
    public async Task<OperationResult> RestoreVersionAsync(FileMetadata file, DateTime snapshotTimestampUtc)
    {
        if (_fileService is null || CurrentShare is null)
            return OperationResult.Fail(Resources.Web_Error_NoShareLoaded);

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null)
            return OperationResult.Fail(Resources.Web_Error_NotAuthenticated);

        // Overwrites the file in place: not undoable, and older inverses would hit it.
        ClearUndo();
        try
        {
            await _fileService.RestoreFileVersionAsync(ToShareRelative(file.Path), snapshotTimestampUtc, userContext);
            _logger.LogInformation("Restored '{Path}' to version {Ts} by {User}",
                file.Path, snapshotTimestampUtc, userContext.User.Username);
            return OperationResult.Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Fail(Resources.Web_Error_AccessDenied);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore version {Ts} of {Path}", snapshotTimestampUtc, file.Path);
            return OperationResult.Fail(Resources.Web_Version_RestoreFailed);
        }
    }

    /// <summary>Display name relative to a folder prefix (for point-in-time folder listings).</summary>
    public static string RelativeName(string shareRelativeFilePath, string folderShareRelativePath)
    {
        if (!string.IsNullOrEmpty(folderShareRelativePath)
            && shareRelativeFilePath.StartsWith(folderShareRelativePath + "/"))
            return shareRelativeFilePath[(folderShareRelativePath.Length + 1)..];
        return shareRelativeFilePath;
    }

    /// <summary>Public accessor for the share-relative path of a browsed item.</summary>
    public string ShareRelativeOf(FileMetadata item) => ToShareRelative(item.Path);
}
