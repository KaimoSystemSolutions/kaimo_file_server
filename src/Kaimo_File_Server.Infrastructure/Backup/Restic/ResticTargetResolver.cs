using System.Security.Cryptography;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

/// <summary>
/// Turns a stored repository into a runnable <see cref="ResticTarget"/>: decrypts the
/// password and backend secrets (Web process only — the vault lives there), loads the SSH
/// connection of SFTP repositories and writes key / CA files into a private temp directory
/// that the target deletes on dispose.
/// </summary>
public sealed class ResticTargetResolver(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICredentialVault vault,
    IConfiguration configuration)
{
    private string ApplicationDataPath => configuration["Storage:ApplicationDataPath"] ?? "/data/kaimo-system";

    /// <summary>Private temp files (SSH keys, CA certificates) of running restic processes.</summary>
    public string TempRoot => configuration["Backup:Restic:TempDirectory"]
        ?? Path.Combine(ApplicationDataPath, ".kaimo-restic-tmp");

    /// <summary>Directories local repositories may be created in.</summary>
    public IReadOnlyList<string> LocalRoots
    {
        get
        {
            var roots = configuration.GetSection("Backup:Restic:LocalRoots").GetChildren()
                .Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList();
            if (roots.Count == 0 && configuration["Backup:Restic:LocalRoots"] is { Length: > 0 } single)
                roots.AddRange(single.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return roots.Count > 0
                ? roots
                : [Path.Combine(configuration["Backup:RootPath"] ?? "/data/kaimo-backups", "restic")];
        }
    }

    public ResticBackendSecrets ReadSecrets(BackupRepository repository)
        => string.IsNullOrEmpty(repository.EncryptedSecrets)
            ? new ResticBackendSecrets()
            : vault.Unprotect<ResticBackendSecrets>(repository.EncryptedSecrets, ResticBackend.SecretsContext(repository.Id));

    public string ReadPassword(BackupRepository repository)
        => string.IsNullOrEmpty(repository.EncryptedPassword)
            ? throw new ResticException("password_missing", "The repository has no password yet.")
            : vault.Unprotect<string>(repository.EncryptedPassword, ResticBackend.PasswordContext(repository.Id));

    public string ProtectSecrets(Guid repositoryId, ResticBackendSecrets secrets)
        => vault.Protect(secrets, ResticBackend.SecretsContext(repositoryId));

    public string ProtectPassword(Guid repositoryId, string password)
        => vault.Protect(password, ResticBackend.PasswordContext(repositoryId));

    /// <summary>Resolves a stored repository with its stored password and secrets.</summary>
    public Task<ResticTarget> ResolveAsync(BackupRepository repository, CancellationToken ct)
        => ResolveAsync(repository, ReadSecrets(repository), ReadPassword(repository), ct);

    /// <summary>Resolves with explicit secrets/password (repository creation, "connect existing").</summary>
    public async Task<ResticTarget> ResolveAsync(
        BackupRepository repository, ResticBackendSecrets secrets, string password, CancellationToken ct)
    {
        var settings = ResticRepositorySettings.Parse(repository.SettingsJson);
        ResticBackend.Validate(repository.Backend, settings, secrets, LocalRoots);

        string? tempDir = null;
        try
        {
            var arguments = new List<string>();
            RsyncSshConnectionSettings? ssh = null;

            if (repository.Backend == BackupBackend.Sftp)
            {
                ssh = await LoadSshSettingsAsync(settings.StorageConnectionId!.Value, ct);
                if (string.IsNullOrWhiteSpace(ssh.PrivateKeySecretReference))
                {
                    tempDir ??= CreateTempDirectory();
                    var keyPath = Path.Combine(tempDir, "id_key");
                    var privateKey = await ReadVaultKeyAsync(settings.StorageConnectionId.Value, ct);
                    try { await ProtocolTemporaryFile.WriteRestrictedBytesAsync(keyPath, privateKey, ct); }
                    finally { CryptographicOperations.ZeroMemory(privateKey); }
                    ssh = ssh with { PrivateKeySecretReference = keyPath };
                }
                try
                {
                    arguments.AddRange(["-o", "sftp.command=" + ResticBackend.BuildSftpCommand(ssh)]);
                }
                catch (ProtocolConfigurationException ex)
                {
                    throw new ResticException(ex.Code, ex.Message, ex);
                }
            }

            if (repository.Backend == BackupBackend.S3 && settings.S3PathStyle)
                arguments.AddRange(["-o", "s3.bucket-lookup=path"]);

            if (!string.IsNullOrWhiteSpace(settings.CaCertificatePem))
            {
                tempDir ??= CreateTempDirectory();
                var caPath = Path.Combine(tempDir, "ca.pem");
                await ProtocolTemporaryFile.WriteRestrictedTextAsync(caPath, settings.CaCertificatePem, ct);
                arguments.AddRange(["--cacert", caPath]);
            }

            var url = ResticBackend.BuildUrl(repository.Backend, settings, ssh);
            var env = ResticBackend.BuildEnvironment(repository.Backend, settings, secrets, password, url);
            return new ResticTarget(url, env, arguments, tempDir);
        }
        catch
        {
            if (tempDir is not null)
                new ResticTarget(string.Empty, new Dictionary<string, string>(), [], tempDir).Dispose();
            throw;
        }
    }

    /// <summary>Removes temp files left behind by a crashed process. Called once on startup.</summary>
    public void WipeTempRoot()
    {
        try
        {
            if (Directory.Exists(TempRoot))
                Directory.Delete(TempRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string CreateTempDirectory()
    {
        var path = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
        {
            Directory.CreateDirectory(TempRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    private async Task<RsyncSshConnectionSettings> LoadSshSettingsAsync(Guid connectionId, CancellationToken ct)
    {
        var connection = await LoadConnectionAsync(connectionId, ct);
        try
        {
            // restic runs the OpenSSH client, which needs the pinned known_hosts file.
            return PinnedSshSettings.ParseAndValidate(connection, connection.ProviderId, requireKnownHosts: true);
        }
        catch (ProtocolConfigurationException ex)
        {
            throw new ResticException(ex.Code is "secret_reference_invalid" ? "known_hosts_missing" : ex.Code, ex.Message, ex);
        }
    }

    private async Task<byte[]> ReadVaultKeyAsync(Guid connectionId, CancellationToken ct)
    {
        var connection = await LoadConnectionAsync(connectionId, ct);
        return PinnedSshSettings.TryReadVaultPrivateKey(connection, vault, out var key)
            ? key
            : throw new ResticException("private_key_missing", "The SSH connection has no private key.");
    }

    private async Task<Core.Domain.StorageConnection> LoadConnectionAsync(Guid connectionId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = await db.StorageConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == connectionId, ct)
                         ?? throw new ResticException("connection_missing", "The SSH connection no longer exists.");
        if (!ResticBackend.SshProviderIds.Contains(connection.ProviderId))
            throw new ResticException("connection_invalid", "Only SFTP or rsync/SSH connections can be used.");
        return connection;
    }
}
