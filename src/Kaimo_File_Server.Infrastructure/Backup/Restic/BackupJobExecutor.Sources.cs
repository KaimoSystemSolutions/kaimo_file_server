using System.Reflection;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

public partial class BackupJobExecutor
{
    private async Task<BackupSourceOutcome> BackupShareAsync(
        ApplicationDbContext db, ResticTarget target, BackupRepository repository, BackupJob job, BackupRun run,
        ShareDefinition? share, Guid shareId, Action<double> progress, CancellationToken ct)
    {
        if (share is null)
            return new(shareId, shareId.ToString(), null, "share_missing", 0, []);

        string? metaDir = null;
        try
        {
            GuardSource(share.Path, await HadContentBeforeAsync(db, repository.Id, share.Id, ct));

            metaDir = Path.Combine(MetaRoot, "share-" + share.Id.ToString("N"));
            CreatePrivateDirectory(metaDir);
            var manifest = Path.Combine(metaDir, BackupAclManifest.FileName);
            progress(0);
            await BackupAclManifest.WriteAsync(db, share, manifest, time.GetUtcNow().UtcDateTime, ct);

            var request = new ResticBackupRequest(
                [Path.TrimEndingDirectorySeparator(share.Path), manifest],
                [
                    "kaimo", "job:" + job.Id.ToString("N"), "run:" + run.Id.ToString("N"),
                    "level:" + LevelTag(job.ContentLevel), "share:" + share.Id.ToString("N"),
                ],
                BuildShareExcludes(share, job.IncludeRecycleBin));

            var result = await RunBackupAsync(target, run, request, progress, ct);
            return new(share.Id, share.Name, result.SnapshotId, null, result.WarningCount, result.Warnings);
        }
        catch (ResticException ex)
        {
            logger.LogWarning("Backup of share {Share} in run {RunId} failed: {Code}.", share.Name, run.Id, ex.Code);
            if (AbortsRun(ex))
                throw;
            return new(share.Id, share.Name, null, ex.Code, 0, []);
        }
        finally
        {
            if (metaDir is not null)
                TryDeleteDirectory(metaDir);
        }
    }

    private async Task<ResticBackupResult> RunBackupAsync(
        ResticTarget target, BackupRun run, ResticBackupRequest request, Action<double> progress, CancellationToken ct)
    {
        var result = await restic.BackupAsync(target, request, p => progress(Math.Clamp(p.PercentDone, 0, 1)), ct);
        run.FilesNew += result.FilesNew;
        run.FilesChanged += result.FilesChanged;
        run.FilesUnmodified += result.FilesUnmodified;
        run.BytesAdded += result.BytesAdded;
        run.BytesProcessed += result.BytesProcessed;
        if (result.HasWarnings && !string.IsNullOrWhiteSpace(result.StderrTail))
            run.LogExcerpt = Tail((run.LogExcerpt + "\n" + result.StderrTail).Trim());
        return result;
    }

    /// <summary>Repository-level failures abort the whole run; source-level ones only that source.</summary>
    private static bool AbortsRun(ResticException ex)
        => ex.Code is "repo_unreachable" or "repo_not_found" or "wrong_password" or "lock_failed"
            or "access_denied" or "tls_error" or "restic_unavailable" or "insufficient_space";

    /// <summary>
    /// A whole pool (Global-only): every share folder in it plus folders that are no share, and
    /// one ACL manifest per share of the pool. Kaimo-internal entries are excluded like for a
    /// share; the version store only when the job asks for it.
    /// </summary>
    private async Task<BackupSourceOutcome> BackupPoolAsync(
        ApplicationDbContext db, ResticTarget target, BackupRepository repository, BackupJob job, BackupRun run,
        string pool, Action<double> progress, CancellationToken ct)
    {
        var name = PoolTag(pool);
        string? metaDir = null;
        try
        {
            var shares = (await db.ShareDefinitions.AsNoTracking().ToListAsync(ct))
                .Where(s => IsInside(s.Path, pool)).ToList();
            // Every pool backup point holds one manifest file per share besides the pool's files.
            GuardSource(pool, await db.BackupSnapshots.AnyAsync(
                s => s.RepositoryId == repository.Id && s.PoolPath == name && s.FileCount > shares.Count, ct));

            metaDir = Path.Combine(MetaRoot, "pool-" + PathHash(pool));
            CreatePrivateDirectory(metaDir);
            progress(0);
            var now = time.GetUtcNow().UtcDateTime;
            foreach (var share in shares)
                await BackupAclManifest.WriteAsync(db, share, Path.Combine(metaDir, share.Id.ToString("N") + "-" + BackupAclManifest.FileName), now, ct);

            var request = new ResticBackupRequest(
                [Path.TrimEndingDirectorySeparator(pool), metaDir],
                [
                    "kaimo", "job:" + job.Id.ToString("N"), "run:" + run.Id.ToString("N"),
                    "level:" + LevelTag(job.ContentLevel), "pool:" + name,
                ],
                BuildPoolExcludes(pool, shares, job.IncludeRecycleBin, IncludesVersions(job)));
            var result = await RunBackupAsync(target, run, request, progress, ct);
            return new(null, name, result.SnapshotId, null, result.WarningCount, result.Warnings);
        }
        catch (ResticException ex)
        {
            logger.LogWarning("Backup of pool {Pool} in run {RunId} failed: {Code}.", name, run.Id, ex.Code);
            if (AbortsRun(ex))
                throw;
            return new(null, name, null, ex.Code, 0, []);
        }
        finally
        {
            if (metaDir is not null)
                TryDeleteDirectory(metaDir);
        }
    }

