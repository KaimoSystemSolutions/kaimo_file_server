using Kaimo_File_Server.Core.Domain;
using Microsoft.Extensions.Configuration;

namespace Kaimo_File_Server.Infrastructure.Clouds;

public class CloudProviderFactory : ICloudProviderFactory
{
    private readonly Dictionary<SyncedFolder, ICloudConnection> _connections;
    private readonly IConfiguration _configuration;

    public CloudProviderFactory(IConfiguration configuration)
    {
        _configuration = configuration;
        _connections = new Dictionary<SyncedFolder, ICloudConnection>();
    }

    public ICloudConnection CreateOrLoad(Guid shareId, SyncedFolder folder)
    {
        if(_connections.TryGetValue(folder, out var existingConnection))
            return existingConnection;
        
        ICloudConnection connection = folder.Provider.ToLowerInvariant() switch
        {
            "google" => new GoogleDriveConnection(shareId, folder.Data, _configuration),
            _ => throw new NotSupportedException(
                $"Cloud provider '{folder.Provider}' is not supported yet.")
        };
        
        _connections[folder] = connection;
        return connection;
    }

    public void DisposeConnection(SyncedFolder folder)
    {
        if(!_connections.TryGetValue(folder, out var existingConnection))
            return;

        existingConnection.Dispose();
        _connections.Remove(folder);
    }
}