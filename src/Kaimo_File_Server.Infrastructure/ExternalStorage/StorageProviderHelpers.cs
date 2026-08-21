using System.Net;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

internal static class StorageProviderHelpers
{
    internal const StorageProviderCapabilities ReadOnlyCapabilities =
        StorageProviderCapabilities.Browse | StorageProviderCapabilities.Read;

    internal const StorageProviderCapabilities ReadWriteCapabilities =
        ReadOnlyCapabilities
        | StorageProviderCapabilities.Write
        | StorageProviderCapabilities.CreateDirectory
        | StorageProviderCapabilities.Delete
        | StorageProviderCapabilities.Rename
        | StorageProviderCapabilities.Move
        | StorageProviderCapabilities.Sync;

    internal static void ValidateConnection(
        StorageConnection connection,
        string expectedProvider,
        StorageAuthorizationMode expectedMode)
    {
        if (!string.Equals(connection.ProviderId, expectedProvider, StringComparison.OrdinalIgnoreCase)
            || connection.AuthorizationMode != expectedMode)
            throw new ProtocolConfigurationException(
                "connection_mode_invalid", "The connection mode is invalid for this provider.");
    }

    internal static void ValidateHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)
            || host.Length > 253
            || host.ContainsAny('/', '\\', ':', '@', '\r', '\n')
            || (!Uri.CheckHostName(host).Equals(UriHostNameType.Dns) && !IPAddress.TryParse(host, out _)))
            throw new ProtocolConfigurationException("host_invalid", "The protocol host is invalid.");
    }

    internal static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535)
            throw new ProtocolConfigurationException("port_invalid", "The protocol port is invalid.");
    }

    internal static StorageConnectionHealthResult Healthy()
        => new(StorageConnectionHealthState.Healthy, "healthy", DateTime.UtcNow);

    internal static StorageConnectionHealthResult Invalid(ProtocolConfigurationException exception)
        => new(exception.Code is "identity_mismatch" or "host_key_mismatch" or "host_key_missing"
                ? StorageConnectionHealthState.IdentityMismatch
                : StorageConnectionHealthState.InvalidConfiguration,
            exception.Code, DateTime.UtcNow);

    internal static StorageConnectionHealthResult Unavailable(string code)
        => new(StorageConnectionHealthState.Unavailable, code, DateTime.UtcNow);
}
