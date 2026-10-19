using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

public sealed class WebDavStorageConnectionProvider(
    ICredentialVault credentialVault) : IStorageConnectionProvider
{
    public string Id => "webdav";
    public string DisplayName => "WebDAV";
    public StorageProviderCapabilities Capabilities =>
        StorageProviderHelpers.ReadWriteCapabilities
        | StorageProviderCapabilities.DirectFileAccess;
    public IReadOnlySet<StorageAuthorizationMode> AuthorizationModes { get; }
        = new HashSet<StorageAuthorizationMode> { StorageAuthorizationMode.UsernamePassword };

    public Task<IStorageSession> OpenSessionAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WebDavConnectionSettings settings = ParseAndValidate(connection);
        Dictionary<string, string> credentials = credentialVault.UnprotectConnectionCredentials(connection);
        ValidateCredentials(credentials);
        HttpClient client = CreateHttpClient(settings, credentials["username"], credentials["password"]);
        var store = new WebDavRemoteFileStore(client, settings.ServerUrl);
        var session = new WebDavStorageSession(connection.Id, Capabilities, client, store);
        return Task.FromResult<IStorageSession>(session);
    }

    public async Task<StorageConnectionHealthResult> TestAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using IStorageSession session = await OpenSessionAsync(connection, cancellationToken);
            await session.RemoteFiles!.ListAsync("/", cancellationToken);
            return StorageProviderHelpers.Healthy();
        }
        catch (ProtocolConfigurationException exception)
        {
            return StorageProviderHelpers.Invalid(exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return StorageProviderHelpers.Unavailable("webdav_connection_failed");
        }
        catch (IOException)
        {
            return StorageProviderHelpers.Unavailable("webdav_connection_failed");
        }
    }

    public Task RevokeAsync(StorageConnection connection, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    internal static WebDavConnectionSettings ParseAndValidate(StorageConnection connection)
    {
        StorageProviderHelpers.ValidateConnection(connection, "webdav", StorageAuthorizationMode.UsernamePassword);
        var settings = ProtocolConnectionSettings.Parse<WebDavConnectionSettings>(connection.SettingsJson, "WebDAV");
        if (string.IsNullOrWhiteSpace(settings.ServerUrl)
            || settings.ServerUrl.Length > 2048
            || !Uri.TryCreate(settings.ServerUrl, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("https" or "http"))
            throw new ProtocolConfigurationException("server_url_invalid", "The WebDAV server URL is invalid.");
        // Basic authentication over plain HTTP exposes the remote credentials on the
        // network; it needs the administrator's explicit, per-connection consent.
        if (uri.Scheme == "http" && !settings.AllowInsecureHttp)
            throw new ProtocolConfigurationException(
                "server_url_insecure", "Plain HTTP requires the explicit AllowInsecureHttp opt-in.");
        return settings;
    }

    internal static void ValidateCredentials(Dictionary<string, string> credentials)
    {
        if (!credentials.TryGetValue("username", out string? username)
            || string.IsNullOrWhiteSpace(username)
            || username.Length > 256
            || username.IndexOfAny(['\r', '\n']) >= 0
            || !credentials.TryGetValue("password", out string? password)
            || password.Length > 4096
            || password.IndexOfAny(['\r', '\n']) >= 0)
            throw new ProtocolConfigurationException("credentials_invalid", "The WebDAV credentials are invalid.");
    }

    internal static HttpClient CreateHttpClient(
        WebDavConnectionSettings settings,
        string username,
        string password)
    {
        // No whole-request timeout: it would also cap large uploads. The store
        // applies WebDavRemoteFileStore.DefaultIdleTimeout per request instead.
        var client = new HttpClient(CreateHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        byte[] credentialBytes = Encoding.UTF8.GetBytes($"{username}:{password}");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(credentialBytes));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        return client;
    }

    /// <summary>
    /// The server URL is operator input, so the client must not become a proxy
    /// into this host: the address is checked when the socket connects (after DNS,
    /// so a rebinding name cannot slip through), and redirects are not followed
    /// because they would bypass that check. Private LAN addresses stay allowed —
    /// a NAS on the local network is the typical WebDAV target.
    /// </summary>
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        AllowAutoRedirect = false,
        ConnectCallback = ConnectToAllowedAddressAsync
    };

    private static async ValueTask<Stream> ConnectToAllowedAddressAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        IPAddress[] addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out IPAddress? literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        IPAddress[] allowed = addresses.Where(address => !IsBlockedAddress(address)).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException(
                "The WebDAV server resolves only to loopback, link-local or otherwise non-routable addresses.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Loopback reaches this container's own services, link-local includes the
    /// cloud metadata endpoint (169.254.169.254); neither is a WebDAV server.
    /// </summary>
    internal static bool IsBlockedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast)
            return true;
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 0                         // 0.0.0.0/8 "this network"
               || (bytes[0] == 169 && bytes[1] == 254) // link-local / metadata
               || bytes[0] >= 224;                    // multicast and reserved
    }
}

internal sealed class WebDavStorageSession(
    Guid connectionId,
    StorageProviderCapabilities capabilities,
    HttpClient client,
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
        return ValueTask.CompletedTask;
    }
}
