using System.Security.Cryptography;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

/// <summary>
/// Turns a stored repository into a runnable <see cref="ResticTarget"/>: decrypts the
/// password and backend secrets (Web process only — the vault lives there), loads the SSH
/// connection of SFTP repositories or the SMB connection of SMB repositories and writes key /
/// CA / rclone config files (or, for SSH password connections, the password and an
/// <c>SSH_ASKPASS</c> helper) into a private temp directory that the target deletes on dispose.
/// </summary>
public sealed class ResticTargetResolver(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICredentialVault vault,
    IConfiguration configuration,
    ILogger<ResticTargetResolver>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<ResticTargetResolver>.Instance;

    /// <summary>Internal application data (DataProtection keys, certificates); part of Full backups.</summary>
    public string ApplicationDataPath => configuration["Storage:ApplicationDataPath"] ?? "/data/kaimo-system";

    /// <summary>The storage pools: direct child folders of the storage root, as the hosts discover them.</summary>
    public IReadOnlyList<string> Pools
    {
        get
        {
            var root = configuration["Storage:RootPath"] ?? "/data/storage";
            return Directory.Exists(root)
                ? Directory.GetDirectories(root).Select(Path.TrimEndingDirectorySeparator).Order(StringComparer.Ordinal).ToList()
                : [];
        }
    }

    /// <summary>Private temp files (SSH keys, CA certificates, GCS keys, rclone configs) of running restic processes.</summary>
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

    /// <summary>
    /// Resolves a stored repository with its stored password and secrets. Configuration rclone
    /// rewrites during the run (refreshed OAuth tokens) is stored back on dispose.
    /// </summary>
    public Task<ResticTarget> ResolveAsync(BackupRepository repository, CancellationToken ct)
        => ResolveCoreAsync(repository, ReadSecrets(repository), ReadPassword(repository), storeRcloneUpdates: true, ct);

    /// <summary>
    /// Resolves with explicit secrets/password (repository creation, "connect existing", probing
    /// edited credentials). Nothing is written back: the secrets may not be the stored ones.
    /// </summary>
    public Task<ResticTarget> ResolveAsync(
        BackupRepository repository, ResticBackendSecrets secrets, string password, CancellationToken ct)
        => ResolveCoreAsync(repository, secrets, password, storeRcloneUpdates: false, ct);

    private async Task<ResticTarget> ResolveCoreAsync(
        BackupRepository repository, ResticBackendSecrets secrets, string password, bool storeRcloneUpdates, CancellationToken ct)
    {
        var settings = ResticRepositorySettings.Parse(repository.SettingsJson);
        ResticBackend.Validate(repository.Backend, settings, secrets, LocalRoots);

        string? tempDir = null;
        try
        {
            var arguments = new List<string>();
            var sshEnvironment = new Dictionary<string, string>();
            RsyncSshConnectionSettings? ssh = null;

            if (repository.Backend == BackupBackend.Sftp)
            {
                var connection = await LoadConnectionAsync(settings.StorageConnectionId!.Value, ResticBackend.SshProviderIds, ct);
                ssh = ParseSshSettings(connection);
                var usePassword = connection.AuthorizationMode == Core.Domain.StorageAuthorizationMode.UsernamePassword;
                if (usePassword)
                {
                    if (!PinnedSshSettings.TryReadVaultPassword(connection, vault, out var sshPassword))
                        throw new ResticException("ssh_password_missing", "The SSH connection has no password.");
                    tempDir ??= CreateTempDirectory();
                    sshEnvironment["SSH_ASKPASS"] = await WriteAskpassAsync(tempDir, sshPassword, ct);
                    sshEnvironment["SSH_ASKPASS_REQUIRE"] = "force";
                }
                else if (string.IsNullOrWhiteSpace(ssh.PrivateKeySecretReference))
                {
                    tempDir ??= CreateTempDirectory();
                    var keyPath = Path.Combine(tempDir, "id_key");
                    var privateKey = PinnedSshSettings.TryReadVaultPrivateKey(connection, vault, out var key)
                        ? key
                        : throw new ResticException("private_key_missing", "The SSH connection has no private key.");
                    try { await ProtocolTemporaryFile.WriteRestrictedBytesAsync(keyPath, privateKey, ct); }
                    finally { CryptographicOperations.ZeroMemory(privateKey); }
                    ssh = ssh with { PrivateKeySecretReference = keyPath };
                }
                try
                {
                    arguments.AddRange(["-o", "sftp.command=" + ResticBackend.BuildSftpCommand(ssh, usePassword)]);
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
            foreach (var (name, value) in sshEnvironment)
                env[name] = value;

            if (repository.Backend == BackupBackend.Gcs)
            {
                tempDir ??= CreateTempDirectory();
                var keyPath = Path.Combine(tempDir, "gcs-key.json");
                await ProtocolTemporaryFile.WriteRestrictedTextAsync(keyPath, secrets.GcsCredentialsJson!, ct);
                env["GOOGLE_APPLICATION_CREDENTIALS"] = keyPath;
            }
            Action? beforeCleanup = null;
            if (repository.Backend == BackupBackend.Rclone)
            {
                tempDir ??= CreateTempDirectory();
                var configPath = Path.Combine(tempDir, "rclone.conf");
                var written = ResticBackend.ParseRcloneConfig(secrets.RcloneConfig).Config;
                await ProtocolTemporaryFile.WriteRestrictedTextAsync(configPath, written, ct);
                env["RCLONE_CONFIG"] = configPath;
                if (storeRcloneUpdates)
                    beforeCleanup = () => StoreRcloneUpdate(repository.Id, configPath, written);
            }
            if (repository.Backend == BackupBackend.Smb)
            {
                // Built from the connection on every run; nothing to store back (no tokens).
                var (smb, credentials) = await LoadSmbConnectionAsync(settings.StorageConnectionId!.Value, ct);
                tempDir ??= CreateTempDirectory();
                var configPath = Path.Combine(tempDir, "rclone.conf");
                await ProtocolTemporaryFile.WriteRestrictedTextAsync(configPath,
                    ResticBackend.BuildSmbRcloneConfig(smb, credentials.Username, credentials.Password, credentials.Domain), ct);
                env["RCLONE_CONFIG"] = configPath;
            }
            return new ResticTarget(url, env, arguments, tempDir, beforeCleanup);
        }
        catch
        {
            if (tempDir is not null)
                new ResticTarget(string.Empty, new Dictionary<string, string>(), [], tempDir).Dispose();
            throw;
        }
    }

    /// <summary>
    /// rclone saves refreshed OAuth tokens into its config file. Providers whose refresh tokens
    /// rotate and expire (OneDrive) would otherwise fail once the pasted token runs out, so a
    /// changed config is validated like pasted input and replaces the stored section.
    /// </summary>
    internal void StoreRcloneUpdate(Guid repositoryId, string configPath, string written)
    {
        try
        {
            if (!File.Exists(configPath))
                return;
            var updated = ResticBackend.ParseRcloneConfig(File.ReadAllText(configPath)).Config;
            if (updated == written)
                return;
            using var db = dbFactory.CreateDbContext();
            var repository = db.BackupRepositories.FirstOrDefault(r => r.Id == repositoryId);
            if (repository is null)
                return;
            var secrets = ReadSecrets(repository);
            secrets.RcloneConfig = updated;
            repository.EncryptedSecrets = ProtectSecrets(repositoryId, secrets);
            db.SaveChanges();
            _logger.LogInformation("Stored the rclone configuration of backup repository {Name} updated by rclone (refreshed token).", repository.Name);
        }
        catch (Exception ex)
        {
            // Never fails the run: the previous token stays stored and may still be valid.
            _logger.LogWarning(ex, "Could not store the rclone configuration updated during a run of backup repository {Id}.", repositoryId);
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

    /// <summary>
    /// Loads an External Storage SMB connection with its decrypted credentials, validated like the
    /// SMB provider does. A disabled connection is not used.
    /// </summary>
    internal async Task<(SmbConnectionSettings Settings, SmbCredentials Credentials)> LoadSmbConnectionAsync(
        Guid connectionId, CancellationToken ct)
    {
        var connection = await LoadConnectionAsync(connectionId, ResticBackend.SmbProviderIds, ct);
        if (connection.State == Core.Domain.StorageConnectionState.Disabled)
            throw new ResticException("connection_disabled", "The SMB connection is disabled.");
        try
        {
            var settings = SmbStorageConnectionProvider.ParseAndValidate(connection);
            return (settings, SmbStorageConnectionProvider.ReadCredentials(vault, connection, settings));
        }
        catch (ProtocolConfigurationException ex)
        {
            throw new ResticException(ex.Code, ex.Message, ex);
        }
        catch (InvalidOperationException ex) when (ex is not ResticException)
        {
            // No or undecryptable credentials.
            throw new ResticException("credentials_invalid", "The SMB credentials are invalid.", ex);
        }
    }

    private static RsyncSshConnectionSettings ParseSshSettings(Core.Domain.StorageConnection connection)
    {
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

    /// <summary>
    /// Writes the SSH password (0600) and the <c>SSH_ASKPASS</c> helper (0700) that prints it.
    /// ssh runs the helper instead of prompting, so the password never appears in arguments
    /// or the environment. Returns the helper path.
    /// </summary>
    internal static async Task<string> WriteAskpassAsync(string tempDir, string password, CancellationToken ct)
    {
        var passwordPath = Path.Combine(tempDir, "ssh-password");
        if (passwordPath.Contains('\''))
            throw new ResticException("temp_path_invalid", "The restic temp directory must not contain quotes.");
        await ProtocolTemporaryFile.WriteRestrictedTextAsync(passwordPath, password, ct);
        var helperPath = Path.Combine(tempDir, "askpass");
        // ssh executes the helper, so Backup:Restic:TempDirectory must not be on a noexec mount.
        await ProtocolTemporaryFile.WriteRestrictedTextAsync(helperPath, $"#!/bin/sh\nexec /bin/cat '{passwordPath}'\n", ct);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(helperPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return helperPath;
    }

    /// <summary>Loads the storage connection a repository reuses; its provider must be one of <paramref name="providerIds"/>.</summary>
    private async Task<Core.Domain.StorageConnection> LoadConnectionAsync(
        Guid connectionId, IReadOnlySet<string> providerIds, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = await db.StorageConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == connectionId, ct)
                         ?? throw new ResticException("connection_missing", "The storage connection no longer exists.");
        if (!providerIds.Contains(connection.ProviderId))
            throw new ResticException("connection_invalid",
                $"The connection type '{connection.ProviderId}' cannot be used for this repository ({string.Join(", ", providerIds)}).");
        return connection;
    }
}
