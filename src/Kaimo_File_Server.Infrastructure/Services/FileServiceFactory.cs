using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Services
{
    public class FileServiceFactory : IFileServiceFactory
    {
        private readonly IServiceProvider _serviceProvider;

        public FileServiceFactory(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider
                ?? throw new ArgumentNullException(nameof(serviceProvider));
        }

        public IFileService CreateForShare(Guid shareId, string sharePath)
        {
            var storage = new FileSystemStorage(sharePath, shareId, _serviceProvider);
            var aclService = new AclService(_serviceProvider);

            // Versioning depends on a scoped DbContext, but the FileService lives
            // for the whole process — wrap it so each call gets its own DI scope.
            var versionService = new ScopedFileVersionService(_serviceProvider);

            // Same scoping concern for ownership persistence (writes a FileMetadata row).
            var ownershipService = new ScopedFileOwnershipService(_serviceProvider);

            // Same scoping concern for the per-share change log (appends one row per mutation).
            var changeLog = new ScopedFileChangeLog(_serviceProvider);

            // Elasticsearch is no longer written from the file-operation path: the
            // SearchIndexingService tails the change log and owns all indexing. Passing
            // null keeps every ES call in FileService inert (all are null-guarded).
            return new FileService(
                storage, aclService, null, shareId, versionService,
                ownershipService,
                _serviceProvider.GetRequiredService<ILogger<FileService>>(),
                _serviceProvider.GetRequiredService<ICloudSyncPathUpdater>(),
                _serviceProvider.GetRequiredService<ICloudSyncOperationCoordinator>(),
                changeLog);
        }
    }
}
