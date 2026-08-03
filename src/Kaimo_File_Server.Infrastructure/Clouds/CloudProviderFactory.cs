using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.DataServices;

namespace Kaimo_File_Server.Infrastructure.Clouds;

public class CloudProviderFactory : ICloudProviderFactory
{
    private readonly ConcurrentDictionary<ConnectionKey, ICloudConnection> _connections;
    private readonly IReadOnlyDictionary<string, ICloudProvider> _providers;

    public CloudProviderFactory(IEnumerable<ICloudProvider> providers)
    {
        _connections = new ConcurrentDictionary<ConnectionKey, ICloudConnection>();
        _providers = providers.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<ICloudProvider> Providers => _providers.Values.ToArray();

    public ICloudConnection CreateOrLoad(Guid shareId, SyncedFolder folder)
    {
        var key = CreateKey(shareId, folder);
        if (_connections.TryGetValue(key, out var existingConnection))
            return existingConnection;
        
        if (!_providers.TryGetValue(folder.Provider, out var provider))
            throw new NotSupportedException($"Cloud provider '{folder.Provider}' is not supported yet.");

        ICloudConnection connection = provider.CreateConnection(shareId, folder);
        
        return _connections.GetOrAdd(key, connection);
    }

    public async Task DisposeConnectionAsync(Guid shareId, SyncedFolder folder)
    {
        if (!_connections.TryRemove(CreateKey(shareId, folder), out var existingConnection))
            return;

        await existingConnection.Dispose();
    }

    /// <summary>
    /// Cache identity intentionally excludes mutable sync settings such as path,
    /// mode and last-run time. Editing a sync therefore keeps its authenticated
    /// provider connection alive.
    /// </summary>
    private static ConnectionKey CreateKey(Guid shareId, SyncedFolder folder)
    {
        var credentials = string.Join('\n', folder.Data
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"{entry.Key}={entry.Value}"));
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(credentials)));
        return new ConnectionKey(shareId, folder.Provider.ToLowerInvariant(), fingerprint);
    }

    private sealed record ConnectionKey(Guid ShareId, string ProviderId, string CredentialFingerprint);
}
