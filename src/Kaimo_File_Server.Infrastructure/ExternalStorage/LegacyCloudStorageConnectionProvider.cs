using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.Clouds;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

/// <summary>
/// Capability adapter that exposes the existing OneDrive and Google connection
/// implementations through the neutral Package 7 contract during migration.
/// </summary>
public sealed class LegacyCloudStorageConnectionProvider(
    string id,
    string displayName,
    StorageProviderCapabilities capabilities,
    IReadOnlySet<StorageAuthorizationMode> authorizationModes,
    ICredentialVault credentialVault,
    IStorageConnectionRepository repository,
    ICloudProviderFactory cloudProviders) : IStorageConnectionProvider
{
    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;
    public StorageProviderCapabilities Capabilities { get; } = capabilities;
    public IReadOnlySet<StorageAuthorizationMode> AuthorizationModes { get; } = authorizationModes;

    public Task<IStorageSession> OpenSessionAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(connection);
        var credentials = credentialVault.UnprotectConnectionCredentials(connection);
        credentials["connectionId"] = connection.Id.ToString("D");
        var folder = new SyncedFolder(connection.ProviderId, credentials, "/");
        var cloudConnection = cloudProviders.CreateOrLoad(Guid.Empty, folder);
        IStorageSession session = new LegacyCloudStorageSession(
            connection.Id,
            Capabilities,
            new LegacyCloudRemoteFileStore(
                connection,
                credentials,
                cloudConnection,
                credentialVault,
                repository));
        return Task.FromResult(session);
    }

    public async Task<StorageConnectionHealthResult> TestAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = await OpenSessionAsync(connection, cancellationToken);
            await session.RemoteFiles!.ListAsync("/", cancellationToken);
            return SmbStorageConnectionProvider.Healthy();
        }
        catch (ProtocolConfigurationException exception)
        {
            return SmbStorageConnectionProvider.Invalid(exception);
        }
        catch (ProviderRequestException exception)
        {
            return new StorageConnectionHealthResult(
                StorageConnectionHealthState.Unavailable,
                exception.ErrorCode,
                DateTime.UtcNow);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return SmbStorageConnectionProvider.Unavailable("provider_request_failed");
        }
    }

    public async Task RevokeAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default)
    {
        Validate(connection);
        var credentials = credentialVault.UnprotectConnectionCredentials(connection);
        credentials["connectionId"] = connection.Id.ToString("D");
        await cloudProviders.RevokeAndEvictAsync(
            Guid.Empty,
            new SyncedFolder(connection.ProviderId, credentials, "/"));
    }

    private void Validate(StorageConnection connection)
    {
        if (!string.Equals(connection.ProviderId, Id, StringComparison.OrdinalIgnoreCase)
            || !AuthorizationModes.Contains(connection.AuthorizationMode)
            || string.IsNullOrWhiteSpace(connection.EncryptedCredentialPayload))
            throw new ProtocolConfigurationException("connection_mode_invalid", "The cloud connection is not ready for this provider.");
    }
}

internal sealed class LegacyCloudStorageSession(
    Guid connectionId,
    StorageProviderCapabilities capabilities,
    IRemoteFileStore remoteFiles) : IStorageSession
{
    public Guid ConnectionId { get; } = connectionId;
    public StorageProviderCapabilities Capabilities { get; } = capabilities;
    public IRemoteFileStore RemoteFiles { get; } = remoteFiles;
    IRemoteFileStore? IStorageSession.RemoteFiles => RemoteFiles;
    public IOptimizedStorageSync? OptimizedSync => null;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class LegacyCloudRemoteFileStore(
    StorageConnection connection,
    Dictionary<string, string> credentials,
    ICloudConnection cloudConnection,
    ICredentialVault credentialVault,
    IStorageConnectionRepository repository) : IRemoteFileStore
{
    public async Task<IReadOnlyList<RemoteStorageItem>> ListAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var items = await cloudConnection.ListAsync(path, cancellationToken);
        await PersistRotationAsync(cancellationToken);
        return items.Select(item => new RemoteStorageItem(
            item.Name, item.Path, item.IsDirectory, item.Size, item.ModifiedAt)).ToArray();
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var stream = new MemoryStream();
        await cloudConnection.DownloadAsync(path, stream, cancellationToken);
        stream.Position = 0;
        await PersistRotationAsync(cancellationToken);
        return stream;
    }

    public async Task WriteAsync(
        string path,
        Stream content,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        if (!overwrite)
        {
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "/";
            string name = Path.GetFileName(path);
            if ((await cloudConnection.ListAsync(parent, cancellationToken))
                .Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("The remote item already exists.");
        }
        await cloudConnection.UploadAsync(path, content, DateTime.UtcNow, cancellationToken);
        await PersistRotationAsync(cancellationToken);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        await cloudConnection.CreateDirectoryAsync(path, cancellationToken);
        await PersistRotationAsync(cancellationToken);
    }

    public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This provider does not expose delete through the current adapter.");

    public Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This provider does not expose move through the current adapter.");

    private async Task PersistRotationAsync(CancellationToken cancellationToken)
    {
        if (!cloudConnection.HasPendingCredentialChanges)
            return;
        foreach (var (key, value) in cloudConnection.GetPendingCredentialChanges())
            credentials[key] = value;
        credentials.Remove("connectionId");
        string protectedGrant = credentialVault.ProtectConnectionCredentials(connection, credentials);
        await repository.UpdateRuntimeAsync(
            connection.Id,
            protectedGrant,
            connection.AccountDisplayName,
            connection.AccountEmail,
            StorageConnectionState.Ready,
            null,
            cancellationToken);
        cloudConnection.AcknowledgeCredentialChanges();
    }
}
