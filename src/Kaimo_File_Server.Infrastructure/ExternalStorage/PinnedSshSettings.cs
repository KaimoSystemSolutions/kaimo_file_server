using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

/// <summary>
/// Shared validation and secret handling for pinned-SSH transports. Both the
/// rsync-over-SSH sync provider and the SFTP browse provider use the identical
/// <see cref="RsyncSshConnectionSettings"/> shape and the same host-key-pinning
/// trust model, so the security-critical validation lives here exactly once.
/// </summary>
internal static partial class PinnedSshSettings
{
    /// <summary>
    /// Parses and validates a pinned-SSH connection. <paramref name="requireKnownHosts"/>
    /// is set for transports that delegate host-key checking to an external
    /// OpenSSH known-hosts file (rsync); transports that pin the SHA-256
    /// fingerprint in process (SFTP) do not require the file to be present.
    /// </summary>
    public static RsyncSshConnectionSettings ParseAndValidate(
        StorageConnection connection, string expectedProviderId, bool requireKnownHosts)
    {
        if (!string.Equals(connection.ProviderId, expectedProviderId, StringComparison.OrdinalIgnoreCase)
            || connection.AuthorizationMode != StorageAuthorizationMode.SshKey)
            throw new ProtocolConfigurationException(
                "connection_mode_invalid", "The connection mode is invalid for pinned SSH.");
        var settings = ProtocolConnectionSettings.Parse<RsyncSshConnectionSettings>(
            connection.SettingsJson, "pinned SSH");
        SmbStorageConnectionProvider.ValidateHost(settings.Host);
        if (settings.Port is < 1 or > 65535)
            throw new ProtocolConfigurationException("port_invalid", "The SSH port is invalid.");
        if (string.IsNullOrWhiteSpace(settings.Username) || !UsernamePattern().IsMatch(settings.Username))
            throw new ProtocolConfigurationException("username_invalid", "The SSH username is invalid.");
        ValidateRemotePath(settings.RemoteRoot);
        ValidateFingerprint(settings.ExpectedHostKeySha256);
        if (!string.IsNullOrWhiteSpace(settings.PrivateKeySecretReference))
            ValidateSecretFile(settings.PrivateKeySecretReference, privateKey: true);
        if (requireKnownHosts)
        {
            ValidateSecretFile(settings.KnownHostsSecretReference, privateKey: false);
            ValidatePinnedHostKeyAgainstKnownHosts(settings);
        }
        else if (!string.IsNullOrWhiteSpace(settings.KnownHostsSecretReference))
        {
            ValidateSecretFile(settings.KnownHostsSecretReference, privateKey: false);
        }
        return settings;
    }

    /// <summary>
    /// Computes the canonical <c>SHA256:base64</c> host-key fingerprint used
    /// throughout setup and pinning, matching the OpenSSH representation without
    /// base64 padding.
    /// </summary>
    public static string ComputeFingerprint(ReadOnlySpan<byte> hostKey)
        => "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');

    /// <summary>
    /// Reads and decodes the vault-protected private key, if present. Returns
    /// false when no protected key is stored (for example when an external
    /// secret-file reference is configured instead).
    /// </summary>
    public static bool TryReadVaultPrivateKey(
        StorageConnection connection, ICredentialVault? credentialVault, out byte[] privateKey)
    {
        privateKey = [];
        if (credentialVault is null || string.IsNullOrWhiteSpace(connection.EncryptedCredentialPayload))
            return false;
        Dictionary<string, string> credentials = credentialVault.UnprotectConnectionCredentials(connection);
        if (!credentials.TryGetValue("privateKeyBase64", out string? encoded)
            || encoded.Length > 2 * 1024 * 1024)
            return false;
        byte[] decoded;
        try { decoded = Convert.FromBase64String(encoded); }
        catch (FormatException exception)
        {
            throw new ProtocolConfigurationException(
                "private_key_invalid", "The protected SSH private key is invalid.", exception);
        }
        if (decoded.Length is <= 0 or > 1024 * 1024)
        {
            CryptographicOperations.ZeroMemory(decoded);
            throw new ProtocolConfigurationException(
                "private_key_invalid", "The protected SSH private key is invalid.");
        }
        privateKey = decoded;
        return true;
    }

    public static void ValidateFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)
            || !fingerprint.StartsWith("SHA256:", StringComparison.Ordinal)
            || fingerprint.Length is < 20 or > 100)
            throw new ProtocolConfigurationException(
                "host_key_invalid", "A SHA-256 SSH host-key fingerprint is required.");
    }

    internal static void ValidateRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !path.StartsWith('/')
            || path.Length > 4096
            || !RemotePathPattern().IsMatch(path)
            || path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
            throw new ProtocolConfigurationException("remote_path_invalid", "The SSH remote path is invalid.");
    }

    internal static void ValidateSecretFile(string path, bool privateKey)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)
            || path.IndexOfAny(['\r', '\n']) >= 0
            || !File.Exists(path))
            throw new ProtocolConfigurationException("secret_reference_invalid", "The SSH secret reference is invalid.");
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            UnixFileMode forbidden = privateKey
                ? UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                  | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute
                : UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
            if ((mode & forbidden) != 0)
                throw new ProtocolConfigurationException("secret_permissions_unsafe", "The SSH secret file permissions are unsafe.");
        }
        if (new FileInfo(path).Length is <= 0 or > 1024 * 1024)
            throw new ProtocolConfigurationException("secret_reference_invalid", "The SSH secret file is empty or too large.");
    }

    private static void ValidatePinnedHostKeyAgainstKnownHosts(RsyncSshConnectionSettings settings)
    {
        string expectedHost = settings.Port == 22 ? settings.Host : $"[{settings.Host}]:{settings.Port}";
        bool matchedHost = false;
        foreach (string line in File.ReadLines(settings.KnownHostsSecretReference))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;
            string[] fields = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || fields[0].StartsWith('|'))
                continue;
            if (!fields[0].Split(',').Contains(expectedHost, StringComparer.OrdinalIgnoreCase))
                continue;
            matchedHost = true;
            try
            {
                byte[] key = Convert.FromBase64String(fields[2]);
                string fingerprint = ComputeFingerprint(key);
                if (CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(fingerprint),
                        Encoding.ASCII.GetBytes(settings.ExpectedHostKeySha256)))
                    return;
            }
            catch (FormatException)
            {
                throw new ProtocolConfigurationException("known_hosts_invalid", "The SSH known-hosts file is invalid.");
            }
        }
        throw new ProtocolConfigurationException(
            matchedHost ? "host_key_mismatch" : "host_key_missing",
            "The pinned SSH host identity does not match the known-hosts secret.");
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();

    [GeneratedRegex("^/[A-Za-z0-9._/-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex RemotePathPattern();
}
