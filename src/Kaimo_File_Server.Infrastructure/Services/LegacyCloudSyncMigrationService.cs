using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Services;

/// <summary>
/// Imports legacy CloudSettings mappings into the first-class model. The import
/// is safe to repeat and intentionally retains the source JSON as a read-only
/// compatibility fallback until the later verified cleanup package.
/// </summary>
public sealed class LegacyCloudSyncMigrationService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICredentialVault credentialVault,
    ILogger<LegacyCloudSyncMigrationService> logger) : ILegacyCloudSyncMigrationService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task EnsureMigratedAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    await ImportAsync(cancellationToken);
                    return;
                }
                catch (DbUpdateException) when (attempt < 2)
                {
                    // Another application instance may have imported the same
                    // unique share/path pair. Reload the committed rows and
                    // converge instead of creating a second connection.
                    await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task ImportAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var shares = await db.ShareDefinitions.ToListAsync(cancellationToken);
        var allDefinitions = await db.SyncDefinitions.ToListAsync(cancellationToken);
        var definitionsByKey = allDefinitions.ToDictionary(
            sync => new SourceKey(sync.LocalShareId, sync.LocalPath),
            SourceKeyComparer.Instance);
        var observedKeys = new HashSet<SourceKey>(SourceKeyComparer.Instance);
        int createdCount = 0;
        int updatedCount = 0;

        foreach (var share in shares)
        {
            foreach (var (untrustedPath, folder) in share.CloudSettings.Folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string localPath = ShareRelativePath.Normalize(untrustedPath);
                var key = new SourceKey(share.Id, localPath);
                observedKeys.Add(key);
                string checksum = CalculateChecksum(folder);
                if (!definitionsByKey.TryGetValue(key, out var definition))
                {
                    Guid? runAsUserId = await ResolveRunAsUserIdAsync(
                        db, folder.Schedule?.RunAsUsername, cancellationToken);
                    var connection = CreateConnection(share, folder, runAsUserId);
                    connection.EncryptedCredentialPayload = credentialVault.ProtectConnectionCredentials(
                        connection,
                        new Dictionary<string, string>(folder.Data, StringComparer.Ordinal));
                    db.StorageConnections.Add(connection);

                    definition = CreateDefinition(
                        share.Id, localPath, folder, connection.Id, runAsUserId, checksum);
                    db.SyncDefinitions.Add(definition);
                    db.SyncDefinitionRuntimes.Add(new SyncDefinitionRuntime
                    {
                        SyncDefinitionId = definition.Id,
                        LastRunAtUtc = folder.LastSync,
                        LastSuccessfulRunAtUtc = folder.LastSync,
                        UpdatedAtUtc = DateTime.UtcNow
                    });
                    definitionsByKey.Add(key, definition);
                    createdCount++;
                    continue;
                }

                // A Package 6 edit or delete clears MigrationSource. Such a row
                // is authoritative and must never be overwritten or recreated
                // from the retained rollback JSON.
                if (definition.MigrationSource != SyncDefinition.LegacyCloudSettingsSource)
                    continue;

                if (definition.MigrationSourceChecksum == checksum && definition.Enabled)
                    continue;

                Guid? existingRunAsUserId = await ResolveRunAsUserIdAsync(
                    db, folder.Schedule?.RunAsUsername, cancellationToken);
                ApplyDefinition(definition, folder, existingRunAsUserId, checksum);
                var connectionToUpdate = await db.StorageConnections.SingleAsync(
                    connection => connection.Id == definition.ConnectionId,
                    cancellationToken);
                connectionToUpdate.ProviderId = folder.Provider;
                connectionToUpdate.AuthorizationMode = GetAuthorizationMode(folder.Provider);
                connectionToUpdate.EffectiveScopes = folder.Data.TryGetValue(
                    "scope", out string? effectiveScope) ? effectiveScope : null;
                connectionToUpdate.EncryptedCredentialPayload = credentialVault.ProtectConnectionCredentials(
                    connectionToUpdate,
                    new Dictionary<string, string>(folder.Data, StringComparer.Ordinal));
                connectionToUpdate.CredentialUpdatedAtUtc = DateTime.UtcNow;
                connectionToUpdate.UpdatedAtUtc = DateTime.UtcNow;
                connectionToUpdate.ConcurrencyVersion = checked(connectionToUpdate.ConcurrencyVersion + 1);
                updatedCount++;
            }
        }

        foreach (var definition in allDefinitions.Where(sync =>
                     sync.MigrationSource == SyncDefinition.LegacyCloudSettingsSource
                     && !observedKeys.Contains(
                     new SourceKey(sync.LocalShareId, sync.LocalPath))))
        {
            if (!definition.Enabled)
                continue;
            definition.Enabled = false;
            definition.UpdatedAtUtc = DateTime.UtcNow;
            updatedCount++;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (createdCount > 0 || updatedCount > 0)
        {
            logger.LogInformation(
                "Imported {CreatedCount} and refreshed {UpdatedCount} legacy cloud-sync definitions.",
                createdCount,
                updatedCount);
        }
    }

    private static StorageConnection CreateConnection(
        ShareDefinition share,
        SyncedFolder folder,
        Guid? runAsUserId)
    {
        DateTime now = DateTime.UtcNow;
        return new StorageConnection
        {
            Id = Guid.NewGuid(),
            CreatedByUserId = runAsUserId ?? Guid.Empty,
            ProviderId = folder.Provider,
            Name = BuildConnectionName(share, folder),
            AuthorizationMode = GetAuthorizationMode(folder.Provider),
            EffectiveScopes = folder.Data.TryGetValue("scope", out string? scope) ? scope : null,
            State = StorageConnectionState.Ready,
            CredentialFormatVersion = StorageConnection.CredentialContextVersion,
            ProtectorPurposeVersion = StorageConnection.CurrentProtectorPurposeVersion,
            CredentialUpdatedAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ConcurrencyVersion = 1
        };
    }

    private static string BuildConnectionName(ShareDefinition share, SyncedFolder folder)
    {
        string label = string.IsNullOrWhiteSpace(folder.DisplayName)
            ? share.Name
            : folder.DisplayName.Trim();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string prefix = $"Legacy sync - {label}";
        return $"{prefix[..Math.Min(prefix.Length, 188)]} ({suffix})";
    }

    private static SyncDefinition CreateDefinition(
        Guid shareId,
        string localPath,
        SyncedFolder folder,
        Guid connectionId,
        Guid? runAsUserId,
        string checksum)
    {
        var definition = new SyncDefinition
        {
            ConnectionId = connectionId,
            LocalShareId = shareId,
            LocalPath = localPath,
            MigrationSource = SyncDefinition.LegacyCloudSettingsSource,
            CreatedByUserId = runAsUserId,
            CreatedAtUtc = DateTime.UtcNow
        };
        ApplyDefinition(definition, folder, runAsUserId, checksum);
        return definition;
    }

    private static void ApplyDefinition(
        SyncDefinition definition,
        SyncedFolder folder,
        Guid? runAsUserId,
        string checksum)
    {
        definition.RemotePath = NormalizeRemotePath(folder.RemotePath);
        definition.RemoteProviderItemId = folder.Data.TryGetValue(
            "remoteFolderId", out string? itemId) ? itemId : null;
        definition.Mode = folder.Mode;
        definition.Schedule = folder.Schedule?.Clone() ?? new CloudSyncSchedule();
        definition.AdvancedSettings = folder.AdvancedSettings?.Clone() ?? new CloudSyncAdvancedSettings();
        definition.DisplayName = folder.DisplayName ?? string.Empty;
        definition.Description = folder.Description ?? string.Empty;
        definition.RequiresRemoteFolderSelection = folder.RequiresRemoteFolderSelection;
        definition.Enabled = true;
        definition.RunAsUserId = runAsUserId;
        definition.UpdatedAtUtc = DateTime.UtcNow;
        definition.MigrationSourceChecksum = checksum;
    }

    private static async Task<Guid?> ResolveRunAsUserIdAsync(
        ApplicationDbContext db,
        string? username,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;
        return await db.Users.Where(user => user.Username == username)
            .Select(user => (Guid?)user.Id)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static string CalculateChecksum(SyncedFolder folder)
    {
        CloudSyncSchedule schedule = folder.Schedule ?? new CloudSyncSchedule();
        CloudSyncAdvancedSettings advanced = folder.AdvancedSettings ?? new CloudSyncAdvancedSettings();
        var canonical = new
        {
            folder.Provider,
            Data = folder.Data.OrderBy(item => item.Key, StringComparer.Ordinal),
            RemotePath = NormalizeRemotePath(folder.RemotePath),
            folder.RequiresRemoteFolderSelection,
            folder.Mode,
            Schedule = new
            {
                schedule.IsEnabled,
                ActiveSlots = (schedule.ActiveSlots ?? []).Order().ToArray(),
                schedule.IntervalSeconds,
                schedule.RunAsUsername
            },
            AdvancedSettings = new
            {
                advanced.MaxFileSizeBytes,
                ExcludedExtensions = (advanced.ExcludedExtensions ?? [])
                    .Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                advanced.MaxUploadBytesPerSecond,
                advanced.MaxDownloadBytesPerSecond,
                // Must be part of the fingerprint: otherwise toggling delete
                // propagation in the legacy editor leaves the checksum unchanged
                // and the refreshed setting never reaches the first-class row.
                advanced.SyncDeletions
            },
            folder.DisplayName,
            folder.Description
        };
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical))));
    }

    private static string NormalizeRemotePath(string? path)
    {
        string normalized = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Replace('\\', '/').Trim('/');
        return normalized.Length == 0 ? "/" : $"/{normalized}";
    }

    private static StorageAuthorizationMode GetAuthorizationMode(string providerId)
        => providerId.Equals("google", StringComparison.OrdinalIgnoreCase)
            ? StorageAuthorizationMode.DelegatedAuthorizationCode
            : StorageAuthorizationMode.DeviceCode;

    private readonly record struct SourceKey(Guid ShareId, string LocalPath);

    private sealed class SourceKeyComparer : IEqualityComparer<SourceKey>
    {
        public static SourceKeyComparer Instance { get; } = new();
        public bool Equals(SourceKey x, SourceKey y)
            => x.ShareId == y.ShareId && string.Equals(
                x.LocalPath, y.LocalPath, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(SourceKey value)
            => HashCode.Combine(
                value.ShareId,
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.LocalPath));
    }
}
