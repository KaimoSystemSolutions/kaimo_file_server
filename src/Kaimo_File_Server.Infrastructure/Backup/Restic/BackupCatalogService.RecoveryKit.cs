using System.Globalization;
using System.Text;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

public partial class BackupCatalogService
{
    // ══════════════════════════════════════════
    //  Recovery kit
    // ══════════════════════════════════════════

    /// <summary>Builds the recovery kit text and records the download. Never contains backend secrets.</summary>
    public async Task<(string FileName, string Content)> BuildRecoveryKitAsync(UserContext actor, Guid repositoryId, CancellationToken ct = default)
    {
        await RequireRepositoriesAsync(actor);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var repository = await db.BackupRepositories.FirstOrDefaultAsync(r => r.Id == repositoryId, ct)
                         ?? throw new ResticException("repository_missing", "The repository no longer exists.");

        var password = resolver.ReadPassword(repository);
        var settings = ResticRepositorySettings.Parse(repository.SettingsJson);
        string url;
        var parameters = new StringBuilder();
        using (var target = await resolver.ResolveAsync(repository, resolver.ReadSecrets(repository), password, ct))
            url = target.RepositoryUrl;
        if (!string.IsNullOrWhiteSpace(settings.Region)) parameters.AppendLine("AWS_DEFAULT_REGION=" + settings.Region);
        if (settings.S3PathStyle) parameters.AppendLine("-o s3.bucket-lookup=path");
        if (repository.Backend == BackupBackend.Sftp && settings.StorageConnectionId is { } connectionId)
        {
            var connection = await db.StorageConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == connectionId, ct);
            if (connection is not null)
            {
                try
                {
                    var ssh = ExternalStorage.PinnedSshSettings.ParseAndValidate(connection, connection.ProviderId, requireKnownHosts: false);
                    parameters.AppendLine($"SSH {ssh.Username}@{ssh.Host}:{ssh.Port} — host key {ssh.ExpectedHostKeySha256}");
                    parameters.AppendLine($"-o sftp.command=\"ssh -i <ssh key> -p {ssh.Port} {ssh.Username}@{ssh.Host} -s sftp\"");
                }
                catch (ExternalStorage.ProtocolConfigurationException) { }
            }
        }
        if (repository.Backend is BackupBackend.Rest && !string.IsNullOrEmpty(resolver.ReadSecrets(repository).RestUsername))
            parameters.AppendLine("RESTIC_REST_USERNAME / RESTIC_REST_PASSWORD");
        if (repository.Backend is BackupBackend.S3)
            parameters.AppendLine("AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY");
        var secrets = resolver.ReadSecrets(repository);
        switch (repository.Backend)
        {
            case BackupBackend.B2:
                parameters.AppendLine("B2_ACCOUNT_ID / B2_ACCOUNT_KEY");
                break;
            case BackupBackend.Azure:
                parameters.AppendLine("AZURE_ACCOUNT_NAME=" + settings.AzureAccountName);
                if (!string.IsNullOrWhiteSpace(settings.AzureEndpointSuffix))
                    parameters.AppendLine("AZURE_ENDPOINT_SUFFIX=" + settings.AzureEndpointSuffix);
                parameters.AppendLine(string.IsNullOrEmpty(secrets.AzureSasToken) ? "AZURE_ACCOUNT_KEY" : "AZURE_ACCOUNT_SAS");
                break;
            case BackupBackend.Gcs:
                parameters.AppendLine("GOOGLE_APPLICATION_CREDENTIALS=<service account key file>");
                if (ResticBackend.GcsProjectId(secrets.GcsCredentialsJson) is { } projectId)
                    parameters.AppendLine("GOOGLE_PROJECT_ID=" + projectId);
                break;
            case BackupBackend.Swift:
                var swift = ResticBackend.ParseSwiftEnvironment(secrets.SwiftEnvironment);
                if (swift.TryGetValue("OS_AUTH_URL", out var authUrl))
                    parameters.AppendLine("OS_AUTH_URL=" + authUrl);
                parameters.AppendLine(string.Join(" / ", swift.Keys.Where(k => k != "OS_AUTH_URL")));
                break;
            case BackupBackend.Rclone:
                parameters.AppendLine($"rclone remote [{ResticBackend.RcloneRemoteName}], type {ResticBackend.ParseRcloneConfig(secrets.RcloneConfig).Type} (rclone must be installed)");
                parameters.AppendLine("RCLONE_CONFIG=<rclone.conf with that remote>");
                break;
        }

        var template = Resources.ResourceManager.GetString("Web_Backup_Kit_Template") ?? DefaultKitTemplate;
        var now = time.GetUtcNow().UtcDateTime;
        var content = string.Format(CultureInfo.InvariantCulture, template,
            repository.Name,
            repository.Backend,
            url,
            parameters.Length > 0 ? parameters.ToString().TrimEnd() : "-",
            password,
            repository.ResticRepositoryId ?? "-",
            await restic.VersionAsync(ct),
            now.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
            ResticBackend.KitCode(password));

        if (!demo.ReadOnly)
        {
            repository.KitDownloadedAtUtc = now;
            await db.SaveChangesAsync(ct);
        }
        logger.LogWarning("Recovery kit of backup repository {Name} downloaded by {User}.", repository.Name, actor.User.Username);

        var safeName = new string(repository.Name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return ($"kaimo-recovery-kit-{(safeName.Length > 0 ? safeName : "repository")}.txt", content);
    }

    /// <summary>Activates the repository once the admin typed the code printed in the downloaded kit.</summary>
    public async Task<bool> ConfirmRecoveryKitAsync(UserContext actor, Guid repositoryId, string code)
    {
        await RequireRepositoriesAsync(actor);
        RequireWritable();
        await using var db = await dbFactory.CreateDbContextAsync();
        var repository = await db.BackupRepositories.FirstOrDefaultAsync(r => r.Id == repositoryId)
                         ?? throw new ResticException("repository_missing", "The repository no longer exists.");
        if (repository.KitDownloadedAtUtc is null)
            throw new ResticException("kit_not_downloaded", "Download the recovery kit first.");
        if (!ResticBackend.KitCodeMatches(resolver.ReadPassword(repository), code))
            return false;
        var now = time.GetUtcNow().UtcDateTime;
        repository.KitConfirmedAtUtc = now;
        repository.KitConfirmedByUserId = actor.User.Id;
        if (repository.State == BackupRepositoryState.PendingRecoveryKit)
            repository.State = BackupRepositoryState.Active;
        repository.UpdatedAtUtc = now;
        await db.SaveChangesAsync();
        return true;
    }

    // Fallback only; the localized template lives in the resource files.
    private const string DefaultKitTemplate = "Kaimo recovery kit\n\nRepository: {0} ({1})\nRESTIC_REPOSITORY={2}\n{3}\nRESTIC_PASSWORD={4}\nRepository id: {5}\nrestic: {6}\nCreated: {7}\nKit code: {8}\n";
}
