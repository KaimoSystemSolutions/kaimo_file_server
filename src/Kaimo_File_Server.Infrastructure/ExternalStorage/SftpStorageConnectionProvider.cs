using System.Security.Cryptography;
using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

/// <summary>
/// SFTP (SSH File Transfer Protocol) browse provider. Unlike the sync-only
/// rsync-over-SSH transport, SFTP exposes a full item-level file contract and
/// can therefore back a virtual share. It shares the pinned-SSH trust model:
/// the private key comes from the connection credential vault (or an external
/// secret file) and the server identity is verified in process against the
/// pinned SHA-256 host-key fingerprint the administrator confirmed.
/// </summary>
public sealed class SftpStorageConnectionProvider(
    ICredentialVault credentialVault) : IStorageConnectionProvider
{
    public string Id => "sftp";
    public string DisplayName => "SFTP";
    // Mirrors SMB: a browsable, read-write remote file store that also
    // participates in the file-based reconciliation sync engine.
    public StorageProviderCapabilities Capabilities =>
        SmbStorageConnectionProvider.ReadWriteCapabilities
        | StorageProviderCapabilities.DirectFileAccess;
    public IReadOnlySet<StorageAuthorizationMode> AuthorizationModes { get; }
        = new HashSet<StorageAuthorizationMode> { StorageAuthorizationMode.SshKey };

    public async Task<IStorageSession> OpenSessionAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = PinnedSshSettings.ParseAndValidate(connection, "sftp", requireKnownHosts: false);
        return await OpenConnectedSessionAsync(connection, settings, cancellationToken);
    }

    public async Task<StorageConnectionHealthResult> TestAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = PinnedSshSettings.ParseAndValidate(connection, "sftp", requireKnownHosts: false);
            await using var session = await OpenConnectedSessionAsync(connection, settings, cancellationToken);
            // Listing the configured root confirms both reachability and that
            // the account can actually see its backup root.
            await ((SftpRemoteFileStore)session.RemoteFiles!).ListAsync("/", cancellationToken);
            return SmbStorageConnectionProvider.Healthy();
        }
        catch (ProtocolConfigurationException exception)
        {
            return SmbStorageConnectionProvider.Invalid(exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SshAuthenticationException)
        {
            return SmbStorageConnectionProvider.Unavailable("ssh_authentication_failed");
        }
        catch (SshException)
        {
            return SmbStorageConnectionProvider.Unavailable("ssh_connection_failed");
        }
        catch (IOException)
        {
            return SmbStorageConnectionProvider.Unavailable("ssh_connection_failed");
        }
        catch (System.Net.Sockets.SocketException)
        {
            return SmbStorageConnectionProvider.Unavailable("ssh_connection_failed");
        }
    }

    public Task RevokeAsync(StorageConnection connection, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    private async Task<SftpStorageSession> OpenConnectedSessionAsync(
        StorageConnection connection,
        RsyncSshConnectionSettings settings,
        CancellationToken cancellationToken)
    {
        PrivateKeyFile key = LoadPrivateKey(connection, settings);
        SftpClient? client = null;
        try
        {
            var authentication = new PrivateKeyAuthenticationMethod(settings.Username, key);
            var connectionInfo = new ConnectionInfo(
                settings.Host, settings.Port, settings.Username, authentication)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            client = new SftpClient(connectionInfo)
            {
                OperationTimeout = TimeSpan.FromSeconds(60)
            };

            // In-process host-key pinning. The server key is trusted only when
            // its SHA-256 fingerprint matches the value the administrator
            // verified through an independent channel during setup.
            bool hostKeyMatched = false;
            client.HostKeyReceived += (_, hostKey) =>
            {
                string fingerprint = PinnedSshSettings.ComputeFingerprint(hostKey.HostKey);
                hostKey.CanTrust = CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(fingerprint),
                    Encoding.ASCII.GetBytes(settings.ExpectedHostKeySha256));
                hostKeyMatched = hostKey.CanTrust;
            };

            try
            {
                await client.ConnectAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A rejected host key surfaces as a connection exception; classify
                // it as an identity failure rather than a transient outage so the
                // UI never silently re-pins a changed server.
                if (!hostKeyMatched)
                    throw new ProtocolConfigurationException(
                        "host_key_mismatch", "The pinned SSH host identity does not match the server.", exception);
                throw;
            }

            var session = new SftpStorageSession(
                connection.Id, Capabilities, client, key,
                new SftpRemoteFileStore(client, settings.RemoteRoot));
            client = null;
            key = null!;
            return session;
        }
        finally
        {
            client?.Dispose();
            (key as IDisposable)?.Dispose();
        }
    }

    private PrivateKeyFile LoadPrivateKey(StorageConnection connection, RsyncSshConnectionSettings settings)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(settings.PrivateKeySecretReference))
                return new PrivateKeyFile(settings.PrivateKeySecretReference);
            if (!PinnedSshSettings.TryReadVaultPrivateKey(connection, credentialVault, out byte[] keyBytes))
                throw new ProtocolConfigurationException("private_key_missing", "The SSH private key is missing.");
            try
            {
                using var stream = new MemoryStream(keyBytes, writable: false);
                return new PrivateKeyFile(stream);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(keyBytes);
            }
        }
        catch (SshException exception)
        {
            throw new ProtocolConfigurationException(
                "private_key_invalid",
                "The SSH private key is unsupported, invalid, or requires a passphrase.", exception);
        }
    }
}

