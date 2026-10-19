using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

public sealed class SmbStorageConnectionProvider(
    ICredentialVault credentialVault,
    IProtocolCommandRunner commandRunner) : IStorageConnectionProvider
{
    public string Id => "smb";
    public string DisplayName => "SMB";
    public StorageProviderCapabilities Capabilities => ReadWriteCapabilities
        | StorageProviderCapabilities.DirectFileAccess;
    public IReadOnlySet<StorageAuthorizationMode> AuthorizationModes { get; }
        = new HashSet<StorageAuthorizationMode> { StorageAuthorizationMode.UsernamePassword };

    public Task<IStorageSession> OpenSessionAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SmbConnectionSettings settings = ParseAndValidate(connection);
        Dictionary<string, string> credentials = credentialVault.UnprotectConnectionCredentials(connection);
        if (!credentials.TryGetValue("username", out string? username)
            || string.IsNullOrWhiteSpace(username)
            || username.Length > 256
            || username.IndexOfAny(['\r', '\n']) >= 0
            || !credentials.TryGetValue("password", out string? password)
            || password.Length > 4096
            || password.IndexOfAny(['\r', '\n']) >= 0)
            throw new ProtocolConfigurationException("credentials_invalid", "The SMB credentials are invalid.");
        credentials.TryGetValue("domain", out string? credentialDomain);
        if (credentialDomain is { Length: > 256 } || credentialDomain?.IndexOfAny(['\r', '\n']) >= 0)
            throw new ProtocolConfigurationException("credentials_invalid", "The SMB credentials are invalid.");
        IRemoteFileStore files = new SmbCommandRemoteFileStore(
            settings,
            new SmbCredentials(username, password, settings.Domain ?? credentialDomain),
            commandRunner);
        return Task.FromResult<IStorageSession>(new ProtocolStorageSession(connection.Id, Capabilities, files));
    }

    public async Task<StorageConnectionHealthResult> TestAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using IStorageSession session = await OpenSessionAsync(connection, cancellationToken);
            await session.RemoteFiles!.ListAsync("/", cancellationToken);
            return Healthy();
        }
        catch (ProtocolConfigurationException exception) { return Invalid(exception); }
        catch (IOException) { return Unavailable("smb_connection_failed"); }
        catch (UnauthorizedAccessException) { return Unavailable("smb_access_denied"); }
    }

    public Task RevokeAsync(StorageConnection connection, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    internal static SmbConnectionSettings ParseAndValidate(StorageConnection connection)
    {
        ValidateConnection(connection, "smb", StorageAuthorizationMode.UsernamePassword);
        var settings = ProtocolConnectionSettings.Parse<SmbConnectionSettings>(connection.SettingsJson, "SMB");
        ValidateHost(settings.Server);
        ValidatePort(settings.Port);
        if (settings.Domain is { Length: > 256 } || settings.Domain?.IndexOfAny(['\r', '\n']) >= 0)
            throw new ProtocolConfigurationException("domain_invalid", "The SMB domain is invalid.");
        if (settings.MinimumDialect is not ("3.0" or "3.02" or "3.1.1"))
            throw new ProtocolConfigurationException("dialect_invalid", "SMB 3.0 or newer is required.");
        if (!settings.RequireSigning || !settings.RequireEncryption)
            throw new ProtocolConfigurationException(
                "transport_policy_unsafe", "SMB signing and encryption must be required.");
        return settings;
    }

    internal static void ValidateHost(string host) => StorageProviderHelpers.ValidateHost(host);
    internal static void ValidatePort(int port) => StorageProviderHelpers.ValidatePort(port);
    internal static void ValidateConnection(
        StorageConnection connection, string expectedProvider, StorageAuthorizationMode expectedMode)
        => StorageProviderHelpers.ValidateConnection(connection, expectedProvider, expectedMode);
    internal static StorageConnectionHealthResult Healthy() => StorageProviderHelpers.Healthy();
    internal static StorageConnectionHealthResult Invalid(ProtocolConfigurationException exception)
        => StorageProviderHelpers.Invalid(exception);
    internal static StorageConnectionHealthResult Unavailable(string code)
        => StorageProviderHelpers.Unavailable(code);
    internal const StorageProviderCapabilities ReadOnlyCapabilities = StorageProviderHelpers.ReadOnlyCapabilities;
    internal const StorageProviderCapabilities ReadWriteCapabilities = StorageProviderHelpers.ReadWriteCapabilities;
}
