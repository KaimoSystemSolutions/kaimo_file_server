using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Storage
{
    /// <summary>
    /// Low-level handle to an open file or directory on the storage backend.
    /// One handle = one OS-level FileStream (for files) that stays open across
    /// many Read/Write calls. The underlying stream is owned by the handle.
    /// </summary>
    public interface IStorageHandle : IAsyncDisposable
    {
        string RelativePath { get; }
        string AbsolutePath { get; }
        bool IsDirectory { get; }
        long Length { get; }

        bool IsDirty { get; }
        bool DeleteOnClose { get; }

        ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct = default);
        ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default);
        ValueTask SetLengthAsync(long length, CancellationToken ct = default);
        ValueTask SetTimesAsync(FileTimes times, CancellationToken ct = default);
        ValueTask FlushAsync(CancellationToken ct = default);

        /// <summary>
        /// Renames/moves the underlying file or directory while keeping this handle
        /// valid. For files the open stream is closed, the file is moved on disk and
        /// the stream is reopened at the new location (preserving the read/write
        /// position), so an SMB client can continue using the same handle after a
        /// rename. Updates <see cref="RelativePath"/> and <see cref="AbsolutePath"/>.
        /// </summary>
        ValueTask MoveAsync(
            string newAbsolutePath, string newRelativePath,
            bool replaceExisting, CancellationToken ct = default);

        /// <summary>
        /// Returns the underlying stream rewound to position 0, for hooks
        /// (versioning, search indexing) at close-time. The caller MUST NOT
        /// dispose this stream — the handle still owns it.
        /// Returns null for directories or non-readable handles.
        /// </summary>
        Stream? GetReadableSnapshot();

        void MarkDeleteOnClose();
    }
}
