using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Web.Helpers;
using Kaimo_File_Server.Core.Language;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class FileBrowserViewModel
{
    private long MaxUploadSizeBytes => 1100L * 1024 * 1024; // 1.1 GB

    Task<OperationResult> IFileBrowserViewModel.UploadFileAsync(string fileName, Stream fileStream,
        CancellationToken cancellationToken, string? relativeDir)
        => UploadFileAsync(fileName, fileStream, cancellationToken, relativeDir);

    /// <summary>
    /// Uploads one file. <paramref name="lastModifiedUtc"/> is the browser's <c>File.lastModified</c>;
    /// browsers report no creation time, so a new file gets that time as creation time too.
    /// </summary>
    public async Task<OperationResult> UploadFileAsync(string fileName, Stream fileStream,
        CancellationToken cancellationToken = default, string? relativeDir = null, DateTime? lastModifiedUtc = null)
    {
        if (_fileService is null || CurrentShare is null)
            return OperationResult.Fail(Resources.Web_Error_NoShareLoaded);

        if (string.IsNullOrWhiteSpace(fileName))
            return OperationResult.Fail(Resources.Web_FileName_Missing);

        // Names from Linux/macOS clients are stored under a Windows-valid name ("a:b" → "a_b").
        fileName = WindowsFileNameHelper.Sanitize(fileName);

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null)
            return OperationResult.Fail(Resources.Web_Error_NotAuthenticated);

        var targetPath = GetCurrentPath(fileName, relativeDir);
        // Not undoable (it may overwrite), and older inverses could hit the new file.
        ClearUndo();

        try
        {
            await WriteWithSourceTimesAsync(_fileService, targetPath, fileStream, userContext,
                lastModifiedUtc is { } lm ? new FileTimes(lm, lm, null) : null, cancellationToken);
            
            _logger.LogInformation("File uploaded: '{Path}' by {User}",
                targetPath, userContext.User.Username);

            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            // A cancelled upload (user abort or browser disconnect) is expected; the storage
            // layer already discarded the partial temp file.
            if (ex is OperationCanceledException)
                _logger.LogInformation("Upload cancelled for '{Path}'", targetPath);
            else
                _logger.LogError(ex, "Upload failed for '{Path}'", targetPath);

            return ex switch
            {
                OperationCanceledException => OperationResult.Fail(Resources.Web_Upload_Aborted),
                UnauthorizedAccessException => OperationResult.Fail(Resources.Web_Error_AccessDenied),
                IOException ioEx when ioEx.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                    => OperationResult.Fail(Resources.Web_Error_FileExists),
                _ => OperationResult.Fail(Resources.Web_Error_UploadFailed)
            };
        }
    }

    private static Task WriteWithSourceTimesAsync(IFileService fileService, string path, Stream data,
        UserContext user, FileTimes? sourceTimes, CancellationToken ct = default)
        => sourceTimes is { } times
            ? fileService.WriteFileAsync(path, data, user, times, ct)
            : fileService.WriteFileAsync(path, data, user, ct);

    public async Task<OperationResult> EnsureFolderAsync(string relativeDir,
        CancellationToken cancellationToken = default)
    {
        if (_fileService is null || CurrentShare is null)
            return OperationResult.Fail(Resources.Web_Error_NoShareLoaded);

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null)
            return OperationResult.Fail(Resources.Web_Error_NotAuthenticated);

        // Only missing folders are created; existing ones are merged into. Cancellation
        // propagates so the upload batch can stop as cancelled.
        ClearUndo();
        try
        {
            await UploadFolderPath.EnsureFoldersAsync(
                _fileService, CurrentPath, relativeDir, userContext, cancellationToken);
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
        catch (IOException ex) when (ex.Message.Contains("not a folder", StringComparison.Ordinal))
        {
            return OperationResult.Fail(Resources.Web_Error_ItemExists);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error creating upload folder '{Path}'", relativeDir);
            return OperationResult.Fail(Resources.Web_Error_CreateFolderFailed);
        }
    }

    public long GetMaxUploadSizeBytes()
    {
        return MaxUploadSizeBytes;
    }
}
