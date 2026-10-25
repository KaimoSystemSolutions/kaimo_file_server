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
    /// <summary>
    /// Local: absolute directory. SFTP: path below the connection's remote root. rclone: path
    /// on the remote (incl. bucket). Others: optional sub path / prefix.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>REST server or S3 endpoint URL (http/https).</summary>
    public string? Endpoint { get; set; }

    /// <summary>S3 / B2 / GCS bucket, Azure / Swift container.</summary>
    public string? Bucket { get; set; }
    public string? Region { get; set; }

    /// <summary>Azure storage account name.</summary>
    public string? AzureAccountName { get; set; }

    /// <summary>Azure endpoint suffix for national clouds (default <c>core.windows.net</c>).</summary>
    public string? AzureEndpointSuffix { get; set; }

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

    /// <summary>B2 application key id and key.</summary>
    public string? B2AccountId { get; set; }
    public string? B2AccountKey { get; set; }

    /// <summary>Azure: exactly one of account key or SAS token.</summary>
    public string? AzureAccountKey { get; set; }
    public string? AzureSasToken { get; set; }

    /// <summary>GCS service account key (JSON).</summary>
    public string? GcsCredentialsJson { get; set; }

    /// <summary>Swift: <c>OS_*</c> / <c>ST_*</c> variables, one <c>NAME=value</c> per line (openrc style).</summary>
    public string? SwiftEnvironment { get; set; }

    /// <summary>rclone: one remote section of an rclone.conf.</summary>
    public string? RcloneConfig { get; set; }
}

