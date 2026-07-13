using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;
using Microsoft.Extensions.DependencyInjection;

namespace Kaimo_File_Server.Infrastructure.Services
{
    /// <summary>
    /// Lifetime-safe wrapper that persists file/directory ownership.
    ///
    /// The per-share <see cref="FileService"/> instances are created once (at SMB
    /// start-up) and live for the whole process, but <see cref="IFileMetadataRepository"/>
    /// depends on a scoped <c>ApplicationDbContext</c>. Capturing a single scoped instance
    /// would pin one DbContext for the server's lifetime, a concurrency and connection-leak
    /// hazard. This adapter therefore opens a fresh DI scope per call — the same pattern as
    /// <see cref="ScopedFileVersionService"/>.
    /// </summary>
    public sealed class ScopedFileOwnershipService : IFileOwnershipService
    {
        private readonly IServiceProvider _serviceProvider;

        public ScopedFileOwnershipService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider
                ?? throw new ArgumentNullException(nameof(serviceProvider));
        }

        public async Task EnsureOwnerAsync(
            Guid shareId, string shareRelativePath, bool isDirectory, Guid ownerUserId)
        {
            // No creator to record (e.g. a system/anonymous context) — leave ownership unset
            // rather than stamping an empty owner.
            if (ownerUserId == Guid.Empty) return;

            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IFileMetadataRepository>();

            // GetOrCreateAsync sets OwnerId only when it inserts a new row; an existing
            // row (e.g. one created by an earlier ACL assignment) keeps its current owner.
            await repo.GetOrCreateAsync(shareRelativePath, isDirectory, ownerUserId, shareId);
        }
    }
}
