using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;

namespace Kaimo_File_Server.Core.Services.File;

public partial class FileService
{
    public Task OnFileCreated(string absolutePath, Task<Stream> fileData)
        => _searchService?.onFileCreated(absolutePath, fileData) ?? DrainStreamAsync(fileData);

    public Task onDirectoryCreated(string absolutePath)
        => _searchService?.onDirectoryCreated(absolutePath) ?? Task.CompletedTask;

    public Task onFileDeleted(string absolutePath)
        => _searchService?.onFileDeleted(absolutePath) ?? Task.CompletedTask;
    
    public Task onDirectoryDeleted(string absolutePath)
        => _searchService?.onDirectoryDeleted(absolutePath) ?? Task.CompletedTask;

    private static async Task DrainStreamAsync(Task<Stream> streamTask)
    {
        await using var stream = await streamTask;
    }

    public void onFileRenamed(string oldAbsolutePath, string newAbsolutePath)
    {
        _searchService?.onFileRenamed(oldAbsolutePath, newAbsolutePath);
    }

    public void onDirectoryRenamed(string oldAbsolutePath, string newAbsolutePath)
    {
        _searchService?.onDirectoryRenamed(oldAbsolutePath, newAbsolutePath);
    }

    // ------------------ External-writer close hooks (Samba VFS direct I/O) ------------------
    //
    // Samba performs the raw file I/O itself, then the VFS bridge calls these so the same
    // cross-cutting effects as FileSession.DisposeAsync happen: versioning, search indexing,
    // ownership. P1-11 surfaces failures to the bridge so authd retains and
    // retries the durable event. The native SMB operation has already completed.

    public async Task NotifyExternalCloseAsync(
        string path,
        UserContext user,
        Func<Task<Stream>> openCapturedContent)
    {
        ArgumentNullException.ThrowIfNull(openCapturedContent);
        var rel = ShareRelativePath.Normalize(path);
        var abs = _storage.ToAbsolutePath(rel);
        var failures = new List<Exception>();

        try
        {
            await RecordOwnerAsync(
                rel, isDirectory: false, user, bestEffort: false);
        }
        catch (Exception ex) { failures.Add(ex); }

        // Versioning: snapshot the freshly written content (mirrors DisposeAsync).
        if (_versionService != null)
        {
            try
            {
                await using var content = await openCapturedContent();
                await _versionService.CreateVersionAsync(_shareId, rel, content, user.User.Id.ToString());
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        // Search index. Awaited (not fire-and-forget like the in-process DisposeAsync):
        // the bridge runs this inside a short-lived gRPC scope, so we must let it finish
        // before the scope/DbContext is torn down — and surface errors.
        if (_searchService != null)
        {
            try { await _searchService.onFileCreated(abs, openCapturedContent()); }
            catch (Exception ex) { failures.Add(ex); }
        }

        // Record the write for the client change feed (create or overwrite; the client downloads
        // the latest either way). The native SMB write already completed.
        await AppendFileUpsertAsync(FileChangeType.Modified, rel);

        if (failures.Count != 0)
            throw new AggregateException(
                $"One or more close side effects failed for '{rel}'.",
                failures);
    }

    public async Task NotifyExternalMkdirAsync(string path, UserContext user)
    {
        var rel = ShareRelativePath.Normalize(path);
        var abs = _storage.ToAbsolutePath(rel);
        var failures = new List<Exception>();
        try
        {
            await RecordOwnerAsync(
                rel, isDirectory: true, user, bestEffort: false);
        }
        catch (Exception ex) { failures.Add(ex); }
        if (_searchService != null)
        {
            try { await _searchService.onDirectoryCreated(abs); }
            catch (Exception ex) { failures.Add(ex); }
        }

        await AppendChangeAsync(FileChangeType.Created, rel, isDirectory: true);

        if (failures.Count != 0)
            throw new AggregateException(
                $"One or more mkdir side effects failed for '{rel}'.",
                failures);
    }

    public async Task NotifyExternalDeleteAsync(string path, bool isDirectory)
    {
        var rel = ShareRelativePath.Normalize(path);
        var abs = _storage.ToAbsolutePath(rel);
        await ApplyDeleteSideEffectsAsync(rel, isDirectory, abs, bestEffort: false);
    }

    public async Task NotifyExternalRenameAsync(
        string oldPath,
        string newPath,
        bool isDirectory,
        Guid sambaLifecycleEventId)
    {
        var oldRel = ShareRelativePath.Normalize(oldPath);
        var newRel = ShareRelativePath.Normalize(newPath);
        var oldAbs = _storage.ToAbsolutePath(oldRel);
        var newAbs = _storage.ToAbsolutePath(newRel);

        await ApplyRenameSideEffectsAsync(
            oldRel, newRel, isDirectory, oldAbs, newAbs, bestEffort: false,
            sambaLifecycleEventId);
    }
}
