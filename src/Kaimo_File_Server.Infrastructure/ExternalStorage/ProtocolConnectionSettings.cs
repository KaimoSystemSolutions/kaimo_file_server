using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

public sealed record SmbMountConnectionSettings(
    string Server,
    string Share,
    string ExpectedServerIdentity,
    string MountPath,
    string AttestationPath,
    string MinimumDialect = "3.0",
    bool RequireSigning = true,
    bool RequireEncryption = true,
    bool ReadOnly = false);

public sealed record NfsMountConnectionSettings(
    string Server,
    string Export,
    string ExpectedServerIdentity,
    string MountPath,
    string AttestationPath,
    int MinimumMajorVersion = 4,
    bool RequireKerberos = false,
    bool ReadOnly = false);

public sealed record RsyncSshConnectionSettings(
    string Host,
    int Port,
    string Username,
    string RemoteRoot,
    string ExpectedHostKeySha256,
    string PrivateKeySecretReference,
    string KnownHostsSecretReference);

public sealed record ProtocolMountAttestation(
    string ProviderId,
    string MountPath,
    string Endpoint,
    string ServerIdentity,
    string[] Assurances,
    DateTime ValidUntilUtc);

internal static class ProtocolConnectionSettings
{
    private const int MaximumJsonCharacters = 64 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static T Parse<T>(string? json, string description)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumJsonCharacters)
            throw new ProtocolConfigurationException("settings_invalid", $"{description} settings are missing or too large.");
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options)
                   ?? throw new ProtocolConfigurationException("settings_invalid", $"{description} settings are invalid.");
        }
        catch (JsonException exception)
        {
            throw new ProtocolConfigurationException("settings_invalid", $"{description} settings are invalid.", exception);
        }
    }
}

public sealed class ProtocolConfigurationException(
    string code,
    string message,
    Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    public string Code { get; } = code;
}
