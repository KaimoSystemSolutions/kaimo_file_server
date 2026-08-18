using System.Text.Json;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

internal sealed class ProtocolMountVerifier
{
    private const int MaximumAttestationBytes = 64 * 1024;
    private static readonly TimeSpan MaximumAttestationLifetime = TimeSpan.FromMinutes(15);

    public ProtocolMountAttestation Verify(
        string providerId,
        string mountPath,
        string attestationPath,
        string endpoint,
        string expectedIdentity,
        IEnumerable<string> requiredAssurances)
    {
        string root = RequireAbsolutePath(mountPath, "mount_path_invalid");
        string attestationFile = RequireAbsolutePath(attestationPath, "attestation_path_invalid");
        if (IsContained(attestationFile, root))
            throw new ProtocolConfigurationException(
                "attestation_location_invalid",
                "The mount attestation must be stored outside the remote mount.");
        if (!Directory.Exists(root))
            throw new ProtocolConfigurationException("mount_unavailable", "The operator-managed mount is unavailable.");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new ProtocolConfigurationException("mount_symlink_rejected", "The mount root cannot be a symbolic link.");
        if (!File.Exists(attestationFile))
            throw new ProtocolConfigurationException("attestation_missing", "The operator mount attestation is missing.");

        var file = new FileInfo(attestationFile);
        if (file.Length is <= 0 or > MaximumAttestationBytes)
            throw new ProtocolConfigurationException("attestation_invalid", "The operator mount attestation is invalid.");
        EnsureReadOnlyAttestation(attestationFile);

        ProtocolMountAttestation attestation;
        try
        {
            attestation = JsonSerializer.Deserialize<ProtocolMountAttestation>(File.ReadAllText(attestationFile))
                          ?? throw new JsonException();
        }
        catch (JsonException exception)
        {
            throw new ProtocolConfigurationException("attestation_invalid", "The operator mount attestation is invalid.", exception);
        }

        if (string.IsNullOrWhiteSpace(attestation.ProviderId)
            || string.IsNullOrWhiteSpace(attestation.MountPath)
            || string.IsNullOrWhiteSpace(attestation.Endpoint)
            || string.IsNullOrWhiteSpace(attestation.ServerIdentity)
            || attestation.Assurances is null)
            throw new ProtocolConfigurationException("attestation_invalid", "The operator mount attestation is invalid.");
        DateTime nowUtc = DateTime.UtcNow;
        if (!string.Equals(attestation.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
            || !PathsEqual(attestation.MountPath, root)
            || !string.Equals(attestation.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(attestation.ServerIdentity, expectedIdentity, StringComparison.Ordinal)
            || attestation.ValidUntilUtc.Kind != DateTimeKind.Utc
            || attestation.ValidUntilUtc <= nowUtc
            || attestation.ValidUntilUtc > nowUtc + MaximumAttestationLifetime)
            throw new ProtocolConfigurationException("identity_mismatch", "The mount identity attestation does not match the connection.");

        var assurances = attestation.Assurances.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requiredAssurances.Any(required => !assurances.Contains(required)))
            throw new ProtocolConfigurationException("transport_policy_unsatisfied", "The mounted transport does not satisfy the required security policy.");
        return attestation;
    }

    private static string RequireAbsolutePath(string path, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ProtocolConfigurationException(errorCode, "An absolute operator-managed path is required.");
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ProtocolConfigurationException(errorCode, "An absolute operator-managed path is required.", exception);
        }
    }

    private static bool IsContained(string candidate, string root)
        => candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                PathComparison);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _ = exception;
            return false;
        }
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void EnsureReadOnlyAttestation(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        UnixFileMode mode = File.GetUnixFileMode(path);
        if ((mode & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw new ProtocolConfigurationException(
                "attestation_permissions_unsafe",
                "The mount attestation must be read-only for the application process.");
    }
}
