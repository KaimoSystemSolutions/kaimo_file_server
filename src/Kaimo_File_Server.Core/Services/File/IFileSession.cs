using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Storage;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Services.File
{
    /// <summary>
    /// High-level open file/directory handle returned by IFileService.OpenAsync.
    /// Carries the user context, triggers versioning + search hooks on dispose.
    /// One session = one logical SMB/HTTP/NFS open.
    /// </summary>
    public interface IFileSession : IAsyncDisposable
    {
        string RelativePath { get; }
        string AbsolutePath { get; }
        bool IsDirectory { get; }
        long Length { get; }
        UserContext User { get; }
        bool IsReadOnly { get; }

        ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct = default);
        ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default);
        ValueTask SetLengthAsync(long length, CancellationToken ct = default);
        ValueTask SetTimesAsync(FileTimes times, CancellationToken ct = default);
        ValueTask FlushAsync(CancellationToken ct = default);

        /// <summary>
        /// Renames/moves this open file or directory (ACL-checked) and keeps the
        /// session valid afterwards, so the same SMB handle can continue to be used.
        /// </summary>
        ValueTask RenameAsync(string newRelativePath, bool replaceExisting, CancellationToken ct = default);

        void MarkDeleteOnClose();
    }

    public sealed record FileOpenResult(IFileSession Session, FileOpenStatus Status);
}
