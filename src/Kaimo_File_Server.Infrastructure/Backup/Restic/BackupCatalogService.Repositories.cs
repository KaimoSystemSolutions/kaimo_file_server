using System.Globalization;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

public partial class BackupCatalogService
{
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
        ApplyMaintenance(repository, draft, time.GetUtcNow().UtcDateTime);
        await ValidateMirrorAsync(db, repository, draft.Secrets, ct);

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
        ApplyMaintenance(repository, draft, repository.UpdatedAtUtc);
        await ValidateMirrorAsync(db, repository, secrets, ct);

        var existing = await db.BackupRepositoryDepartments.Where(x => x.RepositoryId == repositoryId).ToListAsync(ct);
        var wanted = draft.DepartmentIds.ToHashSet();
        db.BackupRepositoryDepartments.RemoveRange(existing.Where(x => !wanted.Contains(x.DepartmentId)));
        foreach (var id in wanted.Where(id => existing.All(x => x.DepartmentId != id)))
            db.BackupRepositoryDepartments.Add(new BackupRepositoryDepartment { RepositoryId = repositoryId, DepartmentId = id });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Maintenance settings of the form. A changed schedule starts counting from now, so a
    /// slot that already passed today does not trigger an immediate catch-up run.
    /// </summary>
    private static void ApplyMaintenance(BackupRepository repository, BackupRepositoryDraft draft, DateTime nowUtc)
    {
        var schedule = NormalizeSchedule(draft.MaintenanceSchedule?.Times ?? [], draft.MaintenanceSchedule?.Days ?? []);
        if (BackupSchedule.Serialize(schedule) != BackupSchedule.Serialize(repository.MaintenanceSchedule) || repository.LastMaintenanceSlotUtc is null)
            repository.LastMaintenanceSlotUtc = nowUtc;
        repository.MaintenanceSchedule = schedule;
        repository.ReadDataMonthly = draft.ReadDataMonthly;
        repository.TestRestoreSampleSize = Math.Clamp(draft.TestRestoreSampleSize, 0, MaximumTestRestoreSample);
        repository.MirrorTargetId = draft.MirrorTargetId is { } id && id != Guid.Empty ? id : null;
    }

    /// <summary>
    /// The mirror must exist, must not lead back to this repository (a copy cycle) and must be
    /// servable by one <c>restic copy</c> call (see <see cref="ResticBackend.ValidateMirror"/>).
    /// </summary>
    private async Task ValidateMirrorAsync(ApplicationDbContext db, BackupRepository repository, ResticBackendSecrets secrets, CancellationToken ct)
    {
        if (repository.MirrorTargetId is not { } mirrorId)
            return;
        var all = await db.BackupRepositories.AsNoTracking().ToDictionaryAsync(r => r.Id, ct);
        if (!all.TryGetValue(mirrorId, out var mirror))
            throw new ResticException("repository_missing", "The mirror repository no longer exists.");
        if (LeadsBackTo(all, mirrorId, repository.Id))
            throw new ResticException("mirror_invalid", "The mirror would copy back into this repository.");
        ResticBackend.ValidateMirror(repository, secrets, mirror, resolver.ReadSecrets(mirror));
    }

    /// <summary>True when following the mirror chain from <paramref name="startId"/> reaches <paramref name="repositoryId"/> (a copy cycle).</summary>
    private static bool LeadsBackTo(IReadOnlyDictionary<Guid, BackupRepository> all, Guid startId, Guid repositoryId)
    {
        var seen = new HashSet<Guid>();
        for (Guid? next = startId; next is { } id && seen.Add(id); next = all.GetValueOrDefault(id)?.MirrorTargetId)
        {
            if (id == repositoryId)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Repositories the repository being edited (<paramref name="repositoryId"/>, null while
    /// creating) can mirror into with the form's current settings: active, no copy cycle, and
    /// servable by one <c>restic copy</c> call. Empty secret fields mean "keep the stored ones".
    /// The current mirror stays listed even if it is no longer active, so the form can show it.
    /// </summary>
    public async Task<IReadOnlyList<BackupRepositoryChoice>> ListMirrorCandidatesAsync(
        UserContext actor, Guid? repositoryId, BackupRepositoryDraft draft, CancellationToken ct = default)
    {
        await RequireRepositoriesAsync(actor);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var all = await db.BackupRepositories.AsNoTracking().ToDictionaryAsync(r => r.Id, ct);
        var stored = repositoryId is { } id ? all.GetValueOrDefault(id) : null;
        var source = NewRepository(stored?.Id ?? Guid.NewGuid(), draft);
        var secrets = stored is null ? draft.Secrets : MergeSecrets(resolver.ReadSecrets(stored), draft.Secrets);

        bool Compatible(BackupRepository mirror)
        {
            try
            {
                ResticBackend.ValidateMirror(source, secrets, mirror, resolver.ReadSecrets(mirror));
                return true;
            }
            catch (ResticException)
            {
                return false;
            }
        }

        return all.Values
            .Where(m => m.Id != source.Id
                        && (m.State == BackupRepositoryState.Active || m.Id == stored?.MirrorTargetId)
                        && !LeadsBackTo(all, m.Id, source.Id)
                        && Compatible(m))
            .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(m => new BackupRepositoryChoice(m.Id, m.Name, m.Backend))
            .ToList();
    }

    /// <summary>Queues the maintenance of a repository now (Global).</summary>
    public async Task<Guid> RunMaintenanceNowAsync(UserContext actor, Guid repositoryId)
    {
        await RequireRepositoriesAsync(actor);
        RequireWritable();
        await using var db = await dbFactory.CreateDbContextAsync();
        var repository = await db.BackupRepositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == repositoryId)
                         ?? throw new ResticException("repository_missing", "The repository no longer exists.");
        if (repository.State != BackupRepositoryState.Active)
            throw new ResticException("repository_not_active", "The repository is not active.");
        var title = string.Format(CultureInfo.CurrentCulture,
            Resources.ResourceManager.GetString("Web_Backup_Maintenance_JobTitle") ?? "Maintenance {0}", repository.Name);
        return await runner.EnqueueMaintenanceAsync(repositoryId, title, BackupRunTrigger.Manual, actor.User.Id);
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
}
