using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;
using Microsoft.Extensions.DependencyInjection;

namespace Kaimo_File_Server.Infrastructure.Services
{
    /// <summary>
    /// Lifetime-safe <see cref="IFileChangeLog"/> adapter that appends change-log entries.
    ///
    /// The per-share <see cref="FileService"/> instances are created once (at SMB start-up) and
    /// live for the whole process, but <see cref="IFileChangeLogRepository"/> depends on a scoped
    /// <c>ApplicationDbContext</c>. This adapter therefore opens a fresh DI scope per call — the
    /// same pattern as <see cref="ScopedFileOwnershipService"/> and <see cref="ScopedFileVersionService"/>.
    /// </summary>
    public sealed class ScopedFileChangeLog : IFileChangeLog
    {
        private readonly IServiceProvider _serviceProvider;

        public ScopedFileChangeLog(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider
                ?? throw new ArgumentNullException(nameof(serviceProvider));
        }

        public async Task AppendAsync(
            Guid shareId,
            FileChangeType type,
            string path,
            bool isDirectory,
            string? oldPath = null,
            long? size = null,
            DateTime? modifiedAtUtc = null,
            CancellationToken ct = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IFileChangeLogRepository>();

            await repo.AppendAsync(new FileChangeLogEntry
            {
                ShareId = shareId,
                ChangeType = type,
                Path = path,
                OldPath = oldPath,
                IsDirectory = isDirectory,
                Size = size,
                ModifiedAtUtc = modifiedAtUtc,
                CreatedAtUtc = DateTime.UtcNow,
            }, ct);
        }
    }
}
