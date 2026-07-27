using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.DataServices;

namespace Kaimo_File_Server.Infrastructure.Clouds;

public interface ICloudProviderFactory
{
    ICloudConnection CreateOrLoad(Guid shareId, SyncedFolder folder);
    public void DisposeConnection(SyncedFolder folder);
    
}