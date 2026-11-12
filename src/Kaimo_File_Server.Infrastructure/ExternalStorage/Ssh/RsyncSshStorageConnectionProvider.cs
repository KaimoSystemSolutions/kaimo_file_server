using System.Security.Cryptography;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Security.Credentials;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Kaimo_File_Server.Infrastructure.ExternalStorage.Protocols;
using Kaimo_File_Server.Infrastructure.ExternalStorage.Smb;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage.Ssh;

public sealed class RsyncSshStorageConnectionProvider(
    IRsyncProcessRunner processRunner,
    ICredentialVault? credentialVault = null,
    IRsyncSshSetupService? setupService = null,
    IConfiguration? configuration = null,
    ILogger<RsyncSshStorageConnectionProvider>? logger = null)
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
        = new HashSet<StorageAuthorizationMode> { StorageAuthorizationMode.SshKey, StorageAuthorizationMode.UsernamePassword };

    public async Task<IStorageSession> OpenSessionAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = ParseAndValidate(connection);
        (settings, string? temporarySecret) = await ResolveSecretAsync(
            connection, settings, cancellationToken);
        IStorageSession session = new RsyncOptimizedStorageSession(
            connection.Id,
            Capabilities,
            new RsyncSshOptimizedSync(settings, processRunner),
            temporarySecret,
            logger);
        return session;
    }

    public async Task<StorageConnectionHealthResult> TestAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = ParseAndValidate(connection);
            (settings, string? temporarySecret) = await ResolveSecretAsync(
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
                ProtocolTemporaryFile.TryDelete(temporarySecret, logger);
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

    /// <summary>
    /// Materializes the connection's secret for the OpenSSH client and returns the temporary
    /// file or directory the caller must remove (null for an external secret file).
    /// </summary>
    private async Task<(RsyncSshConnectionSettings Settings, string? TemporarySecret)> ResolveSecretAsync(
        StorageConnection connection,
        RsyncSshConnectionSettings settings,
        CancellationToken cancellationToken)
    {
        if (connection.AuthorizationMode == StorageAuthorizationMode.UsernamePassword)
            return await WriteAskpassAsync(connection, settings, cancellationToken);
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

    private async Task<(RsyncSshConnectionSettings Settings, string? TemporarySecret)> WriteAskpassAsync(
        StorageConnection connection,
        RsyncSshConnectionSettings settings,
        CancellationToken cancellationToken)
    {
        if (!PinnedSshSettings.TryReadVaultPassword(connection, credentialVault, out string password))
            throw new ProtocolConfigurationException("ssh_password_missing", "The SSH password is missing.");
        string directory = ProtocolTemporaryFile.CreatePath(configuration?[ProtocolTemporaryFile.TempDirectoryKey]);
        try
        {
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(directory);
            else
                Directory.CreateDirectory(
                    directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string askpass = await ProtocolTemporaryFile.WriteAskpassAsync(directory, password, cancellationToken);
            return (settings with { AskpassPath = askpass }, directory);
        }
        catch
        {
            ProtocolTemporaryFile.TryDelete(directory, logger);
            throw;
        }
    }
}

internal sealed class RsyncOptimizedStorageSession(
    Guid connectionId,
    StorageProviderCapabilities capabilities,
    IOptimizedStorageSync optimizedSync,
    string? temporarySecret = null,
    ILogger? logger = null)
    : IStorageSession
{
    public Guid ConnectionId { get; } = connectionId;
    public StorageProviderCapabilities Capabilities { get; } = capabilities;
    public IRemoteFileStore? RemoteFiles => null;
    public IOptimizedStorageSync OptimizedSync { get; } = optimizedSync;
    IOptimizedStorageSync? IStorageSession.OptimizedSync => OptimizedSync;
    public ValueTask DisposeAsync()
    {
        ProtocolTemporaryFile.TryDelete(temporarySecret, logger);
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

    public Task<IReadOnlyList<RemoteStorageItem>> ListDirectoriesAsync(
        string path,
        CancellationToken cancellationToken = default)
        => processRunner.ListDirectoriesAsync(settings, path, cancellationToken);
}
