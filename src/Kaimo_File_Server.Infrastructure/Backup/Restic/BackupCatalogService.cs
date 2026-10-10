using System.Globalization;
using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

/// <summary>What the current actor may do on the backup page.</summary>
public sealed record BackupAccess(
    bool CanManageRepositories,
    bool CanManageJobs,
    bool JobsUnrestricted,
    IReadOnlySet<Guid> JobShareIds,
    bool RestoreUnrestricted = false,
    IReadOnlySet<Guid>? RestoreShareIds = null)
{
    /// <summary>Holds RestoreFromBackup or RestoreBackupInPlace somewhere.</summary>
    public bool CanRestore => RestoreUnrestricted || RestoreShareIds is { Count: > 0 };
    public bool CanAccessPage => CanManageRepositories || CanManageJobs || CanRestore;
    public static readonly BackupAccess None = new(false, false, false, new HashSet<Guid>());
}

/// <summary>A share with restorable backup points.</summary>
public sealed record BackupRestoreShare(Guid ShareId, string Name, int BackupPointCount, DateTime LatestUtc);

/// <summary>One backup point (restic snapshot) of a share, from the local cache.</summary>
public sealed record BackupPointView(
    Guid RepositoryId, string RepositoryName, string SnapshotId, DateTime TimeUtc, string? JobName, long FileCount, long TotalBytes);

/// <summary>An entry of a folder inside a backup point.</summary>
public sealed record BackupBrowseEntry(string Name, bool IsDirectory, long Size, DateTime? ModifiedUtc);

/// <summary>A single file or folder of a backup point, ready to be streamed (file raw, folder as ZIP).</summary>
public sealed record BackupDownload(string FileName, bool IsDirectory, Func<Stream, CancellationToken, Task> WriteToAsync);

/// <summary>Input of the repository form. Secrets are optional on update (null/empty = keep).</summary>
public sealed record BackupRepositoryDraft(
    string Name,
    BackupBackend Backend,
    ResticRepositorySettings Settings,
    ResticBackendSecrets Secrets,
    bool IsAppendOnly,
    int MinFreeSpaceGb,
    IReadOnlyList<Guid> DepartmentIds);

/// <summary>Input of the job form.</summary>
public sealed record BackupJobDraft(
    Guid? Id,
    string Name,
    Guid RepositoryId,
    bool Enabled,
    bool IncludeRecycleBin,
    IReadOnlyList<Guid> ShareIds,
    IReadOnlyList<TimeOnly> Times,
    IReadOnlyList<DayOfWeek> Days);

/// <summary>Result of probing a repository location before creating it.</summary>
public enum BackupRepositoryProbe
{
    /// <summary>Reachable, no repository yet: can be initialized.</summary>
    Empty,

    /// <summary>A restic repository exists there: connect it with its password.</summary>
    Existing,
}

/// <summary>A repository row plus its department releases and job count (Global view).</summary>
public sealed record BackupRepositoryView(BackupRepository Repository, IReadOnlyList<Guid> DepartmentIds, int JobCount);

/// <summary>A repository as offered in the job form (no settings or secrets).</summary>
public sealed record BackupRepositoryChoice(Guid Id, string Name, BackupBackend Backend);

/// <summary>Active repositories plus, per selectable share, which of them the actor may use for it.</summary>
public sealed record BackupRepositoryChoices(
    IReadOnlyList<BackupRepositoryChoice> Repositories,
    IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> UsableByShare);

/// <summary>A job with the display names of its sources.</summary>
public sealed record BackupJobView(BackupJob Job, string RepositoryName, IReadOnlyList<(Guid ShareId, string Name)> Shares);

