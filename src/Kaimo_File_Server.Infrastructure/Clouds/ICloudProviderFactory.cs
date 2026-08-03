using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.DataServices;

namespace Kaimo_File_Server.Infrastructure.Clouds;

public interface ICloudProviderFactory
{
    IReadOnlyCollection<ICloudProvider> Providers { get; }
    ICloudConnection CreateOrLoad(Guid shareId, SyncedFolder folder);
    Task DisposeConnectionAsync(Guid shareId, SyncedFolder folder);
}
