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
    private readonly ConcurrentDictionary<ConnectionKey, ICloudConnection> _connections;
    private readonly IReadOnlyDictionary<string, ICloudProvider> _providers;

    public CloudProviderFactory(IEnumerable<ICloudProvider> providers)
    {
        _connections = new ConcurrentDictionary<ConnectionKey, ICloudConnection>();
        _providers = providers.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<ICloudProvider> Providers => _providers.Values.ToArray();

    /// <inheritdoc />
    public ICloudConnection CreateOrLoad(Guid shareId, SyncedFolder folder)
    {
        var key = CreateKey(shareId, folder);
        if (_connections.TryGetValue(key, out var existingConnection))
            return existingConnection;

        if (!_providers.TryGetValue(folder.Provider, out var provider))
            throw new NotSupportedException($"Cloud provider '{folder.Provider}' is not supported yet.");

        var connection = provider.CreateConnection(shareId, folder);
        var winner = _connections.GetOrAdd(key, connection);
        if (!ReferenceEquals(winner, connection))
            // Another thread won the race: close the one we built (never revoke — that
            // would invalidate the credential the winner is using) and serve the winner.
            _ = CloseQuietlyAsync(connection);
        return winner;
    }

    /// <inheritdoc />
    public Task EvictAsync(Guid shareId, SyncedFolder folder)
    {
        if (_connections.TryRemove(CreateKey(shareId, folder), out var connection))
            _ = CloseQuietlyAsync(connection);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task EvictShareAsync(Guid shareId)
    {
        foreach (var entry in _connections.Where(e => e.Key.ShareId == shareId).ToList())
            if (_connections.TryRemove(entry.Key, out var connection))
                _ = CloseQuietlyAsync(connection);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task EvictConnectionAsync(Guid connectionId)
    {
        foreach (var entry in _connections.Where(e => e.Key.ConnectionId == connectionId).ToList())
            if (_connections.TryRemove(entry.Key, out var connection))
                _ = CloseQuietlyAsync(connection);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task RevokeAndEvictAsync(Guid shareId, SyncedFolder folder)
    {
        if (!_connections.TryRemove(CreateKey(shareId, folder), out var existingConnection))
            return;

        await existingConnection.RevokeAndCloseAsync();
    }

    /// <summary>Closes a connection, swallowing any local-cleanup failure. Never revokes.</summary>
    private static async Task CloseQuietlyAsync(ICloudConnection connection)
    {
        try { await connection.CloseAsync(); }
        catch { /* local cleanup is best-effort; a failure must not surface here */ }
    }

    /// <summary>
    /// Cache identity intentionally excludes mutable sync settings such as path,
    /// mode and last-run time. Editing a sync therefore keeps its authenticated
    /// provider connection alive.
    /// </summary>
    private static ConnectionKey CreateKey(Guid shareId, SyncedFolder folder)
    {
        // Providers with rotating credentials can supply an immutable connection id.
        // The id is carried on the key explicitly (so it can be evicted directly) AND
        // still drives the fingerprint, so credential rotation reuses the connection.
        var hasConnectionId = folder.Data.TryGetValue("connectionId", out var connectionIdText)
                              && Guid.TryParse(connectionIdText, out var parsedId);
        Guid? connectionId = hasConnectionId ? Guid.Parse(folder.Data["connectionId"]) : null;

        var credentials = connectionId is not null
            ? $"connectionId={connectionId}"
            : string.Join('\n', folder.Data
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => $"{entry.Key}={entry.Value}"));
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(credentials)));
        return new ConnectionKey(shareId, folder.Provider.ToLowerInvariant(), connectionId, fingerprint);
    }

    private sealed record ConnectionKey(
        Guid ShareId, string ProviderId, Guid? ConnectionId, string CredentialFingerprint);
}