/// <summary>
/// Server-side entry point for everything the backup UI does. Every method re-checks the
/// actor's authorization (repositories: Global <see cref="ManagementPermission.ManageBackupRepositories"/>;
/// jobs: <see cref="ManagementPermission.ManageBackupJobs"/> on every source share), so the
/// page's own gating is convenience only.
/// </summary>
public sealed class BackupCatalogService(
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
    //  Repositories (Global)
    // ══════════════════════════════════════════

    public async Task<IReadOnlyList<BackupRepositoryView>> ListRepositoriesAsync(UserContext actor)
    {
        await RequireRepositoriesAsync(actor);
        await using var db = await dbFactory.CreateDbContextAsync();
        var repos = await db.BackupRepositories.AsNoTracking().OrderBy(r => r.Name).ToListAsync();
        var releases = await db.BackupRepositoryDepartments.AsNoTracking().ToListAsync();
        var jobCounts = await db.BackupJobs.AsNoTracking().GroupBy(j => j.RepositoryId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count);
        return repos.Select(r => new BackupRepositoryView(
            r,
            releases.Where(x => x.RepositoryId == r.Id).Select(x => x.DepartmentId).ToList(),
            jobCounts.GetValueOrDefault(r.Id))).ToList();
    }

    /// <summary>Directories local repositories may be created in (shown as a hint in the form).</summary>
    public IReadOnlyList<string> LocalRoots => resolver.LocalRoots;

    /// <summary>
    /// Checks whether the location is reachable and whether a repository already exists
    /// there. Uses a random password: "wrong password" proves an existing repository,
    /// "not found" a reachable but empty location.
    /// </summary>
    public async Task<BackupRepositoryProbe> ProbeAsync(UserContext actor, BackupRepositoryDraft draft, CancellationToken ct = default)
    {
        await RequireRepositoriesAsync(actor);
        var probe = NewRepository(Guid.NewGuid(), draft);
        try
        {
            await WithInteractiveTimeoutAsync(async token =>
            {
                using var target = await resolver.ResolveAsync(probe, draft.Secrets, ResticBackend.GeneratePassword(), token);
                return await restic.CatConfigAsync(target, token);
            }, ct);
            // A random password cannot open a repository, so success is impossible in practice.
            return BackupRepositoryProbe.Existing;
        }
        catch (ResticException ex) when (ex.Code == "wrong_password")
        {
            return BackupRepositoryProbe.Existing;
        }
        catch (ResticException ex) when (ex.Code == "repo_not_found")
        {
            return BackupRepositoryProbe.Empty;
        }
    }

    /// <summary>
    /// Creates a repository: either initializes a new one with a generated password or
    /// connects an existing one with the typed password. Nothing is stored unless restic
    /// confirms. The repository starts in <see cref="BackupRepositoryState.PendingRecoveryKit"/>.
    /// </summary>
    public async Task<BackupRepository> CreateRepositoryAsync(
        UserContext actor, BackupRepositoryDraft draft, string? existingPassword, CancellationToken ct = default)
    {
        await RequireRepositoriesAsync(actor);
        RequireWritable();
        ValidateName(draft.Name);

        var repository = NewRepository(Guid.NewGuid(), draft);
        var initialize = string.IsNullOrEmpty(existingPassword);
        var password = initialize ? ResticBackend.GeneratePassword() : existingPassword!;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.BackupRepositories.AnyAsync(r => r.Name == repository.Name, ct))
            throw new ResticException("name_taken", "A repository with this name already exists.");

        var run = new BackupRun
        {
            RepositoryId = repository.Id,
            Type = BackupRunType.Init,
            Trigger = BackupRunTrigger.Manual,
            ActorUserId = actor.User.Id,
            Status = BackupRunStatus.Running,
            StartedAtUtc = time.GetUtcNow().UtcDateTime,
        };

        repository.ResticRepositoryId = await WithInteractiveTimeoutAsync(async token =>
        {
            using var target = await resolver.ResolveAsync(repository, draft.Secrets, password, token);
            if (repository.Backend == BackupBackend.Local && initialize)
                Directory.CreateDirectory(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(target.RepositoryUrl))!);
            return initialize
                ? await restic.InitAsync(target, token)
                : await restic.CatConfigAsync(target, token);
        }, ct);

        repository.EncryptedPassword = resolver.ProtectPassword(repository.Id, password);
        repository.EncryptedSecrets = HasSecrets(draft.Secrets) ? resolver.ProtectSecrets(repository.Id, draft.Secrets) : null;
        repository.LastReachableAtUtc = time.GetUtcNow().UtcDateTime;

        db.BackupRepositories.Add(repository);
        foreach (var departmentId in draft.DepartmentIds.Distinct())
            db.BackupRepositoryDepartments.Add(new BackupRepositoryDepartment { RepositoryId = repository.Id, DepartmentId = departmentId });
        run.Status = BackupRunStatus.Succeeded;
        run.FinishedAtUtc = time.GetUtcNow().UtcDateTime;
        run.DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { mode = initialize ? "initialized" : "connected" });
        db.BackupRuns.Add(run);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Backup repository {Name} ({Backend}) {Mode} by {User}.",
            repository.Name, repository.Backend, initialize ? "initialized" : "connected", actor.User.Username);
        return repository;
    }

    /// <summary>
    /// Updates name, settings, credentials, flags and department releases. When the target
    /// or its credentials change, restic must confirm it is still the same repository.
    /// </summary>
    public async Task UpdateRepositoryAsync(UserContext actor, Guid repositoryId, BackupRepositoryDraft draft, CancellationToken ct = default)
    {
        await RequireRepositoriesAsync(actor);
        RequireWritable();
        ValidateName(draft.Name);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var repository = await db.BackupRepositories.FirstOrDefaultAsync(r => r.Id == repositoryId, ct)
                         ?? throw new ResticException("repository_missing", "The repository no longer exists.");
        if (await db.BackupRepositories.AnyAsync(r => r.Id != repositoryId && r.Name == draft.Name.Trim(), ct))
            throw new ResticException("name_taken", "A repository with this name already exists.");

        var secrets = MergeSecrets(resolver.ReadSecrets(repository), draft.Secrets);
        var settingsJson = draft.Settings.Serialize();
        var targetChanged = settingsJson != repository.SettingsJson || HasSecrets(draft.Secrets);
        if (targetChanged)
        {
            var candidate = NewRepository(repository.Id, draft);
            var id = await WithInteractiveTimeoutAsync(async token =>
            {
                using var target = await resolver.ResolveAsync(candidate, secrets, resolver.ReadPassword(repository), token);
                return await restic.CatConfigAsync(target, token);
            }, ct);
            if (!string.Equals(id, repository.ResticRepositoryId, StringComparison.Ordinal))
                throw new ResticException("repository_mismatch", "The location holds a different repository.");
            repository.SettingsJson = settingsJson;
            repository.EncryptedSecrets = HasSecrets(secrets) ? resolver.ProtectSecrets(repository.Id, secrets) : null;
            repository.LastReachableAtUtc = time.GetUtcNow().UtcDateTime;
            repository.LastErrorCode = null;
        }

        repository.Name = draft.Name.Trim();
        repository.IsAppendOnly = draft.IsAppendOnly;
        repository.MinFreeSpaceGb = Math.Clamp(draft.MinFreeSpaceGb, 0, 100_000);
        repository.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;

        var existing = await db.BackupRepositoryDepartments.Where(x => x.RepositoryId == repositoryId).ToListAsync(ct);
        var wanted = draft.DepartmentIds.ToHashSet();
        db.BackupRepositoryDepartments.RemoveRange(existing.Where(x => !wanted.Contains(x.DepartmentId)));
        foreach (var id in wanted.Where(id => existing.All(x => x.DepartmentId != id)))
            db.BackupRepositoryDepartments.Add(new BackupRepositoryDepartment { RepositoryId = repositoryId, DepartmentId = id });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Disables or re-enables a repository. Re-enabling requires a confirmed recovery kit.</summary>
    public async Task SetRepositoryEnabledAsync(UserContext actor, Guid repositoryId, bool enabled)
    {
        await RequireRepositoriesAsync(actor);
        RequireWritable();
        await using var db = await dbFactory.CreateDbContextAsync();
        var repository = await db.BackupRepositories.FirstOrDefaultAsync(r => r.Id == repositoryId)
                         ?? throw new ResticException("repository_missing", "The repository no longer exists.");
        repository.State = !enabled
            ? BackupRepositoryState.Disabled
            : repository.KitConfirmedAtUtc is null ? BackupRepositoryState.PendingRecoveryKit : BackupRepositoryState.Active;
        repository.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
    }

    /// <summary>Removes the repository from Kaimo. The data at the target is left untouched.</summary>
    public async Task DeleteRepositoryAsync(UserContext actor, Guid repositoryId)
    {
        await RequireRepositoriesAsync(actor);
        RequireWritable();
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.BackupJobs.AnyAsync(j => j.RepositoryId == repositoryId))
            throw new ResticException("repository_in_use", "Delete the repository's backup jobs first.");
        await db.BackupRepositories.Where(r => r.Id == repositoryId).ExecuteDeleteAsync();
    }

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

    // ══════════════════════════════════════════
    //  Jobs (scoped)
    // ══════════════════════════════════════════

    public async Task<IReadOnlyList<BackupJobView>> ListJobsAsync(UserContext actor)
    {
        var access = await GetAccessAsync(actor);
        if (!access.CanManageJobs)
            return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var jobs = await db.BackupJobs.AsNoTracking().Include(j => j.Sources).OrderBy(j => j.Name).ToListAsync();
        var repoNames = await db.BackupRepositories.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name);
        var shareNames = await db.ShareDefinitions.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name);
        return jobs
            .Where(j => IsJobVisible(access, j))
            .Select(j => new BackupJobView(j, repoNames.GetValueOrDefault(j.RepositoryId) ?? "?",
                j.Sources.Where(s => s.ShareId is not null)
                    .Select(s => (s.ShareId!.Value, shareNames.GetValueOrDefault(s.ShareId!.Value) ?? "?")).ToList()))
            .ToList();
    }

    /// <summary>A scoped admin sees a job only if every source is within their scope.</summary>
    public static bool IsJobVisible(BackupAccess access, BackupJob job)
        => access.CanManageJobs
           && (access.JobsUnrestricted
               || (job.Sources.Count > 0 && job.Sources.All(s => s.Kind == BackupSourceKind.Share
                                                                 && s.ShareId is { } id && access.JobShareIds.Contains(id))));

    /// <summary>Shares the actor may add as job sources. The homes share is Global-only.</summary>
    public async Task<IReadOnlyList<ShareDefinition>> ListSelectableSharesAsync(UserContext actor)
    {
        var access = await GetAccessAsync(actor);
        if (!access.CanManageJobs)
            return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var shares = await db.ShareDefinitions.AsNoTracking().OrderBy(s => s.Name).ToListAsync();
        return shares.Where(s => access.JobsUnrestricted || (!s.IsUserHomes && access.JobShareIds.Contains(s.Id))).ToList();
    }

    /// <summary>
    /// Active repositories a job with these sources may use: everything for a Global job
    /// admin, otherwise repositories released to (an ancestor of) every source share's department.
    /// </summary>
    public async Task<IReadOnlyList<BackupRepository>> ListUsableRepositoriesAsync(UserContext actor, IReadOnlyCollection<Guid> shareIds)
    {
        var access = await GetAccessAsync(actor);
        if (!access.CanManageJobs)
            return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var repos = await db.BackupRepositories.AsNoTracking()
            .Where(r => r.State == BackupRepositoryState.Active).OrderBy(r => r.Name).ToListAsync();
        if (access.JobsUnrestricted)
            return repos;

        var releases = await db.BackupRepositoryDepartments.AsNoTracking().ToListAsync();
        var parents = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.ParentDepartmentId);
        var shareDepartments = await db.ShareDefinitions.AsNoTracking()
            .Where(s => shareIds.Contains(s.Id)).Select(s => s.DepartmentId).Distinct().ToListAsync();
        return repos.Where(r =>
        {
            var released = releases.Where(x => x.RepositoryId == r.Id).Select(x => x.DepartmentId).ToHashSet();
            return shareDepartments.All(d => IsReleasedTo(released, d, parents));
        }).ToList();
    }

    /// <summary>All choices for the job form at once, so the form can filter without round trips.</summary>
    public async Task<BackupRepositoryChoices> GetRepositoryChoicesAsync(UserContext actor)
    {
        var shares = await ListSelectableSharesAsync(actor);
        var access = await GetAccessAsync(actor);
        await using var db = await dbFactory.CreateDbContextAsync();
        var repos = await db.BackupRepositories.AsNoTracking()
            .Where(r => r.State == BackupRepositoryState.Active).OrderBy(r => r.Name)
            .Select(r => new BackupRepositoryChoice(r.Id, r.Name, r.Backend)).ToListAsync();
        var all = repos.Select(r => r.Id).ToHashSet();
        var map = new Dictionary<Guid, IReadOnlySet<Guid>>();
        if (access.JobsUnrestricted)
        {
            foreach (var share in shares)
                map[share.Id] = all;
            return new BackupRepositoryChoices(repos, map);
        }

        var releases = await db.BackupRepositoryDepartments.AsNoTracking().ToListAsync();
        var parents = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.ParentDepartmentId);
        foreach (var share in shares)
            map[share.Id] = repos.Where(r => IsReleasedTo(
                releases.Where(x => x.RepositoryId == r.Id).Select(x => x.DepartmentId).ToHashSet(),
                share.DepartmentId, parents)).Select(r => r.Id).ToHashSet();
        return new BackupRepositoryChoices(repos, map);
    }

    /// <summary>SSH connections (sftp, rsync-ssh) an SFTP repository can reuse (Global).</summary>
    public async Task<IReadOnlyList<(Guid Id, string Name)>> ListSshConnectionsAsync(UserContext actor)
    {
        await RequireRepositoriesAsync(actor);
        await using var db = await dbFactory.CreateDbContextAsync();
        var ids = ResticBackend.SshProviderIds.ToList();
        return (await db.StorageConnections.AsNoTracking()
                .Where(c => ids.Contains(c.ProviderId))
                .OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name }).ToListAsync())
            .Select(c => (c.Id, c.Name)).ToList();
    }

    /// <summary>Released to the department itself, one of its ancestors, or the Global department.</summary>
    public static bool IsReleasedTo(IReadOnlySet<Guid> released, Guid departmentId, IReadOnlyDictionary<Guid, Guid?> parents)
    {
        if (released.Contains(WellKnownGUIDs.DEPARTMENT_GLOBAL))
            return true;
        var seen = new HashSet<Guid>();
        Guid? current = departmentId;
        while (current is { } id && seen.Add(id))
        {
            if (released.Contains(id))
                return true;
            current = parents.GetValueOrDefault(id);
        }
        return false;
    }

    public async Task<BackupJob> SaveJobAsync(UserContext actor, BackupJobDraft draft)
    {
        var access = await RequireJobsAsync(actor);
        RequireWritable();
        ValidateName(draft.Name);

        var shareIds = draft.ShareIds.Distinct().ToList();
        if (shareIds.Count == 0)
            throw new ResticException("sources_required", "Select at least one share.");
        if (draft.Times.Count > MaximumScheduleTimes)
            throw new ResticException("schedule_invalid", "Too many start times.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var shares = await db.ShareDefinitions.AsNoTracking().Where(s => shareIds.Contains(s.Id)).ToListAsync();
        if (shares.Count != shareIds.Count)
            throw new ResticException("share_missing", "A selected share no longer exists.");
        if (!access.JobsUnrestricted)
        {
            if (shares.Any(s => s.IsUserHomes || !access.JobShareIds.Contains(s.Id)))
                throw new UnauthorizedAccessException("A selected share is outside the actor's backup scope.");
            if (shares.Select(s => s.DepartmentId).Distinct().Count() > 1)
                throw new ResticException("sources_multi_department", "Jobs spanning several departments need a global administrator.");
        }

        var usable = await ListUsableRepositoriesAsync(actor, shareIds);

        BackupJob job;
        var now = time.GetUtcNow().UtcDateTime;
        if (draft.Id is { } id)
        {
            job = await db.BackupJobs.Include(j => j.Sources).FirstOrDefaultAsync(j => j.Id == id)
                  ?? throw new ResticException("job_missing", "The backup job no longer exists.");
            if (!IsJobVisible(access, job))
                throw new UnauthorizedAccessException("The job is outside the actor's backup scope.");
            // Keeping the current repository is allowed even if it is no longer active.
            if (job.RepositoryId != draft.RepositoryId && usable.All(r => r.Id != draft.RepositoryId))
                throw new ResticException("repository_not_usable", "The repository is not available for these shares.");
        }
        else
        {
            if (usable.All(r => r.Id != draft.RepositoryId))
                throw new ResticException("repository_not_usable", "The repository is not available for these shares.");
            job = new BackupJob { CreatedByUserId = actor.User.Id, CreatedAtUtc = now, LastScheduledSlotUtc = now };
            db.BackupJobs.Add(job);
        }

        if (await db.BackupJobs.AnyAsync(j => j.Id != job.Id && j.Name == draft.Name.Trim()))
            throw new ResticException("name_taken", "A backup job with this name already exists.");

        // Without start times or without weekdays the job runs manually only; store it as fully empty.
        var manualOnly = draft.Times.Count == 0 || draft.Days.Count == 0;
        var schedule = new BackupSchedule
        {
            Times = manualOnly ? [] : draft.Times.Distinct().Order().ToList(),
            Days = manualOnly ? [] : draft.Days.Distinct().Order().ToList(),
        };
        // A changed schedule starts counting from now: a newly added time that already
        // passed today must not trigger an immediate "catch-up" run.
        if (BackupSchedule.Serialize(schedule) != BackupSchedule.Serialize(job.Schedule) || !job.Enabled && draft.Enabled)
            job.LastScheduledSlotUtc = now;

        job.Name = draft.Name.Trim();
        job.RepositoryId = draft.RepositoryId;
        job.Enabled = draft.Enabled;
        job.IncludeRecycleBin = draft.IncludeRecycleBin;
        job.ContentLevel = BackupContentLevel.Files;
        job.Schedule = schedule;
        job.UpdatedAtUtc = now;

        var removed = job.Sources.Where(s => s.ShareId is null || !shareIds.Contains(s.ShareId.Value)).ToList();
        foreach (var source in removed)
            job.Sources.Remove(source);
        db.BackupJobSources.RemoveRange(removed);
        foreach (var shareId in shareIds.Where(sid => job.Sources.All(s => s.ShareId != sid)))
            job.Sources.Add(new BackupJobSource { JobId = job.Id, Kind = BackupSourceKind.Share, ShareId = shareId });

        await db.SaveChangesAsync();
        return job;
    }

    public async Task DeleteJobAsync(UserContext actor, Guid jobId)
    {
        var access = await RequireJobsAsync(actor);
        RequireWritable();
        await using var db = await dbFactory.CreateDbContextAsync();
        var job = await db.BackupJobs.Include(j => j.Sources).FirstOrDefaultAsync(j => j.Id == jobId);
        if (job is null)
            return;
        if (!IsJobVisible(access, job))
            throw new UnauthorizedAccessException("The job is outside the actor's backup scope.");
        db.BackupJobs.Remove(job);
        await db.SaveChangesAsync();
    }

    public async Task<Guid> RunJobNowAsync(UserContext actor, Guid jobId)
    {
        var access = await RequireJobsAsync(actor);
        RequireWritable();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var job = await db.BackupJobs.AsNoTracking().Include(j => j.Sources).FirstOrDefaultAsync(j => j.Id == jobId)
                      ?? throw new ResticException("job_missing", "The backup job no longer exists.");
            if (!IsJobVisible(access, job))
                throw new UnauthorizedAccessException("The job is outside the actor's backup scope.");
            var state = await db.BackupRepositories.Where(r => r.Id == job.RepositoryId).Select(r => r.State).FirstAsync();
            if (state != BackupRepositoryState.Active)
                throw new ResticException("repository_not_active", "The repository is not active.");
        }
        return await runner.EnqueueJobAsync(jobId, BackupRunTrigger.Manual, actor.User.Id);
    }

    /// <summary>Cancels a queued/running run of a job the actor can see, or a restore the actor started.</summary>
    public async Task<bool> CancelRunAsync(UserContext actor, Guid runId)
    {
        var access = await GetAccessAsync(actor);
        var run = runner.Runs.FirstOrDefault(r => r.RunId == runId);
        if (run is null)
            return false;
        await using var db = await dbFactory.CreateDbContextAsync();
        if (run.IsRestore)
        {
            var starter = await db.BackupRuns.AsNoTracking().Where(r => r.Id == runId).Select(r => r.ActorUserId).FirstOrDefaultAsync();
            return access.CanRestore && (access.RestoreUnrestricted || starter == actor.User.Id) && runner.Cancel(runId);
        }
        if (!access.CanManageJobs)
            return false;
        var job = await db.BackupJobs.AsNoTracking().Include(j => j.Sources).FirstOrDefaultAsync(j => j.Id == run.JobId);
        return job is not null && IsJobVisible(access, job) && runner.Cancel(runId);
    }

    // ══════════════════════════════════════════
    //  Restore (scoped: RestoreFromBackup / RestoreBackupInPlace per share)
    // ══════════════════════════════════════════

    /// <summary>The homes share is Global-only, like its backup jobs.</summary>
    private static bool CanSeeRestoreShare(BackupAccess access, ShareDefinition share)
        => access.RestoreUnrestricted || (!share.IsUserHomes && access.RestoreShareIds?.Contains(share.Id) == true);

    private async Task<bool> HasShareBitAsync(UserContext actor, ShareDefinition share, ManagementPermission bit)
    {
        var scope = await auth.GetAuthorizedShareIdsAsync(actor, bit);
        return scope.IsUnrestricted || (!share.IsUserHomes && scope.ScopeIds.Contains(share.Id));
    }

    /// <summary>Shares with cached backup points the actor may restore from.</summary>
    public async Task<IReadOnlyList<BackupRestoreShare>> ListRestorableSharesAsync(UserContext actor)
    {
        var access = await GetAccessAsync(actor);
        if (!access.CanRestore)
            return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var points = (await db.BackupSnapshots.AsNoTracking()
                .Where(s => s.Kind == BackupSnapshotKind.Share && s.ShareId != null)
                .Select(s => new { ShareId = s.ShareId!.Value, s.TimeUtc })
                .ToListAsync())
            .GroupBy(s => s.ShareId)
            .ToDictionary(g => g.Key, g => (Count: g.Count(), Latest: g.Max(s => s.TimeUtc)));
        var ids = points.Keys.ToList();
        var shares = await db.ShareDefinitions.AsNoTracking().Where(s => ids.Contains(s.Id)).ToListAsync();
        return shares.Where(s => CanSeeRestoreShare(access, s))
            .Select(s => new BackupRestoreShare(s.Id, s.Name, points[s.Id].Count, points[s.Id].Latest))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>All backup points of a share, newest first.</summary>
    public async Task<IReadOnlyList<BackupPointView>> ListBackupPointsAsync(UserContext actor, Guid shareId)
    {
        var access = await GetAccessAsync(actor);
        await using var db = await dbFactory.CreateDbContextAsync();
        var share = await db.ShareDefinitions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == shareId)
                    ?? throw new ResticException("share_missing", "The share no longer exists.");
        if (!CanSeeRestoreShare(access, share))
            throw new UnauthorizedAccessException("The share is outside the actor's restore scope.");
        var snapshots = (await db.BackupSnapshots.AsNoTracking()
                .Where(s => s.ShareId == shareId && s.Kind == BackupSnapshotKind.Share)
                .ToListAsync())
            .OrderByDescending(s => s.TimeUtc).ToList();
        var repoNames = await db.BackupRepositories.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name);
        var jobNames = await db.BackupJobs.AsNoTracking().ToDictionaryAsync(j => j.Id, j => j.Name);
        return snapshots.Select(s => new BackupPointView(
            s.RepositoryId, repoNames.GetValueOrDefault(s.RepositoryId) ?? "?", s.SnapshotId, s.TimeUtc,
            s.JobId is { } jobId ? jobNames.GetValueOrDefault(jobId) : null, s.FileCount, s.TotalBytes)).ToList();
    }

    /// <summary>Which restore bits the actor holds on a share (drives the restore dialog; enforced on enqueue).</summary>
    public async Task<(bool FromBackup, bool InPlace)> GetRestoreRightsAsync(UserContext actor, Guid shareId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var share = await db.ShareDefinitions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == shareId);
        if (share is null)
            return (false, false);
        return (await HasShareBitAsync(actor, share, ManagementPermission.RestoreFromBackup),
            await HasShareBitAsync(actor, share, ManagementPermission.RestoreBackupInPlace));
    }

    /// <summary>Restore targets for "into another share": shares with RestoreBackupInPlace.</summary>
    public async Task<IReadOnlyList<ShareDefinition>> ListRestoreTargetSharesAsync(UserContext actor)
    {
        var scope = await auth.GetAuthorizedShareIdsAsync(actor, ManagementPermission.RestoreBackupInPlace);
        if (!scope.IsUnrestricted && scope.ScopeIds.Count == 0)
            return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var shares = await db.ShareDefinitions.AsNoTracking().OrderBy(s => s.Name).ToListAsync();
        return shares.Where(s => scope.IsUnrestricted || (!s.IsUserHomes && scope.ScopeIds.Contains(s.Id))).ToList();
    }

    /// <summary>Loads a share backup point the actor may see; optionally requires a restore bit on its share.</summary>
    private async Task<(BackupSnapshot Snapshot, ShareDefinition Share, BackupRepository Repository)> AuthorizeBackupPointAsync(
        UserContext actor, Guid repositoryId, string snapshotId, ManagementPermission? required, CancellationToken ct)
    {
        var access = await GetAccessAsync(actor);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var snapshot = await db.BackupSnapshots.AsNoTracking()
                           .FirstOrDefaultAsync(s => s.RepositoryId == repositoryId && s.SnapshotId == snapshotId, ct)
                       ?? throw new ResticException("snapshot_missing", "The backup point no longer exists.");
        var share = snapshot.Kind == BackupSnapshotKind.Share && snapshot.ShareId is { } shareId
            ? await db.ShareDefinitions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == shareId, ct)
            : null;
        if (share is null || !CanSeeRestoreShare(access, share)
                          || required is { } bit && !await HasShareBitAsync(actor, share, bit))
            throw new UnauthorizedAccessException("The backup point is outside the actor's restore scope.");
        var repository = await db.BackupRepositories.AsNoTracking().FirstAsync(r => r.Id == repositoryId, ct);
        return (snapshot, share, repository);
    }

    private static string RequireFolder(string? folder, int rootDepth = 0)
        => ShareRelativePath.TryNormalizeStrict(folder ?? string.Empty, out var normalized, rootDepth: rootDepth)
            ? normalized
            : throw new ResticException("path_invalid", "The path is not a valid share path.");

    /// <summary>Lists one folder of a backup point (reads the repository, interactive timeout).</summary>
    public async Task<IReadOnlyList<BackupBrowseEntry>> BrowseAsync(
        UserContext actor, Guid repositoryId, string snapshotId, string folder, CancellationToken ct = default)
    {
        var (snapshot, _, repository) = await AuthorizeBackupPointAsync(actor, repositoryId, snapshotId, null, ct);
        var root = BackupRestoreExecutor.SourceRoot(snapshot)
                   ?? throw new ResticException("snapshot_missing", "The backup point records no share folder.");
        var path = BackupRestoreExecutor.SnapshotPath(root, RequireFolder(folder));
        var nodes = await WithInteractiveTimeoutAsync(async token =>
        {
            using var target = await resolver.ResolveAsync(repository, token);
            return await restic.ListDirectoryAsync(target, snapshotId, path, token);
        }, ct);
        return nodes
            .Where(n => !n.Name.StartsWith(ShareEntryPolicy.InternalNamespacePrefix, StringComparison.Ordinal))
            .OrderByDescending(n => n.IsDirectory).ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(n => new BackupBrowseEntry(n.Name, n.IsDirectory, n.Size, n.ModifiedUtc))
            .ToList();
    }

    /// <summary>
    /// Authorizes and queues a restore. New folder needs RestoreFromBackup on the source share;
    /// in place RestoreBackupInPlace; another share RestoreFromBackup on the source and
    /// RestoreBackupInPlace on the target; restoring permissions RestoreBackupInPlace on the
    /// destination.
    /// </summary>
    public async Task<Guid> EnqueueRestoreAsync(UserContext actor, BackupRestoreRequest request, CancellationToken ct = default)
    {
        RequireWritable();
        if (request.Mode is not (BackupRestoreMode.NewFolder or BackupRestoreMode.InPlace or BackupRestoreMode.OtherShare))
            throw new ResticException("request_invalid", "Unknown restore mode.");
        var sourceBit = request.Mode == BackupRestoreMode.InPlace
            ? ManagementPermission.RestoreBackupInPlace
            : ManagementPermission.RestoreFromBackup;
        var (_, share, _) = await AuthorizeBackupPointAsync(actor, request.RepositoryId, request.SnapshotId, sourceBit, ct);
        if (request.ShareId != share.Id)
            throw new ResticException("snapshot_missing", "The backup point does not belong to the share.");

        var folder = RequireFolder(request.FolderPath);
        var items = request.Items.Distinct(StringComparer.Ordinal).ToList();
        if (items.Any(i => string.IsNullOrEmpty(i) || i.Contains('/') || i.Contains('\\') || i is "." or ".."
                           || i.StartsWith(ShareEntryPolicy.InternalNamespacePrefix, StringComparison.Ordinal)))
            throw new ResticException("path_invalid", "Only entries of the selected folder can be restored.");

        var destination = share;
        string? targetFolder = null;
        if (request.Mode == BackupRestoreMode.OtherShare)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            destination = await db.ShareDefinitions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.TargetShareId, ct)
                          ?? throw new ResticException("share_missing", "The target share no longer exists.");
            if (!await HasShareBitAsync(actor, destination, ManagementPermission.RestoreBackupInPlace))
                throw new UnauthorizedAccessException("The target share is outside the actor's restore scope.");
            targetFolder = RequireFolder(request.TargetFolder, destination.RecycleRootDepth);
            if (targetFolder.Length > 0 && ShareEntryPolicy.IsReservedForUserWrites(targetFolder, destination.RecycleRootDepth))
                throw new ResticException("path_invalid", "The restore target lies in a folder reserved by Kaimo.");
            if (!Directory.Exists(ShareRelativePath.ToContainedAbsolutePath(destination.Path, targetFolder, allowInternalNamespace: false)))
                throw new ResticException("target_folder_missing", "The target folder does not exist.");
        }
        if (request.RestorePermissions && !await HasShareBitAsync(actor, destination, ManagementPermission.RestoreBackupInPlace))
            throw new UnauthorizedAccessException("Restoring permissions needs RestoreBackupInPlace on the destination.");

        var normalized = request with
        {
            FolderPath = folder,
            Items = items,
            Conflict = request.Mode == BackupRestoreMode.InPlace ? request.Conflict : BackupRestoreConflict.Skip,
            TargetShareId = request.Mode == BackupRestoreMode.OtherShare ? destination.Id : null,
            TargetFolder = targetFolder,
            // The circuit's UI language; the background run has none of its own.
            Culture = CultureInfo.CurrentUICulture.Name,
        };
        var title = string.Format(CultureInfo.CurrentCulture,
            Resources.ResourceManager.GetString("Web_Backup_Restore_JobTitle") ?? "Restore {0}", share.Name);
        logger.LogInformation("Restore from backup point {Snapshot} of share {Share} ({Mode}) requested by {User}.",
            request.SnapshotId, share.Name, request.Mode, actor.User.Username);
        return await runner.EnqueueRestoreAsync(normalized, title, actor.User.Id, ct);
    }

    /// <summary>
    /// Authorizes a download (RestoreFromBackup) of one file or folder and returns a writer that
    /// streams it straight from the repository. Every download is recorded as a restore run.
    /// </summary>
    public async Task<BackupDownload> OpenDownloadAsync(
        UserContext actor, Guid repositoryId, string snapshotId, string path, CancellationToken ct = default)
    {
        if (demo.ReadOnly)
            throw new ReadOnlyDemoException();
        var (snapshot, share, repository) = await AuthorizeBackupPointAsync(
            actor, repositoryId, snapshotId, ManagementPermission.RestoreFromBackup, ct);
        var relative = RequireFolder(path);
        if (relative.Length == 0)
            throw new ResticException("path_invalid", "Select a file or folder.");
        var root = BackupRestoreExecutor.SourceRoot(snapshot)
                   ?? throw new ResticException("snapshot_missing", "The backup point records no share folder.");
        var parent = ShareRelativePath.GetParent(relative);
        var name = ShareRelativePath.GetFileName(relative);
        var node = (await BrowseAsync(actor, repositoryId, snapshotId, parent, ct)).FirstOrDefault(e => e.Name == name)
                   ?? throw new ResticException("item_missing", "The entry does not exist in the backup point.");
        var absolute = BackupRestoreExecutor.SnapshotPath(root, relative);
        var details = new BackupRestoreDetails(
            new BackupRestoreRequest(repository.Id, snapshotId, share.Id, parent, [name], BackupRestoreMode.Download, BackupRestoreConflict.Skip, false),
            new BackupRestoreOutcome { SourceShareName = share.Name, DestinationPath = relative });

        return new BackupDownload(node.IsDirectory ? name + ".zip" : name, node.IsDirectory, async (output, token) =>
        {
            var run = new BackupRun
            {
                RepositoryId = repository.Id,
                Type = BackupRunType.Restore,
                Trigger = BackupRunTrigger.Manual,
                ActorUserId = actor.User.Id,
                Status = BackupRunStatus.Running,
                QueuedAtUtc = time.GetUtcNow().UtcDateTime,
                StartedAtUtc = time.GetUtcNow().UtcDateTime,
                DetailsJson = details.Serialize(),
            };
            await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
            {
                db.BackupRuns.Add(run);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            try
            {
                using var target = await resolver.ResolveAsync(repository, token);
                await restic.DumpAsync(target, snapshotId, absolute, node.IsDirectory, output, token);
                run.Status = BackupRunStatus.Succeeded;
            }
            catch (Exception ex)
            {
                run.Status = token.IsCancellationRequested ? BackupRunStatus.Cancelled : BackupRunStatus.Failed;
                run.ErrorCode = ex is ResticException re ? re.Code : token.IsCancellationRequested ? "cancelled" : "unexpected_error";
                throw;
            }
            finally
            {
                run.FinishedAtUtc = time.GetUtcNow().UtcDateTime;
                await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
                db.BackupRuns.Update(run);
                await db.SaveChangesAsync(CancellationToken.None);
            }
        });
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
    };

    private static void ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
            throw new ResticException("name_invalid", "A name (max. 200 characters) is required.");
    }

    private static bool HasSecrets(ResticBackendSecrets s)
        => !string.IsNullOrEmpty(s.RestUsername) || !string.IsNullOrEmpty(s.RestPassword)
           || !string.IsNullOrEmpty(s.S3AccessKeyId) || !string.IsNullOrEmpty(s.S3SecretAccessKey);

    /// <summary>Fields left empty in the form keep their stored value.</summary>
    private static ResticBackendSecrets MergeSecrets(ResticBackendSecrets stored, ResticBackendSecrets typed) => new()
    {
        RestUsername = string.IsNullOrEmpty(typed.RestUsername) ? stored.RestUsername : typed.RestUsername,
        RestPassword = string.IsNullOrEmpty(typed.RestPassword) ? stored.RestPassword : typed.RestPassword,
        S3AccessKeyId = string.IsNullOrEmpty(typed.S3AccessKeyId) ? stored.S3AccessKeyId : typed.S3AccessKeyId,
        S3SecretAccessKey = string.IsNullOrEmpty(typed.S3SecretAccessKey) ? stored.S3SecretAccessKey : typed.S3SecretAccessKey,
    };

    // Fallback only; the localized template lives in the resource files.
    private const string DefaultKitTemplate = "Kaimo recovery kit\n\nRepository: {0} ({1})\nRESTIC_REPOSITORY={2}\n{3}\nRESTIC_PASSWORD={4}\nRepository id: {5}\nrestic: {6}\nCreated: {7}\nKit code: {8}\n";
}
