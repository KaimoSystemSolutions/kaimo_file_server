using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Language;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class FileBrowserViewModel
{
    public async Task CreateFolderAtAsync(string path)
    {
        var (service, user) = await RequireServiceAndUserAsync();
        await service.CreateDirectoryAsync(path, user);
    }

    /// <summary>Create a new sub-folder inside the current directory.</summary>
    public async Task<OperationResult> CreateFolderAsync(string folderName)
    {
        if (_fileService is null || CurrentShare is null)
            return OperationResult.Fail(Resources.Web_Error_NoShareLoaded);

        if (string.IsNullOrWhiteSpace(folderName))
            return OperationResult.Fail(Resources.Web_Folder_NameRequired);

        // File- / Directoryname Validation
        if (!WindowsFileNameHelper.IsValid(folderName))
        {
            var errors = WindowsFileNameHelper.GetValidationErrors(folderName);
            return OperationResult.Fail(Resources.Web_Name_InvalidChars + string.Join(", ", errors));
        }

        try
        {
            var userContext = await GetCurrentUserContextAsync();
            if (userContext is null)
                return OperationResult.Fail(Resources.Web_Error_NotAuthenticated);

            var targetPath = GetCurrentPath(folderName);

            await _fileService.CreateDirectoryAsync(targetPath, userContext);

            _logger.LogInformation("Folder created: '{Path}' by {User}",
                targetPath, userContext.User.Username);

            return OperationResult.Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Fail(Resources.Web_Error_AccessDenied);
        }
        catch (CloudSyncOperationConflictException)
        {
            return OperationResult.Fail(Resources.Web_Error_ShareInUse);
        }
        catch (IOException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Fail(Resources.Web_Error_ItemExists);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating folder '{FolderName}'", folderName);
            return OperationResult.Fail(Resources.Web_Error_CreateFolderFailed);
        }
    }

    /// <summary>Delete a file or directory (recursively).</summary>
    public async Task<OperationResult> DeleteAsync(FileMetadata item)
    {
        if (_fileService is null || CurrentShare is null)
            return OperationResult.Fail(Resources.Web_Error_NoShareLoaded);

        try
        {
            var userContext = await GetCurrentUserContextAsync();
            if (userContext is null)
                return OperationResult.Fail(Resources.Web_Error_NotAuthenticated);

            // Build relative path within the share
            var relativePath = item.Path;
            if (relativePath.StartsWith(CurrentShare.Path))
                relativePath = relativePath[CurrentShare.Path.Length..].TrimStart('/');

            await _fileService.DeleteFileAsync(relativePath, userContext, CurrentShare.IsRecycleEnabled);

            _logger.LogInformation("{Type} deleted: '{Path}' by {User}",
                item.IsDirectory ? "Directory" : "File",
                relativePath, userContext.User.Username);

            return OperationResult.Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Fail(Resources.Web_Error_AccessDenied);
        }
        catch (FileNotFoundException)
        {
            return OperationResult.Fail(Resources.Web_Error_FileOrFolderNotFound);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting '{Path}'", item.Path);
            return OperationResult.Fail(Resources.Web_Error_DeleteFailed);
        }
    }

    public async Task<OperationResult> RenameAsync(FileMetadata item, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            return OperationResult.Fail(Resources.Web_Rename_NameRequired);

        if (!WindowsFileNameHelper.IsValid(newName))
        {
            var errors = WindowsFileNameHelper.GetValidationErrors(newName);
            return OperationResult.Fail(
                Resources.Web_Name_InvalidChars + string.Join(", ", errors));
        }

        if (CurrentShare is null)
            return OperationResult.Fail(Resources.Web_Error_NoShareLoaded);

        var relativePath = item.Path;
        if (relativePath.StartsWith(CurrentShare.Path))
            relativePath = relativePath[CurrentShare.Path.Length..].TrimStart('/');

        var parentDir = Kaimo_File_Server.Core.Helpers.ShareRelativePath.GetParent(relativePath);
        var newRelativePath = Kaimo_File_Server.Core.Helpers.ShareRelativePath.Combine(parentDir, newName);

        return await MoveInternalAsync(item, relativePath, newRelativePath, "renamed");
    }

    /// <summary>
    /// Moves an item into a different directory within the same share, keeping its name.
    /// </summary>
    /// <param name="item">The item to move.</param>
    /// <param name="destRelativePath">Share-relative path of the destination directory ("" = share root).</param>
    public async Task<OperationResult> MoveAsync(FileMetadata item, string destRelativePath)
    {
        if (CurrentShare is null)
            return OperationResult.Fail(Resources.Web_Error_NoShareLoaded);

        var relativePath = item.Path;
        if (relativePath.StartsWith(CurrentShare.Path))
            relativePath = relativePath[CurrentShare.Path.Length..].TrimStart('/');

        var newRelativePath = Kaimo_File_Server.Core.Helpers.ShareRelativePath.Combine(destRelativePath, item.Name);

        // No-op: dropped onto the folder it's already in
        var currentParent = Kaimo_File_Server.Core.Helpers.ShareRelativePath.GetParent(relativePath);
        if (string.Equals(currentParent, destRelativePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return OperationResult.Ok();

        return await MoveInternalAsync(item, relativePath, newRelativePath, "moved");
    }

    public async Task<List<FileMetadata>> ListDirectoryAsync(string dirPath)
    {
        var (service, user) = await RequireServiceAndUserAsync();
        return await service.ListAsync(dirPath, user);
    }

    public async Task CopyAsync(FileMetadata item, string targetPath, CancellationToken cancellationToken)
    {
        var (service, user) = await RequireServiceAndUserAsync();
        Stream fileToCopy = await service.ReadFileAsync(item.Path, user);
        await service.WriteFileAsync(targetPath, fileToCopy, user, cancellationToken);
    }

    private string GetCurrentPath(string fileName)
    {
        return string.IsNullOrEmpty(CurrentPath) 
            ? fileName
            : $"{CurrentPath}/{fileName}";
    }

    // Target of a folder-upload entry: relativeDir is validated by the caller (UploadFolderPath).
    private string GetCurrentPath(string fileName, string? relativeDir)
        => string.IsNullOrEmpty(relativeDir) ? GetCurrentPath(fileName) : GetCurrentPath($"{relativeDir}/{fileName}");

    /// <summary>
    /// Shared implementation for Rename and Move: both are just "relocate item from
    /// one share-relative path to another" as far as the file service is concerned.
    /// </summary>
    private async Task<OperationResult> MoveInternalAsync(
        FileMetadata item, string relativePath, string newRelativePath, string logVerb)
    {
        if (_fileService is null)
            return OperationResult.Fail(Resources.Web_Error_NoShareLoaded);

        try
        {
            var userContext = await GetCurrentUserContextAsync();
            if (userContext is null)
                return OperationResult.Fail(Resources.Web_Error_NotAuthenticated);

            await _fileService.RenameAsync(relativePath, newRelativePath, userContext);

            _logger.LogInformation("{Type} {Verb}: '{OldPath}' -> '{NewPath}' by {User}",
                item.IsDirectory ? "Directory" : "File",
                logVerb, relativePath, newRelativePath, userContext.User.Username);

            return OperationResult.Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Fail(Resources.Web_Error_AccessDenied);
        }
        catch (IOException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Fail(Resources.Web_Error_ItemExists);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error relocating '{Path}' to '{NewPath}'", relativePath, newRelativePath);
            return OperationResult.Fail(logVerb == "moved"
                ? Resources.Web_Error_MoveFailed
                : Resources.Web_Error_RenameFailed);
        }
    }

    public async Task<OperationResult> ArchiveAsync(List<FileMetadata> items, string format)
    {
        if (_fileService is null || CurrentShare is null)
            return OperationResult.Fail(Resources.Web_Error_NoShareLoaded);

        try
        {
            var userContext = await GetCurrentUserContextAsync();
            if (userContext is null)
                return OperationResult.Fail(Resources.Web_Error_NotAuthenticated);

            var relativePaths = items.Select(item =>
            {
                var path = item.Path;
                if (path.StartsWith(CurrentShare.Path))
                    path = path[CurrentShare.Path.Length..].TrimStart('/');
                return path;
            }).ToList();

            var archiveName = items.Count == 1
                ? Path.GetFileNameWithoutExtension(items[0].Name) + format
                : "archiv" + format;

            var targetPath = GetCurrentPath(archiveName);

            await _fileService.ArchiveAsync(relativePaths, targetPath, format, userContext);

            _logger.LogInformation("Archived {Count} items -> '{Target}' by {User}",
                items.Count, targetPath, userContext.User.Username);

            return OperationResult.Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Fail(Resources.Web_Error_AccessDenied);
        }
        catch (IOException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Fail(string.Format(Resources.Web_Error_ArchiveExists, $"archiv{format}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error archiving {Count} items", items.Count);
            return OperationResult.Fail(Resources.Web_Error_ArchiveFailed);
        }
    }

    public async Task<OperationResult> UnzipAsync(FileMetadata file)
    {
        if (_fileService is null || CurrentShare is null)
            return OperationResult.Fail(Resources.Web_Error_NoShareLoaded);

        try
        {
            var userContext = await GetCurrentUserContextAsync();
            if (userContext is null)
                return OperationResult.Fail(Resources.Web_Error_NotAuthenticated);

            var relativePath = file.Path;
            if (relativePath.StartsWith(CurrentShare.Path))
                relativePath = relativePath[CurrentShare.Path.Length..].TrimStart('/');

            // Target folder = same directory, named after the zip (without extension)
            var folderName = Path.GetFileNameWithoutExtension(file.Name);
            var parentDir = Kaimo_File_Server.Core.Helpers.ShareRelativePath.GetParent(relativePath);
            var targetDir = Kaimo_File_Server.Core.Helpers.ShareRelativePath.Combine(parentDir, folderName);

            await _fileService.UnzipAsync(relativePath, targetDir, userContext);

            _logger.LogInformation("Unzipped: '{Path}' -> '{Target}' by {User}",
                relativePath, targetDir, userContext.User.Username);

            return OperationResult.Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Fail(Resources.Web_Error_AccessDenied);
        }
        catch (InvalidDataException)
        {
            return OperationResult.Fail(Resources.Web_Error_NotValidZip);
        }
        catch (IOException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Fail(Resources.Web_Error_FolderExists);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unzipping '{Path}'", file.Path);
            return OperationResult.Fail(Resources.Web_Error_ExtractFailed);
        }
    }
}