internal sealed class SftpStorageSession(
    Guid connectionId,
    StorageProviderCapabilities capabilities,
    SftpClient client,
    PrivateKeyFile privateKey,
    IRemoteFileStore remoteFiles) : IStorageSession
{
    public Guid ConnectionId { get; } = connectionId;
    public StorageProviderCapabilities Capabilities { get; } = capabilities;
    public IRemoteFileStore RemoteFiles { get; } = remoteFiles;
    IRemoteFileStore? IStorageSession.RemoteFiles => RemoteFiles;
    public IOptimizedStorageSync? OptimizedSync => null;

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        (privateKey as IDisposable)?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Maps the provider-neutral <see cref="IRemoteFileStore"/> contract onto an
/// SFTP session. All incoming paths are connection-root-relative; they are
/// normalized, rejected on traversal, and resolved beneath the configured
/// remote root before any server request. Symbolic links are never traversed.
/// </summary>
internal sealed class SftpRemoteFileStore(ISftpClient client, string remoteRoot) : IRemoteFileStore
{
    public async Task<IReadOnlyList<RemoteStorageItem>> ListAsync(
        string path, CancellationToken cancellationToken = default)
    {
        string storePath = SftpPaths.NormalizeStorePath(path);
        string serverPath = SftpPaths.ToServerPath(remoteRoot, storePath);
        var items = new List<RemoteStorageItem>();
        await foreach (ISftpFile entry in client.ListDirectoryAsync(serverPath, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (entry.Name is "." or ".." || entry.Name.Length == 0 || entry.IsSymbolicLink)
                continue;
            items.Add(new RemoteStorageItem(
                entry.Name,
                SftpPaths.CombineChild(storePath, entry.Name),
                entry.IsDirectory,
                entry.IsDirectory ? null : entry.Length,
                entry.LastWriteTimeUtc));
        }
        return items;
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        string serverPath = ResolveFile(path);
        return await client.OpenAsync(serverPath, FileMode.Open, FileAccess.Read, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task WriteAsync(
        string path, Stream content, bool overwrite, CancellationToken cancellationToken = default)
    {
        string serverPath = ResolveFile(path);
        if (!overwrite && await client.ExistsAsync(serverPath, cancellationToken).ConfigureAwait(false))
            throw new IOException("The remote item already exists.");
        await using SftpFileStream target = await client.OpenAsync(
            serverPath,
            overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write,
            cancellationToken).ConfigureAwait(false);
        await content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        string serverPath = ResolveFile(path);
        await client.CreateDirectoryAsync(serverPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default)
    {
        string serverPath = ResolveFile(path);
        ISftpFile entry = await client.GetAsync(serverPath, cancellationToken).ConfigureAwait(false);
        await DeleteEntryAsync(entry, recursive, cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveAsync(
        string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        string source = ResolveFile(sourcePath);
        string destination = ResolveFile(destinationPath);
        await client.RenameFileAsync(source, destination, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteEntryAsync(
        ISftpFile entry, bool recursive, CancellationToken cancellationToken)
    {
        // A symbolic link is unlinked directly; its target is never followed.
        if (entry.IsSymbolicLink || !entry.IsDirectory)
        {
            await client.DeleteFileAsync(entry.FullName, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (recursive)
        {
            await foreach (ISftpFile child in client.ListDirectoryAsync(entry.FullName, cancellationToken)
                               .ConfigureAwait(false))
            {
                if (child.Name is "." or "..")
                    continue;
                await DeleteEntryAsync(child, recursive: true, cancellationToken).ConfigureAwait(false);
            }
        }
        await client.DeleteDirectoryAsync(entry.FullName, cancellationToken).ConfigureAwait(false);
    }

    private string ResolveFile(string path)
    {
        string storePath = SftpPaths.NormalizeStorePath(path);
        if (storePath == "/")
            throw new ProtocolConfigurationException("remote_path_invalid", "The remote root is not a file path.");
        return SftpPaths.ToServerPath(remoteRoot, storePath);
    }
}

/// <summary>Pure, testable path handling for the SFTP file store.</summary>
internal static class SftpPaths
{
    /// <summary>
    /// Normalizes a connection-root-relative path to a rooted <c>/a/b</c> form,
    /// rejecting traversal, control characters, and overlong input.
    /// </summary>
    public static string NormalizeStorePath(string? path)
    {
        string value = (path ?? string.Empty).Replace('\\', '/').Trim();
        if (value.Length > 4096 || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ProtocolConfigurationException("remote_path_invalid", "The remote path is invalid.");
        string[] segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
            throw new ProtocolConfigurationException("remote_path_invalid", "The remote path is invalid.");
        return segments.Length == 0 ? "/" : "/" + string.Join('/', segments);
    }

    /// <summary>Resolves a normalized store path beneath the configured remote root.</summary>
    public static string ToServerPath(string remoteRoot, string normalizedStorePath)
    {
        PinnedSshSettings.ValidateRemotePath(remoteRoot);
        string root = remoteRoot.TrimEnd('/');
        string combined = normalizedStorePath == "/" ? root : root + normalizedStorePath;
        string result = combined.Length == 0 ? "/" : combined;
        if (result.Length > 4096)
            throw new ProtocolConfigurationException("remote_path_invalid", "The remote path is too long.");
        return result;
    }

    public static string CombineChild(string normalizedStorePath, string childName)
        => normalizedStorePath == "/" ? "/" + childName : normalizedStorePath + "/" + childName;
}
