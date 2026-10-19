using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

/// <summary>
/// Server-side entry point for everything the backup UI does. Every method re-checks the
/// actor's authorization (repositories: Global <see cref="ManagementPermission.ManageBackupRepositories"/>;
/// jobs: <see cref="ManagementPermission.ManageBackupJobs"/> on every source share), so the
/// page's own gating is convenience only.
/// </summary>
public sealed partial class BackupCatalogService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IManagementAuthService auth,
    ResticTargetResolver resolver,
    ResticClient restic,
    IBackupRunner runner,
    DemoModeOptions demo,
    TimeProvider time,
    ILogger<BackupCatalogService> logger)
{
    public const int MaximumScheduleTimes = 24;
    public const int MaximumTestRestoreSample = 1000;
    public const int MaximumAlertAfterDays = 365;

    // ══════════════════════════════════════════
    //  Access
    // ══════════════════════════════════════════

    public async Task<BackupAccess> GetAccessAsync(UserContext? actor)
    {
        if (actor is null || !actor.User.IsEnabled)
            return BackupAccess.None;
        var repos = await auth.HasGlobalPermissionAsync(actor, ManagementPermission.ManageBackupRepositories);
        var jobScope = await auth.GetAuthorizedShareIdsAsync(actor, ManagementPermission.ManageBackupJobs);
        var canJobs = jobScope.IsUnrestricted || jobScope.ScopeIds.Count > 0;
        var restoreScope = await auth.GetAuthorizedShareIdsAnyAsync(actor,
            ManagementPermission.RestoreFromBackup | ManagementPermission.RestoreBackupInPlace);
        return new BackupAccess(repos, canJobs, jobScope.IsUnrestricted, jobScope.ScopeIds.ToHashSet(),
            restoreScope.IsUnrestricted, restoreScope.ScopeIds.ToHashSet());
    }

    private async Task<BackupAccess> RequireRepositoriesAsync(UserContext actor)
    {
        var access = await GetAccessAsync(actor);
        if (!access.CanManageRepositories)
            throw new UnauthorizedAccessException("ManageBackupRepositories at global scope is required.");
        return access;
    }

    private async Task<BackupAccess> RequireJobsAsync(UserContext actor)
    {
        var access = await GetAccessAsync(actor);
        if (!access.CanManageJobs)
            throw new UnauthorizedAccessException("ManageBackupJobs is required.");
        return access;
    }

    private void RequireWritable()
    {
        if (demo.ReadOnly)
            throw new ReadOnlyDemoException();
    }

    // ══════════════════════════════════════════
    //  Activity
    // ══════════════════════════════════════════

    /// <summary>Recent runs: all for Global admins, otherwise only runs of visible jobs.</summary>
    public async Task<IReadOnlyList<BackupRun>> ListRunsAsync(UserContext actor, int take = 200)
    {
        var access = await GetAccessAsync(actor);
        if (!access.CanAccessPage)
            return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.BackupRuns.AsNoTracking().OrderByDescending(r => r.QueuedAtUtc).AsQueryable();
        if (access.CanManageRepositories && access.JobsUnrestricted)
            return await query.Take(take).ToListAsync();

        var jobs = await db.BackupJobs.AsNoTracking().Include(j => j.Sources).ToListAsync();
        var visible = jobs.Where(j => IsJobVisible(access, j)).Select(j => (Guid?)j.Id).ToList();
        // Restores carry no job: a scoped admin sees the restores they started themselves.
        var actorId = actor.User.Id;
        query = access.CanManageRepositories
            ? query.Where(r => r.JobId == null || visible.Contains(r.JobId))
            : query.Where(r => visible.Contains(r.JobId) || (r.Type == BackupRunType.Restore && r.ActorUserId == actorId));
        return await query.Take(take).ToListAsync();
    }

    // ══════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════

    /// <summary>
    /// restic retries backend errors (e.g. rejected credentials) with backoff for many
    /// minutes; an action the admin waits for in the UI must give up much earlier.
    /// </summary>
    public static readonly TimeSpan InteractiveTimeout = TimeSpan.FromSeconds(90);

    private static async Task<T> WithInteractiveTimeoutAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(InteractiveTimeout);
        try
        {
            return await action(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ResticException("timeout", "The repository did not answer in time.");
        }
    }

    private static BackupRepository NewRepository(Guid id, BackupRepositoryDraft draft) => new()
    {
        Id = id,
        Name = draft.Name.Trim(),
        Backend = draft.Backend,
        SettingsJson = draft.Settings.Serialize(),
        IsAppendOnly = draft.IsAppendOnly,
        MinFreeSpaceGb = Math.Clamp(draft.MinFreeSpaceGb, 0, 100_000),
        MirrorTargetId = draft.MirrorTargetId is { } mirrorId && mirrorId != Guid.Empty ? mirrorId : null,
    };

    private static void ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
            throw new ResticException("name_invalid", "A name (max. 200 characters) is required.");
    }

    private static bool HasSecrets(ResticBackendSecrets s)
        => new[]
        {
            s.RestUsername, s.RestPassword, s.S3AccessKeyId, s.S3SecretAccessKey, s.B2AccountId, s.B2AccountKey,
            s.AzureAccountKey, s.AzureSasToken, s.GcsCredentialsJson, s.SwiftEnvironment, s.RcloneConfig,
        }.Any(v => !string.IsNullOrEmpty(v));

    /// <summary>Fields left empty in the form keep their stored value.</summary>
    private static ResticBackendSecrets MergeSecrets(ResticBackendSecrets stored, ResticBackendSecrets typed)
    {
        // Azure takes either key or SAS token: typing one replaces the stored pair.
        var azureTyped = !string.IsNullOrEmpty(typed.AzureAccountKey) || !string.IsNullOrEmpty(typed.AzureSasToken);
        return new()
        {
            RestUsername = string.IsNullOrEmpty(typed.RestUsername) ? stored.RestUsername : typed.RestUsername,
            RestPassword = string.IsNullOrEmpty(typed.RestPassword) ? stored.RestPassword : typed.RestPassword,
            S3AccessKeyId = string.IsNullOrEmpty(typed.S3AccessKeyId) ? stored.S3AccessKeyId : typed.S3AccessKeyId,
            S3SecretAccessKey = string.IsNullOrEmpty(typed.S3SecretAccessKey) ? stored.S3SecretAccessKey : typed.S3SecretAccessKey,
            B2AccountId = string.IsNullOrEmpty(typed.B2AccountId) ? stored.B2AccountId : typed.B2AccountId,
            B2AccountKey = string.IsNullOrEmpty(typed.B2AccountKey) ? stored.B2AccountKey : typed.B2AccountKey,
            AzureAccountKey = azureTyped ? typed.AzureAccountKey : stored.AzureAccountKey,
            AzureSasToken = azureTyped ? typed.AzureSasToken : stored.AzureSasToken,
            GcsCredentialsJson = string.IsNullOrEmpty(typed.GcsCredentialsJson) ? stored.GcsCredentialsJson : typed.GcsCredentialsJson,
            SwiftEnvironment = string.IsNullOrEmpty(typed.SwiftEnvironment) ? stored.SwiftEnvironment : typed.SwiftEnvironment,
            RcloneConfig = string.IsNullOrEmpty(typed.RcloneConfig) ? stored.RcloneConfig : typed.RcloneConfig,
        };
    }

}