/// <summary>
/// A resolved restic target: repository URL, environment (password and backend secrets —
/// never passed as arguments) and backend-specific global arguments. Disposing deletes the
/// temporary files (SSH key, CA certificate, …) the resolution wrote, after running
/// <paramref name="beforeCleanup"/> (e.g. storing a config file the backend rewrote).
/// </summary>
public sealed class ResticTarget(
    string repositoryUrl,
    IReadOnlyDictionary<string, string> environment,
    IReadOnlyList<string> globalArguments,
    string? temporaryDirectory,
    Action? beforeCleanup = null) : IDisposable
{
    public string RepositoryUrl { get; } = repositoryUrl;
    public IReadOnlyDictionary<string, string> Environment { get; } = environment;
    public IReadOnlyList<string> GlobalArguments { get; } = globalArguments;

    public void Dispose()
    {
        beforeCleanup?.Invoke();
        beforeCleanup = null;
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
            if (backend is not (BackupBackend.Rest or BackupBackend.S3 or BackupBackend.Swift)
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
            case BackupBackend.B2:
                ValidateContainer(settings.Bucket);
                ValidateRelativePath(settings.Path, required: false);
                if (string.IsNullOrWhiteSpace(secrets.B2AccountId) || string.IsNullOrWhiteSpace(secrets.B2AccountKey))
                    throw new ResticException("credentials_required", "Key id and application key are required.");
                break;
            case BackupBackend.Azure:
                ValidateContainer(settings.Bucket);
                ValidateRelativePath(settings.Path, required: false);
                if (string.IsNullOrWhiteSpace(settings.AzureAccountName) || !AzureAccountPattern().IsMatch(settings.AzureAccountName))
                    throw new ResticException("azure_account_invalid", "The storage account name is invalid.");
                if (!string.IsNullOrWhiteSpace(settings.AzureEndpointSuffix) && !HostPattern().IsMatch(settings.AzureEndpointSuffix))
                    throw new ResticException("endpoint_invalid", "The endpoint suffix must be a host name.");
                if (string.IsNullOrWhiteSpace(secrets.AzureAccountKey) == string.IsNullOrWhiteSpace(secrets.AzureSasToken))
                    throw new ResticException("azure_credentials_invalid", "Enter either the account key or a SAS token.");
                break;
            case BackupBackend.Gcs:
                ValidateContainer(settings.Bucket);
                ValidateRelativePath(settings.Path, required: false);
                GcsProjectId(secrets.GcsCredentialsJson);
                break;
            case BackupBackend.Swift:
                ValidateContainer(settings.Bucket);
                ValidateRelativePath(settings.Path, required: false);
                ParseSwiftEnvironment(secrets.SwiftEnvironment);
                break;
            case BackupBackend.Rclone:
                ValidateRelativePath(settings.Path, required: true);
                ParseRcloneConfig(secrets.RcloneConfig);
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
            BackupBackend.B2 => $"b2:{settings.Bucket}:/{Suffix(settings.Path).TrimStart('/')}",
            BackupBackend.Azure => $"azure:{settings.Bucket}:/{Suffix(settings.Path).TrimStart('/')}",
            BackupBackend.Gcs => $"gs:{settings.Bucket}:/{Suffix(settings.Path).TrimStart('/')}",
            BackupBackend.Swift => $"swift:{settings.Bucket}:/{Suffix(settings.Path).TrimStart('/')}",
            // The pasted section is always written as [kaimo], whatever it was called before.
            BackupBackend.Rclone => $"rclone:{RcloneRemoteName}:{settings.Path!.Trim()}",
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
        if (backend == BackupBackend.B2)
        {
            env["B2_ACCOUNT_ID"] = secrets.B2AccountId ?? string.Empty;
            env["B2_ACCOUNT_KEY"] = secrets.B2AccountKey ?? string.Empty;
        }
        if (backend == BackupBackend.Azure)
        {
            env["AZURE_ACCOUNT_NAME"] = settings.AzureAccountName ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(secrets.AzureAccountKey))
                env["AZURE_ACCOUNT_KEY"] = secrets.AzureAccountKey.Trim();
            else
                env["AZURE_ACCOUNT_SAS"] = (secrets.AzureSasToken ?? string.Empty).Trim().TrimStart('?');
            if (!string.IsNullOrWhiteSpace(settings.AzureEndpointSuffix))
                env["AZURE_ENDPOINT_SUFFIX"] = settings.AzureEndpointSuffix.Trim();
        }
        // GOOGLE_APPLICATION_CREDENTIALS and RCLONE_CONFIG point to files the resolver writes.
        if (backend == BackupBackend.Gcs && GcsProjectId(secrets.GcsCredentialsJson) is { } projectId)
            env["GOOGLE_PROJECT_ID"] = projectId;
        if (backend == BackupBackend.Swift)
        {
            foreach (var (key, value) in ParseSwiftEnvironment(secrets.SwiftEnvironment))
                env[key] = value;
        }
        return env;
    }

    /// <summary>
    /// Rejects a mirror pairing that one <c>restic copy</c> call cannot serve: backend credentials
    /// and options are process-wide (not per repository), so two repositories of the same
    /// backend type must share them (SFTP: the same SSH connection).
    /// </summary>
    public static void ValidateMirror(
        BackupRepository source, ResticBackendSecrets sourceSecrets, BackupRepository mirror, ResticBackendSecrets mirrorSecrets)
    {
        if (source.Id == mirror.Id)
            throw new ResticException("mirror_invalid", "A repository cannot mirror into itself.");
        if (source.Backend != mirror.Backend)
            return;
        var a = ResticRepositorySettings.Parse(source.SettingsJson);
        var b = ResticRepositorySettings.Parse(mirror.SettingsJson);
        var compatible = source.Backend switch
        {
            BackupBackend.Sftp => a.StorageConnectionId == b.StorageConnectionId,
            BackupBackend.Rest => (sourceSecrets.RestUsername ?? "") == (mirrorSecrets.RestUsername ?? "")
                                  && (sourceSecrets.RestPassword ?? "") == (mirrorSecrets.RestPassword ?? ""),
            BackupBackend.S3 => sourceSecrets.S3AccessKeyId == mirrorSecrets.S3AccessKeyId
                                && sourceSecrets.S3SecretAccessKey == mirrorSecrets.S3SecretAccessKey
                                && (a.Region ?? "") == (b.Region ?? "")
                                && a.S3PathStyle == b.S3PathStyle,
            BackupBackend.B2 => sourceSecrets.B2AccountId == mirrorSecrets.B2AccountId
                                && sourceSecrets.B2AccountKey == mirrorSecrets.B2AccountKey,
            BackupBackend.Azure => a.AzureAccountName == b.AzureAccountName
                                   && (a.AzureEndpointSuffix ?? "") == (b.AzureEndpointSuffix ?? "")
                                   && (sourceSecrets.AzureAccountKey ?? "") == (mirrorSecrets.AzureAccountKey ?? "")
                                   && (sourceSecrets.AzureSasToken ?? "") == (mirrorSecrets.AzureSasToken ?? ""),
            BackupBackend.Gcs => (sourceSecrets.GcsCredentialsJson ?? "").Trim() == (mirrorSecrets.GcsCredentialsJson ?? "").Trim(),
            BackupBackend.Swift => ParseSwiftEnvironment(sourceSecrets.SwiftEnvironment)
                .OrderBy(e => e.Key, StringComparer.Ordinal)
                .SequenceEqual(ParseSwiftEnvironment(mirrorSecrets.SwiftEnvironment).OrderBy(e => e.Key, StringComparer.Ordinal)),
            BackupBackend.Rclone => ParseRcloneConfig(sourceSecrets.RcloneConfig).Config == ParseRcloneConfig(mirrorSecrets.RcloneConfig).Config,
            _ => true,
        };
        if (!compatible)
            throw new ResticException("mirror_conflict", "Both repositories use the same backend type with different credentials or options.");
    }

    /// <summary>
    /// The <c>sftp.command</c> restic runs: the same hardened ssh invocation the rsync
    /// provider uses (pinned known_hosts, batch mode, key only) plus the sftp subsystem.
    /// With <paramref name="password"/> the connection authenticates by password through
    /// the <c>SSH_ASKPASS</c> helper the resolver writes (same host pinning).
    /// </summary>
    public static string BuildSftpCommand(RsyncSshConnectionSettings ssh, bool password = false)
        => (password ? RsyncProcessRunner.BuildPasswordRemoteShell(ssh) : RsyncProcessRunner.BuildRemoteShell(ssh))
           + $" {ssh.Username}@{ssh.Host} -s sftp";

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

    /// <summary>Section name the pasted rclone remote is written under (<c>rclone:kaimo:&lt;path&gt;</c>).</summary>
    public const string RcloneRemoteName = "kaimo";

    /// <summary>A validated rclone remote: its type and the normalized config file content.</summary>
    public sealed record RcloneRemote(string Type, string Config);

    // Remotes that wrap another remote or a local path: with one section they can only point
    // into the server's own file system, past the local-root check.
    private static readonly HashSet<string> BlockedRcloneTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "local", "alias", "union", "combine", "crypt", "chunker", "hasher", "compress", "cache", "memory",
    };

    /// <summary>
    /// Parses one rclone.conf remote section (header optional). Rejects wrapper/local types and
    /// options that run a command or read a file on the server (<c>ssh</c>, <c>*_command</c>,
    /// <c>*_file</c>, <c>*_path</c>, certificates) — inline variants such as
    /// <c>service_account_credentials</c> or <c>key_pem</c> work instead.
    /// </summary>
    public static RcloneRemote ParseRcloneConfig(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64 * 1024 || text.Contains('\0'))
            throw new ResticException("rclone_config_invalid", "Paste one remote section of an rclone.conf.");
        var lines = new List<string>();
        string? type = null;
        var sections = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
                continue;
            if (line[0] == '[')
            {
                if (++sections > 1 || lines.Count > 0)
                    throw new ResticException("rclone_config_invalid", "Paste exactly one remote section.");
                continue;
            }
            var eq = line.IndexOf('=');
            var key = eq > 0 ? line[..eq].Trim().ToLowerInvariant() : string.Empty;
            if (!RcloneKeyPattern().IsMatch(key))
                throw new ResticException("rclone_config_invalid", "Every line must have the form 'name = value'.");
            if (key is "ssh" or "ca_cert" or "client_cert" or "client_key" or "kerberos_ccache"
                || key.EndsWith("_command", StringComparison.Ordinal)
                || key.EndsWith("_file", StringComparison.Ordinal)
                || key.EndsWith("_path", StringComparison.Ordinal))
                throw new ResticException("rclone_option_blocked", $"The rclone option '{key}' is not allowed.");
            var value = line[(eq + 1)..].Trim();
            if (key == "type")
                type = value;
            lines.Add($"{key} = {value}");
        }
        if (string.IsNullOrEmpty(type))
            throw new ResticException("rclone_config_invalid", "The remote has no 'type'.");
        if (BlockedRcloneTypes.Contains(type))
            throw new ResticException("rclone_type_blocked", $"rclone remotes of type '{type}' are not allowed.");
        return new RcloneRemote(type, $"[{RcloneRemoteName}]\n{string.Join('\n', lines)}\n");
    }

    /// <summary>
    /// Parses the Swift variables (openrc style: <c>export OS_X="y"</c>, comments and shell lines
    /// ignored). Only <c>OS_*</c> / <c>ST_*</c> names reach the restic process.
    /// </summary>
    public static Dictionary<string, string> ParseSwiftEnvironment(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16 * 1024 || text.Contains('\0'))
            throw new ResticException("swift_auth_missing", "OS_AUTH_URL (or ST_AUTH) is required.");
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line[7..].TrimStart();
            var match = AssignmentPattern().Match(line);
            if (!match.Success)
                continue; // comments and the echo/read/if lines of a downloaded openrc file
            var key = match.Groups[1].Value;
            var value = match.Groups[2].Value.Trim();
            if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])
                value = value[1..^1];
            if (!key.StartsWith("OS_", StringComparison.Ordinal) && !key.StartsWith("ST_", StringComparison.Ordinal))
                throw new ResticException("swift_variable_invalid", $"Only OS_* and ST_* variables are allowed ({key}).");
            if (value.StartsWith('$'))
                throw new ResticException("swift_variable_reference", $"Replace {value} with the actual value.");
            env[key] = value;
        }
        if (!env.ContainsKey("OS_AUTH_URL") && !env.ContainsKey("ST_AUTH"))
            throw new ResticException("swift_auth_missing", "OS_AUTH_URL (or ST_AUTH) is required.");
        return env;
    }

    /// <summary>
    /// Validates a GCS key and returns its <c>project_id</c>. Only service-account and user keys:
    /// "external account" keys can make the client read local files or run programs.
    /// </summary>
    public static string? GcsProjectId(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 64 * 1024)
            throw new ResticException("gcs_credentials_invalid", "Paste the service account key (JSON).");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.GetString() is not ("service_account" or "authorized_user"))
                throw new ResticException("gcs_credentials_invalid", "Paste the service account key (JSON).");
            return root.TryGetProperty("project_id", out var project) && project.ValueKind == JsonValueKind.String
                   && ProjectPattern().IsMatch(project.GetString()!)
                ? project.GetString()
                : null;
        }
        catch (JsonException ex)
        {
            throw new ResticException("gcs_credentials_invalid", "Paste the service account key (JSON).", ex);
        }
    }

    private static void ValidateContainer(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !ContainerPattern().IsMatch(name))
            throw new ResticException("bucket_invalid", "The bucket or container name is invalid.");
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

    // B2, GCS, Azure and Swift names; never ':' or '/', which would change the restic URL.
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{1,254}$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerPattern();

    [GeneratedRegex("^[a-z0-9]{3,24}$", RegexOptions.CultureInvariant)]
    private static partial Regex AzureAccountPattern();

    [GeneratedRegex("^[A-Za-z0-9-]+(\\.[A-Za-z0-9-]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{4,61}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectPattern();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex RcloneKeyPattern();

    [GeneratedRegex("^([A-Z][A-Z0-9_]{0,63})=(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex AssignmentPattern();
}
