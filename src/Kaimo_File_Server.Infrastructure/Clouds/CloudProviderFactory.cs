using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.DataServices;

namespace Kaimo_File_Server.Infrastructure.Clouds;

/// <summary>
/// Thread-safe provider registry and authenticated-connection cache shared by
/// cloud-sync consumers.
/// </summary>
public class CloudProviderFactory : ICloudProviderFactory
{
    private readonly ConcurrentDictionary<ConnectionKey, CacheEntry> _connections;
    private readonly IReadOnlyDictionary<string, ICloudProvider> _providers;

    public CloudProviderFactory(IEnumerable<ICloudProvider> providers)
    {
        _connections = new ConcurrentDictionary<ConnectionKey, CacheEntry>();
        _providers = providers.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<ICloudProvider> Providers => _providers.Values.ToArray();

    /// <inheritdoc />
    public ICloudConnection CreateOrLoad(Guid shareId, SyncedFolder folder)
    {
        var key = CreateKey(shareId, folder);
        if (_connections.TryGetValue(key, out var existing) && existing.Holds(folder.Data))
            return existing.Connection;

        if (!_providers.TryGetValue(folder.Provider, out var provider))
            throw new NotSupportedException($"Cloud provider '{folder.Provider}' is not supported yet.");

        var created = new CacheEntry(folder.Data, provider.CreateConnection(shareId, folder));
        // ponytail: a replaced connection is dropped, not disposed — another sync may still
        // be using it; it is collected once that run ends. Add ref-counting if that ever leaks.
        return _connections.AddOrUpdate(
            key,
            created,
            (_, current) => current.Holds(folder.Data) ? current : created).Connection;
    }

    /// <inheritdoc />
    public async Task DisposeConnectionAsync(Guid shareId, SyncedFolder folder)
    {
        if (!_connections.TryRemove(CreateKey(shareId, folder), out var existing))
            return;

        await existing.Connection.Dispose();
    }

    /// <summary>
    /// Cache identity intentionally excludes mutable sync settings such as path,
    /// mode and last-run time. Editing a sync therefore keeps its authenticated
    /// provider connection alive.
    /// </summary>
    private static ConnectionKey CreateKey(Guid shareId, SyncedFolder folder)
    {
        // Providers with rotating credentials can supply an immutable connection
        // id. This keeps one cache slot per connection when a refresh token is
        // replaced and avoids retaining an unreachable cache entry.
        var credentials = folder.Data.TryGetValue("connectionId", out var connectionId)
            ? $"connectionId={connectionId}"
            : string.Join('\n', folder.Data
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => $"{entry.Key}={entry.Value}"));
        return new ConnectionKey(shareId, folder.Provider.ToLowerInvariant(), Fingerprint(credentials));
    }

    private static string Fingerprint(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record ConnectionKey(Guid ShareId, string ProviderId, string CredentialFingerprint);

    /// <summary>
    /// Providers rotate tokens by writing into the very dictionary they were
    /// created with, so <see cref="LiveData"/> always reflects what the cached
    /// connection currently holds. A stable connectionId keeps one cache slot per
    /// connection; comparing the live credentials against the stored ones then
    /// tells an own rotation (equal: reuse) from a re-authorization or a grant
    /// changed elsewhere (different: replace, instead of using the old grant until
    /// the process restarts).
    /// </summary>
    private sealed record CacheEntry(IReadOnlyDictionary<string, string> LiveData, ICloudConnection Connection)
    {
        /// <remarks>
        /// Only stored keys are compared: providers may add defaults (e.g. a
        /// scope) to their live data that were never persisted.
        /// </remarks>
        public bool Holds(IReadOnlyDictionary<string, string> stored)
        {
            try
            {
                return stored.All(entry => entry.Key == "connectionId"
                    || (LiveData.TryGetValue(entry.Key, out var live) && live == entry.Value));
            }
            catch (InvalidOperationException)
            {
                // Modified by a concurrent token refresh while being compared:
                // that refresh is the connection's own rotation, so keep it.
                return true;
            }
        }
    }
}
