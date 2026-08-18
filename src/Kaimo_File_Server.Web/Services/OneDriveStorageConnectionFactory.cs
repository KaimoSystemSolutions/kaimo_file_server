using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Clouds;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Opens OneDrive clients whose refresh-token exchange, reload, rotation write,
/// and acknowledgement are one distributed critical section.
/// </summary>
public sealed class OneDriveStorageConnectionFactory(
    IStorageConnectionRepository connections,
    ICredentialVault credentialVault,
    IStorageConnectionCredentialLeaseManager leases,
    IHttpClientFactory httpClientFactory)
{
    private static readonly TimeSpan AcquisitionTimeout = TimeSpan.FromSeconds(30);

    public OneDriveConnection Create(StorageConnection record, string clientName = "CloudAccessOneDrive")
    {
        ArgumentNullException.ThrowIfNull(record);
        var credentials = credentialVault.UnprotectConnectionCredentials(record);
        return new OneDriveConnection(
            credentials,
            httpClientFactory.CreateClient(clientName),
            cancellationToken => AcquireAsync(record.Id, cancellationToken),
            cancellationToken => ReloadAsync(record.Id, cancellationToken),
            (rotated, cancellationToken) => PersistAsync(record.Id, rotated, cancellationToken));
    }

    private async Task<IAsyncDisposable?> AcquireAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(AcquisitionTimeout);
        do
        {
            var lease = await leases.TryAcquireAsync(connectionId, cancellationToken);
            if (lease is not null) return lease;
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        } while (DateTimeOffset.UtcNow < deadline);
        return null;
    }

    private async Task<Dictionary<string, string>> ReloadAsync(
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        var latest = await connections.GetAsync(connectionId, cancellationToken)
                     ?? throw new InvalidOperationException("Storage connection no longer exists.");
        return credentialVault.UnprotectConnectionCredentials(latest);
    }

    private async Task PersistAsync(
        Guid connectionId,
        Dictionary<string, string> rotatedCredentials,
        CancellationToken cancellationToken)
    {
        var latest = await connections.GetAsync(connectionId, cancellationToken)
                     ?? throw new InvalidOperationException("Storage connection no longer exists.");
        await connections.UpdateRuntimeAsync(
            connectionId,
            credentialVault.ProtectConnectionCredentials(latest, rotatedCredentials),
            null,
            null,
            StorageConnectionState.Ready,
            null,
            cancellationToken);
    }
}