    /// <summary>
    /// The "system" backup point: database dump and run info, for Full also the application
    /// data (DataProtection keys, certificates) and an external DataProtection certificate.
    /// </summary>
    private async Task<BackupSourceOutcome> BackupSystemAsync(
        ApplicationDbContext db, ResticTarget target, BackupJob job, BackupRun run, string systemDir,
        Action<double> progress, CancellationToken ct)
    {
        try
        {
            var paths = new List<string> { systemDir };
            var excludes = new List<string>();
            if (job.ContentLevel == BackupContentLevel.Full)
            {
                var applicationData = Path.TrimEndingDirectorySeparator(resolver.ApplicationDataPath);
                if (!Directory.Exists(applicationData))
                    throw new ResticException("source_unavailable", "The application data folder does not exist.");
                paths.Add(applicationData);
                // Caches and temp files (restic cache, SMB snapshot cache, ...) are no recovery data.
                excludes.Add(applicationData.Replace('\\', '/') + "/" + ShareEntryPolicy.InternalNamespacePrefix + "*");
                excludes.Add(ResticClient.TrimDirectory(restic.CacheDirectory));
                excludes.Add(ResticClient.TrimDirectory(resolver.TempRoot));
                var certificate = configuration["DataProtection:CertificatePath"];
                if (!string.IsNullOrWhiteSpace(certificate) && File.Exists(certificate) && !IsInside(certificate, applicationData))
                    paths.Add(certificate);
            }

            await WriteBackupInfoAsync(db, job, run, Path.Combine(systemDir, BackupInfoFileName), ct);
            var request = new ResticBackupRequest(
                paths,
                [
                    "kaimo", "job:" + job.Id.ToString("N"), "run:" + run.Id.ToString("N"),
                    "level:" + LevelTag(job.ContentLevel), "kind:system",
                ],
                excludes);
            var result = await RunBackupAsync(target, run, request, progress, ct);
            return new(null, SystemSourceKey, result.SnapshotId, null, result.WarningCount, result.Warnings);
        }
        catch (ResticException ex)
        {
            logger.LogWarning("System backup in run {RunId} failed: {Code}.", run.Id, ex.Code);
            if (AbortsRun(ex))
                throw;
            return new(null, SystemSourceKey, null, ex.Code, 0, []);
        }
    }

    /// <summary>What a disaster recovery needs to know besides the dump: where things lived.</summary>
    private async Task WriteBackupInfoAsync(ApplicationDbContext db, BackupJob job, BackupRun run, string path, CancellationToken ct)
    {
        var shares = await db.ShareDefinitions.AsNoTracking().OrderBy(s => s.Name)
            .Select(s => new { id = s.Id, name = s.Name, path = s.Path, isUserHomes = s.IsUserHomes }).ToListAsync(ct);
        var dump = new FileInfo(Path.Combine(Path.GetDirectoryName(path)!, DatabaseDumpFileName));
        var info = new
        {
            formatVersion = 1,
            createdAtUtc = time.GetUtcNow().UtcDateTime,
            kaimoVersion = typeof(BackupJobExecutor).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            jobId = job.Id,
            jobName = job.Name,
            runId = run.Id,
            contentLevel = LevelTag(job.ContentLevel),
            database = new { file = DatabaseDumpFileName, format = "pg_dump custom", sizeBytes = dump.Exists ? dump.Length : 0 },
            applicationDataPath = resolver.ApplicationDataPath,
            dataProtectionCertificatePath = configuration["DataProtection:CertificatePath"],
            pools = resolver.Pools,
            shares,
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }), ct);
    }
}
