using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Core.Services.File;

public partial class FileService
{
    public async Task<List<DateTime>> GetSnapshotTimestampsAsync(UserContext user)
    {
        if (_versionService == null) return new List<DateTime>();
        // No ACL check on timestamps themselves — they're just dates.
        // Per-path access is enforced when the user opens an @GMT- path.
        // Scoped to this share so snapshots never leak across shares that
        // happen to contain files at the same relative path.
        return await _versionService.GetSnapshotTimestampsAsync(_shareId);
    }

    // ------------------ Versioning (web UI) ------------------

    public async Task<List<FileVersion>> GetFileVersionsAsync(string path, UserContext user)
    {
        if (_versionService == null) return new List<FileVersion>();

        var normalized = ShareRelativePath.Normalize(path);
        await EnsureAccessAsync(user, normalized, false, FilePermission.ListReadData);

        return await _versionService.GetVersionsAsync(_shareId, normalized);
    }

    public async Task<Stream> ReadFileVersionAsync(
        string path, DateTime snapshotTimestampUtc, UserContext user)
    {
        if (_versionService == null)
            throw new NotSupportedException("Versioning is not configured.");

        var normalized = ShareRelativePath.Normalize(path);
        await EnsureAccessAsync(user, normalized, false, FilePermission.ListReadData);

        return await _versionService.ReadVersionAsync(_shareId, normalized, snapshotTimestampUtc);
    }

    public Task RestoreFileVersionAsync(
        string path, DateTime snapshotTimestampUtc, UserContext user)
        => RestoreFileVersionAsync(path, snapshotTimestampUtc, user, allowWithoutBackup: false);

    public async Task RestoreFileVersionAsync(
        string path, DateTime snapshotTimestampUtc, UserContext user, bool allowWithoutBackup)
    {
        if (_versionService == null)
            throw new NotSupportedException("Versioning is not configured.");

        var normalized = ShareRelativePath.Normalize(path);
        await EnsureAccessAsync(user, normalized, false, FilePermission.CreateWriteData);

        // Snapshot the current content first so restoring is itself undoable.
        // Content-addressable dedup skips this if it equals the latest version. When the
        // policy or a full pool prevents the snapshot, the overwrite would destroy the
        // current content for good, so it needs the caller's explicit consent.
        if (await _storage.ExistsAsync(normalized))
        {
            await using var current = await _storage.ReadAsync(normalized, preserveAccessTime: true);
            var capture = await _versionService.CaptureVersionAsync(
                _shareId, normalized, current, user.User.Id.ToString());
            if (!capture.PreservesContent && !allowWithoutBackup)
                throw new VersionBackupUnavailableException(capture.Outcome);
        }

        // ReadVersionAsync fully decompresses and verifies size/hash before it
        // returns. IStorageEngine.WriteAsync then publishes the verified content
        // atomically, so a corrupt snapshot or failed write leaves the live file
        // untouched.
        await using var restored = await _versionService.ReadVersionAsync(_shareId, normalized, snapshotTimestampUtc);
        await _storage.WriteAsync(normalized, restored);

        await AppendFileUpsertAsync(FileChangeType.Modified, normalized);

        await OnFileCreated(ToAbsolutePath(normalized), _storage.ReadAsync(normalized, preserveAccessTime: true));
    }

    public async Task<List<DateTime>> GetFolderSnapshotTimestampsAsync(
        string folderPath, UserContext user)
    {
        if (_versionService == null) return new List<DateTime>();

        var normalized = ShareRelativePath.Normalize(folderPath);
        await EnsureAccessAsync(user, normalized, true, FilePermission.ListReadData);

        return await _versionService.GetSnapshotTimestampsAsync(_shareId, normalized);
    }

    public async Task<List<FileVersion>> GetFolderSnapshotAsync(
        string folderPath, DateTime asOfUtc, UserContext user)
    {
        if (_versionService == null) return new List<FileVersion>();

        var normalized = ShareRelativePath.Normalize(folderPath);
        await EnsureAccessAsync(user, normalized, true, FilePermission.ListReadData);

        var versions = await _versionService.GetFolderSnapshotAsync(
            _shareId, normalized, asOfUtc);
        if (versions.Count == 0) return versions;

        var readable = await FilterReadablePathsAsync(
            versions.Select(v => (v.FilePath, false)).ToList(), user);

        return versions.Where(v => readable.Contains(v.FilePath)).ToList();
    }

    public async Task<List<FileVersion>> GetFolderSnapshotAsync(
        string folderPath, DateTime asOfUtc, UserContext user,
        CancellationToken cancellationToken)
    {
        if (_versionService == null) return new List<FileVersion>();

        cancellationToken.ThrowIfCancellationRequested();
        var normalized = ShareRelativePath.Normalize(folderPath);
        await EnsureAccessAsync(
                user, normalized, true, FilePermission.ListReadData)
            .WaitAsync(cancellationToken);

        var versions = await _versionService.GetFolderSnapshotAsync(
            _shareId, normalized, asOfUtc, cancellationToken);
        if (versions.Count == 0) return versions;

        // Hide files the user may not read (per-file ACLs can differ from the folder).
        var readable = await FilterReadablePathsAsync(
                versions.Select(v => (v.FilePath, false)).ToList(), user)
            .WaitAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        return versions.Where(v => readable.Contains(v.FilePath)).ToList();
    }
}
