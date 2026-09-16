using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.File;
using Microsoft.Extensions.DependencyInjection;

namespace Kaimo_File_Server.Infrastructure.Services
{
    /// <summary>
    /// Lifetime-safe wrapper around the scoped <see cref="IFileVersionService"/>.
    ///
    /// The per-share <see cref="FileService"/> instances are constructed per operation
    /// by <c>FileServiceFactory.CreateForShare</c>, but the real version service
    /// depends on a scoped <c>ApplicationDbContext</c>. Capturing a single scoped
    /// instance would pin one DbContext for the lifetime of the server, which is a
    /// concurrency and connection-leak hazard.
    ///
    /// This adapter therefore opens a fresh DI scope for every call, resolves the
    /// scoped service inside it, and disposes the scope once the operation
    /// completes — exactly the pattern <see cref="Core.Security.AclService"/> uses.
    /// Every returned value (streams own their memory or temporary file; lists are
    /// materialised) is safe to use after the scope is gone.
    /// </summary>
    public sealed class ScopedFileVersionService : IFileVersionService
    {
        private readonly IServiceProvider _serviceProvider;

        public ScopedFileVersionService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider
                ?? throw new ArgumentNullException(nameof(serviceProvider));
        }

        public async Task<FileVersion?> CreateVersionAsync(
            Guid shareId, string filePath, Stream content, string? userId = null)
        {
            using var scope = _serviceProvider.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFileVersionService>();
            return await svc.CreateVersionAsync(shareId, filePath, content, userId);
        }

        public async Task<Stream> ReadVersionAsync(Guid shareId, string filePath, DateTime snapshotTimestampUtc)
        {
            using var scope = _serviceProvider.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFileVersionService>();
            // The returned seekable stream owns all resources it needs and remains
            // usable after the scope (and its DbContext) is disposed.
            return await svc.ReadVersionAsync(shareId, filePath, snapshotTimestampUtc);
        }

        public async Task<Stream> ReadVersionAsync(
            Guid shareId, string filePath, DateTime snapshotTimestampUtc,
            CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFileVersionService>();
            return await svc.ReadVersionAsync(
                shareId, filePath, snapshotTimestampUtc, cancellationToken);
        }

        public async Task<List<FileVersion>> GetVersionsAsync(Guid shareId, string filePath)
        {
            using var scope = _serviceProvider.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFileVersionService>();
            return await svc.GetVersionsAsync(shareId, filePath);
        }

        public async Task<List<DateTime>> GetSnapshotTimestampsAsync(Guid shareId, string pathPrefix = "")
        {
            using var scope = _serviceProvider.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFileVersionService>();
            return await svc.GetSnapshotTimestampsAsync(shareId, pathPrefix);
        }

        public async Task<List<FileVersion>> GetFolderSnapshotAsync(Guid shareId, string folderPath, DateTime asOfUtc)
        {
            using var scope = _serviceProvider.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFileVersionService>();
            return await svc.GetFolderSnapshotAsync(shareId, folderPath, asOfUtc);
        }

        public async Task<List<FileVersion>> GetFolderSnapshotAsync(
            Guid shareId, string folderPath, DateTime asOfUtc,
            CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFileVersionService>();
            return await svc.GetFolderSnapshotAsync(
                shareId, folderPath, asOfUtc, cancellationToken);
        }

        public async Task<FileVersion?> GetVersionAtAsync(Guid shareId, string filePath, DateTime snapshotTimestampUtc)
        {
            using var scope = _serviceProvider.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFileVersionService>();
            return await svc.GetVersionAtAsync(shareId, filePath, snapshotTimestampUtc);
        }

        public async Task<int> ApplyRetentionAsync(
            Guid shareId, string filePath, int? maxVersions = null, TimeSpan? maxAge = null)
        {
            using var scope = _serviceProvider.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFileVersionService>();
            return await svc.ApplyRetentionAsync(shareId, filePath, maxVersions, maxAge);
        }

        public async Task RenamePathAsync(
            Guid shareId,
            string oldPath,
            string newPath,
            Guid? sambaLifecycleEventId = null)
        {
            using var scope = _serviceProvider.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFileVersionService>();
            await svc.RenamePathAsync(
                shareId, oldPath, newPath, sambaLifecycleEventId);
        }

        public async Task<int> DeletePathAsync(Guid shareId, string path)
        {
            using var scope = _serviceProvider.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFileVersionService>();
            return await svc.DeletePathAsync(shareId, path);
        }

        public async Task<int> DeleteShareAsync(Guid shareId)
        {
            using var scope = _serviceProvider.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFileVersionService>();
            return await svc.DeleteShareAsync(shareId);
        }

        public async Task<VersionRetentionSweepResult> SweepExpiredVersionsAsync(
            TimeSpan maxAge, int minVersionsToKeep, int maxPaths, CancellationToken cancellationToken = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFileVersionService>();
            return await svc.SweepExpiredVersionsAsync(maxAge, minVersionsToKeep, maxPaths, cancellationToken);
        }

        public async Task<OrphanBlobSweepResult> ReclaimOrphanBlobsAsync(
            IReadOnlyList<string> shardPrefixes, TimeSpan minimumAge, CancellationToken cancellationToken = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFileVersionService>();
            return await svc.ReclaimOrphanBlobsAsync(shardPrefixes, minimumAge, cancellationToken);
        }
    }
}
