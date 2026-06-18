using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Storage;
using Kaimo_File_Server.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Services
{
    public class FileServiceFactory : IFileServiceFactory
    {
        private readonly IServiceProvider _serviceProvider;

        private readonly ISearchService _searchService;
        
        public FileServiceFactory(IServiceProvider serviceProvider, ISearchService searchService)
        {
            _serviceProvider = serviceProvider
                ?? throw new ArgumentNullException(nameof(serviceProvider));

            _searchService = searchService;
        }

        public IFileService CreateForShare(Guid shareId, string sharePath)
        {
            var storage = new FileSystemStorage(sharePath, shareId, _serviceProvider);
            var aclService = new AclService(_serviceProvider);
            return new FileService(storage, aclService, _searchService, shareId);
        }
    }
}