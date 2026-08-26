using Kaimo_File_Server.Core.Domain.ClientSync;

namespace Kaimo_File_Server.Core.Services.File
{
    /// <summary>
    /// A thin append port the <see cref="FileService"/> uses to record a mutation in the per-share
    /// change log. Implemented by a lifetime-safe adapter that opens its own DI scope per call,
    /// because <see cref="FileService"/> instances outlive the scoped database context.
    ///
    /// Appends are best-effort: a failure here must never fail the underlying file operation.
    /// </summary>
    public interface IFileChangeLog
    {
        /// <summary>Records a single mutation. Paths are share-relative (forward slashes).</summary>
        Task AppendAsync(
            Guid shareId,
            FileChangeType type,
            string path,
            bool isDirectory,
            string? oldPath = null,
            long? size = null,
            DateTime? modifiedAtUtc = null,
            CancellationToken ct = default);
    }
}
