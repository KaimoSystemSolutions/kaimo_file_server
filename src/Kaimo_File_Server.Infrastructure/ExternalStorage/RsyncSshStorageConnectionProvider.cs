using System.Security.Cryptography;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

public sealed class RsyncSshStorageConnectionProvider(
    IRsyncProcessRunner processRunner,
    ICredentialVault? credentialVault = null,
    IRsyncSshSetupService? setupService = null)
    : IStorageConnectionProvider
{
    public string Id => "rsync-ssh";
    public string DisplayName => "rsync / SSH";
    // Native rsync is intentionally sync-only: it does not provide the
    // item-level file contract required by browsing and virtual shares.
    public StorageProviderCapabilities Capabilities => StorageProviderCapabilities.Sync
        | StorageProviderCapabilities.OptimizedSync
        | StorageProviderCapabilities.Read
        | StorageProviderCapabilities.Write;
    public IReadOnlySet<StorageAuthorizationMode> AuthorizationModes { get; }
        = new HashSet<StorageAuthorizationMode> { StorageAuthorizationMode.SshKey };

    public async Task<IStorageSession> OpenSessionAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = ParseAndValidate(connection);
        (settings, string? temporaryKey) = await ResolvePrivateKeyAsync(
            connection, settings, cancellationToken);
        IStorageSession session = new RsyncOptimizedStorageSession(
            connection.Id,
            Capabilities,
            new RsyncSshOptimizedSync(settings, processRunner),
            temporaryKey);
        return session;
    }

    public async Task<StorageConnectionHealthResult> TestAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = ParseAndValidate(connection);
            (settings, string? temporaryKey) = await ResolvePrivateKeyAsync(
                connection, settings, cancellationToken);
            try
            {
                bool connected = await processRunner.TestSshAsync(settings, cancellationToken);
                return connected
                    ? SmbStorageConnectionProvider.Healthy()
                    : SmbStorageConnectionProvider.Unavailable("ssh_connection_failed");
            }
            finally
            {
                if (temporaryKey is not null)
                    File.Delete(temporaryKey);
            }
        }
        catch (ProtocolConfigurationException exception)
        {
            return SmbStorageConnectionProvider.Invalid(exception);
        }
        catch (IOException)
        {
            return SmbStorageConnectionProvider.Unavailable("ssh_secret_unavailable");
        }
        catch (UnauthorizedAccessException)
        {
            return SmbStorageConnectionProvider.Unavailable("ssh_secret_access_denied");
        }
    }

    public Task RevokeAsync(StorageConnection connection, CancellationToken cancellationToken = default)
        => DeleteManagedKnownHostAsync(setupService, connection);

    /// <summary>
    /// Removes the managed known_hosts file the guided SSH setup created for this
    /// connection (rsync and SFTP share it). Only files inside the managed directory
    /// are ever deleted; external secret references are left untouched.
    /// </summary>
    internal static async Task DeleteManagedKnownHostAsync(
        IRsyncSshSetupService? setupService, StorageConnection connection)
    {
        if (setupService is null)
            return;
        try
        {
            var settings = ProtocolConnectionSettings.Parse<RsyncSshConnectionSettings>(
                connection.SettingsJson, connection.ProviderId);
            await setupService.DeleteManagedKnownHostAsync(settings.KnownHostsSecretReference);
        }
        catch (ProtocolConfigurationException)
        {
            // Revocation is best-effort for an already-invalid connection.
        }
        catch (IOException)
        {
            // Revocation is best-effort after the connection record was deleted.
        }
        catch (UnauthorizedAccessException)
        {
            // Revocation is best-effort after the connection record was deleted.
        }
        catch (ArgumentException)
        {
            // Revocation is best-effort for malformed legacy paths.
        }
    }

    internal static RsyncSshConnectionSettings ParseAndValidate(StorageConnection connection)
        => PinnedSshSettings.ParseAndValidate(connection, "rsync-ssh", requireKnownHosts: true);

    private async Task<(RsyncSshConnectionSettings Settings, string? TemporaryKey)> ResolvePrivateKeyAsync(
        StorageConnection connection,
        RsyncSshConnectionSettings settings,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(settings.PrivateKeySecretReference))
            return (settings, null);
        if (!PinnedSshSettings.TryReadVaultPrivateKey(connection, credentialVault, out byte[] privateKey))
            throw new ProtocolConfigurationException(
                "private_key_missing", "The SSH private key is missing.");
        string temporary = ProtocolTemporaryFile.CreatePath();
        try
        {
            await ProtocolTemporaryFile.WriteRestrictedBytesAsync(
                temporary, privateKey, cancellationToken);
            return (settings with { PrivateKeySecretReference = temporary }, temporary);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }
}

internal sealed class RsyncOptimizedStorageSession(
    Guid connectionId,
    StorageProviderCapabilities capabilities,
    IOptimizedStorageSync optimizedSync,
    string? temporaryPrivateKey = null)
    : IStorageSession
{
    public Guid ConnectionId { get; } = connectionId;
    public StorageProviderCapabilities Capabilities { get; } = capabilities;
    public IRemoteFileStore? RemoteFiles => null;
    public IOptimizedStorageSync OptimizedSync { get; } = optimizedSync;
    IOptimizedStorageSync? IStorageSession.OptimizedSync => OptimizedSync;
    public ValueTask DisposeAsync()
    {
        if (temporaryPrivateKey is not null)
            File.Delete(temporaryPrivateKey);
        return ValueTask.CompletedTask;
    }
}

internal sealed class RsyncSshOptimizedSync(
    RsyncSshConnectionSettings settings,
    IRsyncProcessRunner processRunner) : IOptimizedStorageSync
{
    public Task<IReadOnlyList<string>> SynchronizeAsync(
        OptimizedSyncRequest request,
        CancellationToken cancellationToken = default)
        => processRunner.RunRsyncAsync(settings, request, cancellationToken);
}
