using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Storage;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Core.Services.File;

public partial class FileService
{
    private sealed class FileSession : IFileSession
    {
        private readonly IStorageHandle _handle;
        private readonly FileService _owner;
        private readonly bool _isNewFile;
        private bool _disposed;
        private bool _skipVersioning;

        public string RelativePath { get; private set; }
        public string AbsolutePath => _handle.AbsolutePath;
        public bool IsDirectory => _handle.IsDirectory;
        public long Length => _handle.Length;
        public UserContext User { get; }
        public bool IsReadOnly { get; }

        public FileSession(
            IStorageHandle handle, FileService owner,
            string relativePath, UserContext user, bool isNewFile, bool isReadOnly)
        {
            _handle = handle;
            _owner = owner;
            RelativePath = relativePath;
            User = user;
            _isNewFile = isNewFile;
            IsReadOnly = isReadOnly;
        }

        public ValueTask<int> ReadAsync(long o, Memory<byte> b, CancellationToken ct)
            => _handle.ReadAsync(o, b, ct);
        public ValueTask WriteAsync(long o, ReadOnlyMemory<byte> d, CancellationToken ct)
            => _handle.WriteAsync(o, d, ct);
        public ValueTask SetLengthAsync(long l, CancellationToken ct)
            => _handle.SetLengthAsync(l, ct);
        public ValueTask SetTimesAsync(FileTimes t, CancellationToken ct)
            => _handle.SetTimesAsync(t, ct);
        public ValueTask FlushAsync(CancellationToken ct) => _handle.FlushAsync(ct);

        public void MarkDeleteOnClose() => _handle.MarkDeleteOnClose();

        public void SkipVersioning() => _skipVersioning = true;

        public async ValueTask RenameAsync(
            string newRelativePath, bool replaceExisting, CancellationToken ct = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FileSession));

            var oldRel = RelativePath;
            var newRel = ShareRelativePath.Normalize(newRelativePath);
            if (string.Equals(oldRel, newRel, StringComparison.Ordinal))
                return;

            _owner.EnsureUserWriteTargetAllowed(newRel);
            await using var operationLease =
                await _owner.BeginPathMutationOrThrowAsync(oldRel, newRel, ct);

            bool isDir = _handle.IsDirectory;

            // ACL: the source needs Delete, the destination needs Create/Write.
            if (!await _owner._acl.HasAccessAsync(
                    User, _owner._shareId, oldRel, isDir, FilePermission.Delete))
                throw new UnauthorizedAccessException($"Rename (delete) denied for '{oldRel}'");

            if (!await _owner._acl.HasAccessAsync(
                    User, _owner._shareId, newRel, isDir, FilePermission.CreateWriteData))
                throw new UnauthorizedAccessException($"Rename (create) denied for '{newRel}'");

            // Overwriting an existing target additionally requires Delete on it.
            if (replaceExisting
                && await _owner._storage.ExistsAsync(newRel)
                && !await _owner._acl.HasAccessAsync(
                        User, _owner._shareId, newRel, isDir, FilePermission.Delete))
                throw new UnauthorizedAccessException($"Rename (overwrite) denied for '{newRel}'");

            var oldAbs = _handle.AbsolutePath;
            var newAbs = _owner._storage.ToAbsolutePath(newRel);
            await _handle.MoveAsync(newAbs, newRel, replaceExisting, ct);
            RelativePath = newRel;
            await _owner.ApplyRenameSideEffectsAsync(
                oldRel, newRel, isDir, oldAbs, newAbs, bestEffort: false);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            var abs = _handle.AbsolutePath;
            var rel = RelativePath;
            var isDirty = _handle.IsDirty;
            var isDir = _handle.IsDirectory;
            var deleting = _handle.DeleteOnClose;

            // === Pre-close hooks (need the open stream) ===
            // Versioning: snapshot the file content while the stream is still open.
            if (isDirty && !isDir && !deleting && !_skipVersioning && _owner._versionService != null)
            {
                try
                {
                    await _handle.FlushAsync();
                    var snap = _handle.GetReadableSnapshot();
                    if (snap != null)
                    {
                        await _owner._versionService.CreateVersionAsync(
                            _owner._shareId, rel, snap, User.User.Id.ToString());
                    }
                }
                catch (Exception ex)
                {
                    _owner._logger.LogWarning(LogEvents.FileVersionSnapshotFailed, ex, LogMessages.FileVersionSnapshotFailed, rel);
                }
            }

            // === Close the underlying handle (this may delete the file) ===
            await _handle.DisposeAsync();

            // === Post-close hooks (just need the path) ===
            try
            {
                if (deleting)
                {
                    await _owner.ApplyDeleteSideEffectsAsync(
                        rel, isDir, abs, bestEffort: true);
                }
                else if (_isNewFile && isDir)
                {
                    await _owner.onDirectoryCreated(abs);
                }
                else if (isDirty && !isDir)
                {
                    // Content was written and closed — record it for the client change feed. A new
                    // file already logged its Created at open; this marks the content as Modified.
                    // Stat the closed file rather than stamping "now", so a time set via
                    // SetTimesAsync (cloud pull) matches what delta and the item tag report.
                    await _owner.AppendFileUpsertAsync(FileChangeType.Modified, rel);

                    // Re-open through storage for search indexing — closed stream
                    // means no race with the SMB session. The Task<Stream> contract
                    // your search hook expects is preserved.
                    await _owner.OnFileCreated(abs, _owner._storage.ReadAsync(rel, preserveAccessTime: true));
                }
            }
            catch (Exception ex)
            {
                _owner._logger.LogWarning(LogEvents.FileSearchHookFailed, ex, LogMessages.FileSearchHookFailed, rel);
            }
        }
    }

    private sealed class ReadOnlySnapshotSession : IFileSession
    {
        private readonly Stream _stream;
        private bool _disposed;

        public string RelativePath { get; }
        public string AbsolutePath => RelativePath;
        public bool IsDirectory => false;
        public long Length => _stream.Length;
        public UserContext User { get; }
        public bool IsReadOnly => true;

        public ReadOnlySnapshotSession(Stream stream, string relativePath, UserContext user)
        {
            _stream = stream;
            RelativePath = relativePath;
            User = user;
        }

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            if (_stream.Position != offset) _stream.Position = offset;
            return await _stream.ReadAsync(buffer, ct);
        }

        public ValueTask WriteAsync(long o, ReadOnlyMemory<byte> d, CancellationToken ct)
            => throw new UnauthorizedAccessException("Snapshot is read-only.");
        public ValueTask SetLengthAsync(long l, CancellationToken ct)
            => throw new UnauthorizedAccessException("Snapshot is read-only.");
        public ValueTask SetTimesAsync(FileTimes t, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RenameAsync(string newRelativePath, bool replaceExisting, CancellationToken ct)
            => throw new UnauthorizedAccessException("Snapshot is read-only.");
        public void MarkDeleteOnClose() => throw new UnauthorizedAccessException("Snapshot is read-only.");

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
