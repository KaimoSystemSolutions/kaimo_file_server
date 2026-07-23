using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Infrastructure.Clouds;

public interface ICloudProviderFactory
{
    ICloudConnection Create(Guid shareId, SyncedFolder folder);
}