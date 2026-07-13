using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.File;
using Microsoft.Extensions.DependencyInjection;

namespace Kaimo_File_Server.Infrastructure.Services
{
    /// <summary>
    /// Lifetime-safe wrapper around the scoped <see cref="IFileVersionService"/>.
    ///
    /// The per-share <see cref="FileService"/> instances are created once (at SMB
    /// start-up) and live for the whole process, but the real version service
    /// depends on a scoped <c>ApplicationDbContext</c>. Capturing a single scoped
    /// instance would pin one DbContext for the lifetime of the server, which is a
    /// concurrency and connection-leak hazard.
    ///
    /// This adapter therefore opens a fresh DI scope for every call, resolves the
    /// scoped service inside it, and disposes the scope once the operation
    /// completes — exactly the pattern <see cref="Core.Security.AclService"/> uses.
    /// Every returned value (streams are fully buffered, lists are materialised)
    /// is safe to use after the scope is gone.
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
            // FileVersionService returns a fully buffered MemoryStream, so it
            // remains usable after the scope (and its DbContext) is disposed.
            return await svc.ReadVersionAsync(shareId, filePath, snapshotTimestampUtc);
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
    }
}
