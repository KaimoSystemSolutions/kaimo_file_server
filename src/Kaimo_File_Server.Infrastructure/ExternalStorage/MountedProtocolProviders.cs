using System.Net;
using System.Text.RegularExpressions;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

public sealed partial class SmbStorageConnectionProvider : IStorageConnectionProvider
{
    private readonly ProtocolMountVerifier _mountVerifier = new();

    public string Id => "smb";
    public string DisplayName => "SMB";
    public StorageProviderCapabilities Capabilities => ReadWriteMountedCapabilities
        | StorageProviderCapabilities.RequiresHostMount;
    public IReadOnlySet<StorageAuthorizationMode> AuthorizationModes { get; }
        = new HashSet<StorageAuthorizationMode> { StorageAuthorizationMode.HostMount };

    public Task<IStorageSession> OpenSessionAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConnection(connection);
        var settings = ProtocolConnectionSettings.Parse<SmbMountConnectionSettings>(
            connection.SettingsJson, "SMB");
        ValidateSettings(settings);
        string endpoint = $"//{settings.Server}/{settings.Share}";
        _mountVerifier.Verify(
            Id,
            settings.MountPath,
            settings.AttestationPath,
            endpoint,
            settings.ExpectedServerIdentity,
            ["operator-managed", "smb3", "signing", "encryption"]);
        StorageProviderCapabilities capabilities = settings.ReadOnly
            ? ReadOnlyMountedCapabilities | StorageProviderCapabilities.RequiresHostMount
            : Capabilities;
        IStorageSession session = new MountedStorageSession(
            connection.Id,
            capabilities,
            new MountedRemoteFileStore(settings.MountPath, settings.ReadOnly));
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
            return Healthy();
        }
        catch (ProtocolConfigurationException exception)
        {
            return Invalid(exception);
        }
        catch (IOException)
        {
            return Unavailable("mount_io_failed");
        }
        catch (UnauthorizedAccessException)
        {
            return Unavailable("mount_access_denied");
        }
    }

    public Task RevokeAsync(StorageConnection connection, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    private static void ValidateSettings(SmbMountConnectionSettings settings)
    {
        ValidateHost(settings.Server);
        if (string.IsNullOrWhiteSpace(settings.Share)
            || settings.Share.Length > 80
            || settings.Share.EndsWithAny('.', ' ')
            || !SmbShareName().IsMatch(settings.Share))
            throw new ProtocolConfigurationException("share_invalid", "The SMB share name is invalid.");
        ValidateIdentity(settings.ExpectedServerIdentity);
        if (settings.MinimumDialect is not ("3.0" or "3.02" or "3.1.1"))
            throw new ProtocolConfigurationException("dialect_invalid", "SMB 3.0 or newer is required.");
        if (!settings.RequireSigning || !settings.RequireEncryption)
            throw new ProtocolConfigurationException(
                "transport_policy_unsafe",
                "SMB signing and encryption must be required.");
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._$ -]{0,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex SmbShareName();

    internal static void ValidateHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)
            || host.Length > 253
            || host.ContainsAny('/', '\\', ':', '@', '\r', '\n')
            || (!Uri.CheckHostName(host).Equals(UriHostNameType.Dns)
                && !IPAddress.TryParse(host, out _)))
            throw new ProtocolConfigurationException("host_invalid", "The protocol host is invalid.");
    }

    internal static void ValidateIdentity(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity)
            || identity.Length > 512
            || identity.ContainsAny('\r', '\n'))
            throw new ProtocolConfigurationException("identity_invalid", "The expected server identity is invalid.");
    }

    internal static void ValidateConnection(StorageConnection connection, string expectedProvider = "smb")
    {
        if (!string.Equals(connection.ProviderId, expectedProvider, StringComparison.OrdinalIgnoreCase)
            || connection.AuthorizationMode != StorageAuthorizationMode.HostMount)
            throw new ProtocolConfigurationException("connection_mode_invalid", "The connection mode is invalid for this provider.");
    }

    internal static StorageConnectionHealthResult Healthy()
        => new(StorageConnectionHealthState.Healthy, "healthy", DateTime.UtcNow);
    internal static StorageConnectionHealthResult Invalid(ProtocolConfigurationException exception)
        => new(exception.Code is "identity_mismatch" or "host_key_mismatch" or "host_key_missing"
                ? StorageConnectionHealthState.IdentityMismatch
                : StorageConnectionHealthState.InvalidConfiguration,
            exception.Code,
            DateTime.UtcNow);
    internal static StorageConnectionHealthResult Unavailable(string code)
        => new(StorageConnectionHealthState.Unavailable, code, DateTime.UtcNow);

    internal const StorageProviderCapabilities ReadOnlyMountedCapabilities =
        StorageProviderCapabilities.Browse
        | StorageProviderCapabilities.Read
        | StorageProviderCapabilities.Sync;

    internal const StorageProviderCapabilities ReadWriteMountedCapabilities =
        ReadOnlyMountedCapabilities
        | StorageProviderCapabilities.Write
        | StorageProviderCapabilities.CreateDirectory
        | StorageProviderCapabilities.Delete
        | StorageProviderCapabilities.Rename
        | StorageProviderCapabilities.Move;
}

