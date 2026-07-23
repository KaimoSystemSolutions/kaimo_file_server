using Kaimo_File_Server.Core.Domain;
using Microsoft.Extensions.Configuration;

namespace Kaimo_File_Server.Infrastructure.Clouds;

public class CloudProviderFactory : ICloudProviderFactory
{
    private readonly IConfiguration _configuration;

    public CloudProviderFactory(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public ICloudConnection Create(Guid shareId, SyncedFolder folder)
    {
        ICloudConnection connection = folder.Provider.ToLowerInvariant() switch
        {
            "google" => new GoogleDriveConnection(shareId, folder.Data, _configuration),
            _ => throw new NotSupportedException(
                $"Cloud provider '{folder.Provider}' is not supported yet.")
        };

        folder.Connection = connection;
        return connection;
    }
}