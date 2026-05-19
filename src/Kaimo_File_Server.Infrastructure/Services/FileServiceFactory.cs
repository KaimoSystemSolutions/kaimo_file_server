using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Storage;

namespace Kaimo_File_Server.Infrastructure.Services
{
    public class FileServiceFactory : IFileServiceFactory
    {
        private readonly IServiceProvider _serviceProvider;

        public FileServiceFactory(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public IFileService CreateForShare(Guid shareId, string sharePath)
        {
            var storage = new FileSystemStorage(sharePath, shareId, _serviceProvider);
            var aclService = new AclService(_serviceProvider);
            return new FileService(storage, aclService, shareId);
        }
    }
}