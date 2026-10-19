using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using System.Security.Cryptography;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class FileBrowserViewModel
{
    // ================= Upload conflict handling (web UI only) =================
    //
    // A web upload whose target name already exists on disk — or collides with another
    // upload in flight in this same session — must not silently overwrite. The incoming
    // bytes are staged to a server-side temp file so the browser stream is freed and the
    // rest of the batch keeps uploading; the collision is then resolved: identical content
    // is dropped, and a genuine conflict waits for the user's Overwrite/Rename/Discard
    // choice. SMB and WebDAV keep their own protocol conflict semantics and never come here.

    public enum UploadConflictAction { Overwrite, Rename, Discard }

    public enum UploadResult { Uploaded, SkippedIdentical, Discarded, Held, Failed }

    /// <summary>A staged upload waiting for the user to decide how to resolve a name collision.</summary>
    public sealed class PendingUploadConflict
    {
        public Guid Id { get; } = Guid.NewGuid();
        public required string FileName { get; init; }
        public required string TargetRelativePath { get; init; }
        public required string StagedTempPath { get; init; }
        public required long IncomingSize { get; init; }
        public required DateTime IncomingModifiedUtc { get; init; }
        // The browser's File.lastModified, stamped on the written file; null when unknown.
        public DateTime? SourceModifiedUtc { get; init; }
        // The existing on-disk item at stage time (null when the collision is only with
        // another in-flight upload that has not yet landed on disk).
        public FileMetadata? Existing { get; init; }
        // The share's file service captured at stage time, so resolving the conflict
        // still writes to the originating share even if the (circuit-scoped) view model
        // has since navigated to a different share.
        public required IFileService FileService { get; init; }
    }

    private static readonly string UploadStagingRoot =
        Path.Combine(Path.GetTempPath(), "kaimo-upload-conflicts");

    private readonly object _uploadLock = new();
    private readonly HashSet<string> _reservedUploadPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PendingUploadConflict> _pendingConflicts = new();
    private UploadConflictAction? _stickyConflictAction;
    private int _activeUploadBatches;

    public event Action? OnUploadConflictsChanged;

    public IReadOnlyList<PendingUploadConflict> PendingConflicts
    {
        get { lock (_uploadLock) return _pendingConflicts.ToList(); }
    }

    public bool HasPendingConflicts
    {
        get { lock (_uploadLock) return _pendingConflicts.Count > 0; }
    }

    /// <summary>Marks the start of an upload batch so a "apply to all" choice stays in
    /// effect for the whole run and resets once every batch has finished and drained.</summary>
    public void BeginUploadBatch()
    {
        lock (_uploadLock) _activeUploadBatches++;
    }

    public void EndUploadBatch()
    {
        lock (_uploadLock)
        {
            if (_activeUploadBatches > 0) _activeUploadBatches--;
            ClearStickyIfIdle();
        }
    }

    // Requires _uploadLock.
    private void ClearStickyIfIdle()
    {
        if (_activeUploadBatches == 0 && _pendingConflicts.Count == 0)
            _stickyConflictAction = null;
    }

    /// <summary>
    /// Uploads one selected file, detecting a name collision first. No collision → a
    /// normal upload. Collision → stage the bytes and either skip (identical), apply the
    /// session's sticky choice, or hold it for the user. Always consumes <paramref name="stream"/>.
    /// </summary>
    public async Task<UploadResult> UploadWithConflictHandlingAsync(
        string fileName, Stream stream, CancellationToken ct = default, string? relativeDir = null,
        DateTime? lastModifiedUtc = null)
    {
        if (_fileService is null || CurrentShare is null || string.IsNullOrWhiteSpace(fileName))
            return UploadResult.Failed;

        // Sanitize before the collision probe so a conflict is detected on the stored name.
        fileName = WindowsFileNameHelper.Sanitize(fileName);

        var user = await GetCurrentUserContextAsync();
        if (user is null) return UploadResult.Failed;

        var target = ShareRelativePath.Normalize(GetCurrentPath(fileName, relativeDir));

        // Probe for an existing item; the read stream doubles as the source for the
        // identical-content check when the caller may read it. Not a user access, so the
        // existing file keeps its last-access time.
        Stream? existingContent = null;
        bool existsOnDisk;
        bool existingReadable;
        try
        {
            existingContent = await _fileService.ReadFileAsync(target, user, preserveAccessTime: true);
            existsOnDisk = true;
            existingReadable = true;
        }
        catch (FileNotFoundException) { existsOnDisk = false; existingReadable = false; }
        catch (DirectoryNotFoundException) { existsOnDisk = false; existingReadable = false; }
        catch { existsOnDisk = true; existingReadable = false; } // exists but not readable as a file

        bool reservedByOther;
        lock (_uploadLock)
        {
            reservedByOther = _reservedUploadPaths.Contains(target);
            if (!existsOnDisk && !reservedByOther)
                _reservedUploadPaths.Add(target); // claim it for a straight-through write
        }

        if (!existsOnDisk && !reservedByOther)
        {
            existingContent?.Dispose();
            try
            {
                var r = await UploadFileAsync(fileName, stream, ct, relativeDir, lastModifiedUtc);
                return r.Success ? UploadResult.Uploaded : UploadResult.Failed;
            }
            finally
            {
                lock (_uploadLock) _reservedUploadPaths.Remove(target);
            }
        }

        // ---- Collision: stage the incoming bytes and hash them. ----
        Directory.CreateDirectory(UploadStagingRoot);
        var tempPath = Path.Combine(UploadStagingRoot, Guid.NewGuid().ToString("N") + ".upload");
        string incomingHash;
        long incomingSize;
        try
        {
            await using var fs = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await stream.CopyToAsync(fs, ct);
            await fs.FlushAsync(ct);
            fs.Position = 0;
            incomingHash = await ComputeSha256Async(fs);
            incomingSize = fs.Length;
        }
        catch
        {
            existingContent?.Dispose();
            TryDeleteStaged(tempPath);
            throw; // cancellation/IO surfaces to the batch loop
        }

        // Identical content → keep the existing file, drop the copy silently.
        if (existingReadable && existingContent is not null)
        {
            string existingHash;
            await using (existingContent) existingHash = await ComputeSha256Async(existingContent);
            if (string.Equals(existingHash, incomingHash, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteStaged(tempPath);
                return UploadResult.SkippedIdentical;
            }
        }
        else
        {
            existingContent?.Dispose();
        }

        var existingMeta = existsOnDisk ? await SafeGetMetadataAsync(target, user) : null;
        var conflict = new PendingUploadConflict
        {
            // Shown in the conflict dialog; the relative path tells same-named files apart.
            FileName = string.IsNullOrEmpty(relativeDir) ? fileName : $"{relativeDir}/{fileName}",
            TargetRelativePath = target,
            StagedTempPath = tempPath,
            IncomingSize = incomingSize,
            IncomingModifiedUtc = lastModifiedUtc ?? DateTime.UtcNow,
            SourceModifiedUtc = lastModifiedUtc,
            Existing = existingMeta,
            FileService = _fileService,
        };

        UploadConflictAction? sticky;
        lock (_uploadLock) sticky = _stickyConflictAction;

        if (sticky is { } act)
        {
            await ApplyConflictActionAsync(conflict, act, user);
            return act == UploadConflictAction.Discard ? UploadResult.Discarded : UploadResult.Uploaded;
        }

        lock (_uploadLock) _pendingConflicts.Add(conflict);
        NotifyConflictsChanged();
        return UploadResult.Held;
    }

    /// <summary>Resolves one held conflict (called from the conflict dialog). With
    /// <paramref name="applyToAll"/> the same choice is applied to every other queued
    /// conflict and remembered for the rest of the upload run.</summary>
    public async Task ResolveConflictAsync(Guid conflictId, UploadConflictAction action, bool applyToAll)
    {
        var user = await GetCurrentUserContextAsync();
        if (user is null) return;

        PendingUploadConflict? conflict;
        List<PendingUploadConflict> alsoApply = new();
        lock (_uploadLock)
        {
            conflict = _pendingConflicts.FirstOrDefault(c => c.Id == conflictId);
            if (conflict is not null) _pendingConflicts.Remove(conflict);
            if (applyToAll)
            {
                _stickyConflictAction = action;
                alsoApply = _pendingConflicts.ToList();
                _pendingConflicts.Clear();
            }
        }

        if (conflict is null) return;

        await ApplyConflictActionAsync(conflict, action, user);
        foreach (var c in alsoApply)
            await ApplyConflictActionAsync(c, action, user);

        lock (_uploadLock) ClearStickyIfIdle();
        NotifyConflictsChanged();
    }

    private async Task ApplyConflictActionAsync(
        PendingUploadConflict c, UploadConflictAction action, UserContext user)
    {
        try
        {
            switch (action)
            {
                case UploadConflictAction.Discard:
                    return;
                case UploadConflictAction.Overwrite:
                    // The replaced file keeps its own creation time.
                    await WriteStagedAsync(c.StagedTempPath, c.TargetRelativePath, user, c.FileService,
                        c.SourceModifiedUtc is { } m ? new FileTimes(null, m, null) : null);
                    break;
                case UploadConflictAction.Rename:
                    var renamed = await NextAvailableNameAsync(c.TargetRelativePath, user, c.FileService);
                    await WriteStagedAsync(c.StagedTempPath, renamed, user, c.FileService,
                        c.SourceModifiedUtc is { } r ? new FileTimes(r, r, null) : null);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resolving upload conflict for '{Path}' ({Action}) failed",
                c.TargetRelativePath, action);
        }
        finally
        {
            TryDeleteStaged(c.StagedTempPath);
        }
    }

    private static async Task WriteStagedAsync(
        string tempPath, string targetRelativePath, UserContext user, IFileService fileService, FileTimes? sourceTimes)
    {
        await using var fs = new FileStream(
            tempPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await WriteWithSourceTimesAsync(fileService, targetRelativePath, fs, user, sourceTimes);
    }

    /// <summary>Windows-Explorer-style "name (2).ext" collision-free variant of a path.</summary>
    private async Task<string> NextAvailableNameAsync(
        string targetRelativePath, UserContext user, IFileService fileService)
    {
        var dir = ShareRelativePath.GetParent(targetRelativePath);
        var name = ShareRelativePath.GetFileName(targetRelativePath);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);

        for (var n = 2; n < 10000; n++)
        {
            var candidate = Combine(dir, $"{stem} ({n}){ext}");
            if (!await FileExistsAsync(candidate, user, fileService) && Reserve(candidate))
                return candidate;
        }
        return Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");

        static string Combine(string folder, string leaf) =>
            ShareRelativePath.Normalize(folder.Length == 0 ? leaf : $"{folder}/{leaf}");
    }

    // Reserves a rename target so two concurrent renames cannot pick the same free name.
    private bool Reserve(string path)
    {
        lock (_uploadLock) return _reservedUploadPaths.Add(path);
    }

    private static async Task<bool> FileExistsAsync(
        string relativePath, UserContext user, IFileService fileService)
    {
        try { await using var s = await fileService.ReadFileAsync(relativePath, user, preserveAccessTime: true); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch { return true; } // exists but not readable as a file
    }

    private async Task<FileMetadata?> SafeGetMetadataAsync(string relativePath, UserContext user)
    {
        try { return await _fileService!.GetMetadataAsync(relativePath, user); }
        catch { return null; }
    }

    private void NotifyConflictsChanged()
    {
        OnUploadConflictsChanged?.Invoke();
        OnStateChanged?.Invoke();
    }

    private static async Task<string> ComputeSha256Async(Stream stream)
    {
        if (stream.CanSeek) stream.Position = 0;
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(await sha256.ComputeHashAsync(stream));
    }

    private static void TryDeleteStaged(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* a leaked staging temp is cleaned on dispose */ }
    }
}