public sealed class NfsStorageConnectionProvider : IStorageConnectionProvider
{
    private readonly ProtocolMountVerifier _mountVerifier = new();

    public string Id => "nfs";
    public string DisplayName => "NFS";
    public StorageProviderCapabilities Capabilities => SmbStorageConnectionProvider.ReadWriteMountedCapabilities
        | StorageProviderCapabilities.RequiresHostMount;
    public IReadOnlySet<StorageAuthorizationMode> AuthorizationModes { get; }
        = new HashSet<StorageAuthorizationMode> { StorageAuthorizationMode.HostMount };

    public Task<IStorageSession> OpenSessionAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SmbStorageConnectionProvider.ValidateConnection(connection, Id);
        var settings = ProtocolConnectionSettings.Parse<NfsMountConnectionSettings>(
            connection.SettingsJson, "NFS");
        SmbStorageConnectionProvider.ValidateHost(settings.Server);
        SmbStorageConnectionProvider.ValidateIdentity(settings.ExpectedServerIdentity);
        if (string.IsNullOrWhiteSpace(settings.Export)
            || !settings.Export.StartsWith('/')
            || settings.Export.ContainsAny('\r', '\n', ':'))
            throw new ProtocolConfigurationException("export_invalid", "The NFS export is invalid.");
        if (settings.MinimumMajorVersion < 4)
            throw new ProtocolConfigurationException("nfs_version_unsafe", "NFSv4 or newer is required.");
        var required = new List<string> { "operator-managed", "host-allowlist", "nfs4" };
        if (settings.RequireKerberos)
            required.Add("kerberos");
        _mountVerifier.Verify(
            Id,
            settings.MountPath,
            settings.AttestationPath,
            $"{settings.Server}:{settings.Export}",
            settings.ExpectedServerIdentity,
            required);
        StorageProviderCapabilities capabilities = settings.ReadOnly
            ? SmbStorageConnectionProvider.ReadOnlyMountedCapabilities | StorageProviderCapabilities.RequiresHostMount
            : Capabilities;
        IStorageSession session = new MountedStorageSession(
            connection.Id,
            capabilities,
            new MountedRemoteFileStore(settings.MountPath, settings.ReadOnly));
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
        catch (IOException)
        {
            return SmbStorageConnectionProvider.Unavailable("mount_io_failed");
        }
        catch (UnauthorizedAccessException)
        {
            return SmbStorageConnectionProvider.Unavailable("mount_access_denied");
        }
    }

    public Task RevokeAsync(StorageConnection connection, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

internal static class ProtocolStringExtensions
{
    public static bool ContainsAny(this string value, params char[] candidates)
        => value.IndexOfAny(candidates) >= 0;

    public static bool EndsWithAny(this string value, params char[] candidates)
        => value.Length > 0 && candidates.Contains(value[^1]);
}
