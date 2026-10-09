using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.ExternalStorage;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

/// <summary>A restic failure with a stable, non-secret code shown in the UI and stored on runs.</summary>
public sealed class ResticException(string code, string message, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>Non-secret backend settings of a repository (stored as <see cref="BackupRepository.SettingsJson"/>).</summary>
public sealed class ResticRepositorySettings
{
    /// <summary>Local: absolute directory. SFTP: path below the connection's remote root. REST/S3: optional sub path / prefix.</summary>
    public string? Path { get; set; }

    /// <summary>REST server or S3 endpoint URL (http/https).</summary>
    public string? Endpoint { get; set; }
    public string? Bucket { get; set; }
    public string? Region { get; set; }

    /// <summary>S3 path-style bucket lookup (MinIO and most self-hosted S3 servers).</summary>
    public bool S3PathStyle { get; set; }

    /// <summary>SFTP: the existing pinned-SSH storage connection (sftp or rsync-ssh) to reuse.</summary>
    public Guid? StorageConnectionId { get; set; }

    /// <summary>Optional PEM CA certificate for a REST/S3 endpoint with a private CA.</summary>
    public string? CaCertificatePem { get; set; }

    public static ResticRepositorySettings Parse(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new ResticRepositorySettings()
            : JsonSerializer.Deserialize<ResticRepositorySettings>(json) ?? new ResticRepositorySettings();

    public string Serialize() => JsonSerializer.Serialize(this);
}

/// <summary>Backend credentials; only ever stored vault-protected (<see cref="BackupRepository.EncryptedSecrets"/>).</summary>
public sealed class ResticBackendSecrets
{
    public string? RestUsername { get; set; }
    public string? RestPassword { get; set; }
    public string? S3AccessKeyId { get; set; }
    public string? S3SecretAccessKey { get; set; }
}

/// <summary>
/// A resolved restic target: repository URL, environment (password and backend secrets —
/// never passed as arguments) and backend-specific global arguments. Disposing deletes the
/// temporary files (SSH key, CA certificate) the resolution wrote.
/// </summary>
public sealed class ResticTarget(
    string repositoryUrl,
    IReadOnlyDictionary<string, string> environment,
    IReadOnlyList<string> globalArguments,
    string? temporaryDirectory) : IDisposable
{
    public string RepositoryUrl { get; } = repositoryUrl;
    public IReadOnlyDictionary<string, string> Environment { get; } = environment;
    public IReadOnlyList<string> GlobalArguments { get; } = globalArguments;

    public void Dispose()
    {
        if (temporaryDirectory is null)
            return;
        try { Directory.Delete(temporaryDirectory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* wiped on next startup */ }
    }
}

/// <summary>
/// Pure mapping of a repository definition onto restic's URL / environment / option model,
/// plus the validation that keeps operator input from escaping its intended target.
/// </summary>
public static partial class ResticBackend
{
    public const string SecretsProviderId = "restic";
    public const string SecretsKind = "backend-secrets";
    public const string PasswordKind = "repo-password";

    public static CredentialContext SecretsContext(Guid repositoryId) => new(repositoryId, SecretsProviderId, SecretsKind, 1);
    public static CredentialContext PasswordContext(Guid repositoryId) => new(repositoryId, SecretsProviderId, PasswordKind, 1);

    /// <summary>The SSH-capable connection providers SFTP repositories may reuse.</summary>
    public static readonly IReadOnlySet<string> SshProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sftp", "rsync-ssh" };

    /// <summary>Validates settings and secrets of a repository; throws <see cref="ResticException"/>.</summary>
    public static void Validate(
        BackupBackend backend,
        ResticRepositorySettings settings,
        ResticBackendSecrets secrets,
        IReadOnlyList<string> localRoots)
    {
        if (!string.IsNullOrWhiteSpace(settings.CaCertificatePem))
        {
            if (backend is not (BackupBackend.Rest or BackupBackend.S3)
                || settings.CaCertificatePem.Length > 64 * 1024
                || !settings.CaCertificatePem.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
                throw new ResticException("ca_certificate_invalid", "The CA certificate must be a PEM certificate.");
        }

        switch (backend)
        {
            case BackupBackend.Local:
                ValidateLocalPath(settings.Path, localRoots);
                break;
            case BackupBackend.Sftp:
                if (settings.StorageConnectionId is null || settings.StorageConnectionId == Guid.Empty)
                    throw new ResticException("connection_required", "An SSH connection is required.");
                ValidateRelativePath(settings.Path, required: true);
                break;
            case BackupBackend.Rest:
                ValidateEndpoint(settings.Endpoint);
                ValidateRelativePath(settings.Path, required: false);
                if (string.IsNullOrEmpty(secrets.RestUsername) != string.IsNullOrEmpty(secrets.RestPassword))
                    throw new ResticException("credentials_incomplete", "Enter both user name and password, or neither.");
                break;
            case BackupBackend.S3:
                ValidateEndpoint(settings.Endpoint);
                if (string.IsNullOrWhiteSpace(settings.Bucket) || !BucketPattern().IsMatch(settings.Bucket))
                    throw new ResticException("bucket_invalid", "The bucket name is invalid.");
                ValidateRelativePath(settings.Path, required: false);
                if (!string.IsNullOrWhiteSpace(settings.Region) && !RegionPattern().IsMatch(settings.Region))
                    throw new ResticException("region_invalid", "The region is invalid.");
                if (string.IsNullOrWhiteSpace(secrets.S3AccessKeyId) || string.IsNullOrWhiteSpace(secrets.S3SecretAccessKey))
                    throw new ResticException("credentials_required", "Access key and secret key are required.");
                break;
            default:
                throw new ResticException("backend_unsupported", "This backend is not supported.");
        }
    }

    /// <summary>Repository URL for every backend except SFTP (which needs the SSH connection).</summary>
    public static string BuildUrl(BackupBackend backend, ResticRepositorySettings settings, RsyncSshConnectionSettings? ssh = null)
        => backend switch
        {
            BackupBackend.Local => Path.TrimEndingDirectorySeparator(settings.Path!),
            BackupBackend.Sftp => ssh is null
                ? throw new ResticException("connection_required", "An SSH connection is required.")
                : $"sftp:{ssh.Username}@{ssh.Host}:{CombineRemote(ssh.RemoteRoot, settings.Path!)}",
            BackupBackend.Rest => "rest:" + settings.Endpoint!.TrimEnd('/') + Suffix(settings.Path),
            BackupBackend.S3 => "s3:" + settings.Endpoint!.TrimEnd('/') + "/" + settings.Bucket + Suffix(settings.Path),
            _ => throw new ResticException("backend_unsupported", "This backend is not supported."),
        };

    /// <summary>Environment variables carrying the password and backend secrets.</summary>
    public static Dictionary<string, string> BuildEnvironment(
        BackupBackend backend, ResticRepositorySettings settings, ResticBackendSecrets secrets, string password, string repositoryUrl)
    {
        var env = new Dictionary<string, string>
        {
            ["RESTIC_REPOSITORY"] = repositoryUrl,
            ["RESTIC_PASSWORD"] = password,
        };
        if (backend == BackupBackend.Rest && !string.IsNullOrEmpty(secrets.RestUsername))
        {
            env["RESTIC_REST_USERNAME"] = secrets.RestUsername;
            env["RESTIC_REST_PASSWORD"] = secrets.RestPassword ?? string.Empty;
        }
        if (backend == BackupBackend.S3)
        {
            env["AWS_ACCESS_KEY_ID"] = secrets.S3AccessKeyId ?? string.Empty;
            env["AWS_SECRET_ACCESS_KEY"] = secrets.S3SecretAccessKey ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(settings.Region))
                env["AWS_DEFAULT_REGION"] = settings.Region;
        }
        return env;
    }

    /// <summary>
    /// The <c>sftp.command</c> restic runs: the same hardened ssh invocation the rsync
    /// provider uses (pinned known_hosts, batch mode, key only) plus the sftp subsystem.
    /// </summary>
    public static string BuildSftpCommand(RsyncSshConnectionSettings ssh)
        => RsyncProcessRunner.BuildRemoteShell(ssh) + $" {ssh.Username}@{ssh.Host} -s sftp";

    /// <summary>32 random bytes, URL-safe base64 — the generated repository password.</summary>
    public static string GeneratePassword()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// The code typed to confirm the recovery kit: the first six characters of the base32
    /// SHA-256 of the password. Proves the kit (not just the page) was saved.
    /// </summary>
    public static string KitCode(string password)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password));
        // 6 base32 characters need 30 bits.
        ulong bits = ((ulong)hash[0] << 24) | ((ulong)hash[1] << 16) | ((ulong)hash[2] << 8) | hash[3];
        var chars = new char[6];
        for (var i = 0; i < 6; i++)
            chars[i] = alphabet[(int)((bits >> (32 - 5 * (i + 1))) & 31)];
        return new string(chars);
    }

    public static bool KitCodeMatches(string password, string? typed)
    {
        var normalized = (typed ?? string.Empty).Trim().Replace("-", "").Replace(" ", "").ToUpperInvariant();
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(KitCode(password)),
            System.Text.Encoding.ASCII.GetBytes(normalized));
    }

    internal static void ValidateLocalPath(string? path, IReadOnlyList<string> localRoots)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.IndexOfAny(['\r', '\n', '\0']) >= 0
            || !System.IO.Path.IsPathFullyQualified(path))
            throw new ResticException("path_invalid", "The repository path must be an absolute path.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!string.Equals(full, Path.TrimEndingDirectorySeparator(path), StringComparison.Ordinal))
            throw new ResticException("path_invalid", "The repository path must be normalized (no '.' or '..').");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var allowed = localRoots.Any(root =>
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return full.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
        });
        if (!allowed)
            throw new ResticException("path_outside_roots",
                "Local repositories must lie below an allowed root: " + string.Join(", ", localRoots));
    }

    private static void ValidateRelativePath(string? path, bool required)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (required)
                throw new ResticException("path_invalid", "A repository path is required.");
            return;
        }
        if (path.Length > 1024
            || !RelativePathPattern().IsMatch(path)
            || path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(s => s is "." or ".."))
            throw new ResticException("path_invalid", "The repository path may only contain letters, digits, '.', '_', '-' and '/'.");
    }

    private static void ValidateEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || endpoint.IndexOfAny([' ', '\r', '\n']) >= 0)
            throw new ResticException("endpoint_invalid",
                "The endpoint must be an http(s) URL without credentials, query or fragment.");
    }

    private static string Suffix(string? path)
        => string.IsNullOrWhiteSpace(path) ? string.Empty : "/" + path.Trim('/');

    private static string CombineRemote(string root, string child)
    {
        var combined = root.TrimEnd('/') + "/" + child.Trim('/');
        PinnedSshSettings.ValidateRemotePath(combined);
        return combined;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex BucketPattern();

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex RegionPattern();

    [GeneratedRegex("^/?[A-Za-z0-9._/-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RelativePathPattern();
}
